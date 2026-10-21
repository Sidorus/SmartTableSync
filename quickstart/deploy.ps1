<#
.SYNOPSIS
    Sets up SmartTableSync between a parent and a child Azure SQL database that already exist:
    the Function, its identities, the database users and grants, and the first sync.

.DESCRIPTION
    Default (setup):
      1. Checks both databases and shows which strategy each table will get.
      2. Terraform creates the Azure side only: resource group, the runtime identity (and in
         Auto mode the provisioning identity), the Function App with its storage, and
         Application Insights. Your databases and servers are never created or changed by Terraform.
      3. Parent: creates the smarttablesync-runtime database user and grants it read access to
         the listed tables only (not with your own connection strings). -EnableChangeTracking
         also turns Change Tracking on for them.
      4. Child: Auto mode creates the database users and grants (not with your own connection
         strings), and the Function creates the tables itself. Manual mode applies the scaffold
         tool's scripts, then grants.
      5. Publishes the Function from source, starts the first sync and waits for it.

    Every SQL script it runs is written to quickstart/generated/<suffix>/ first, so you can read
    what was applied. Nothing is ever deleted from your databases.

    -DryRun    only checks the databases, writes the SQL scripts and shows Terraform's plan.
    -SyncNow   starts a sync right away and waits for it, instead of waiting for the timer.
    -Destroy   removes the Azure resources and prints the SQL to remove the database users.

    Two ways to give the databases:
      -ParentServer/-ParentDatabase/-ChildDatabase  the Function signs in with new managed
          identities; this script creates their database users and grants, running SQL as you
          (Microsoft Entra), so your account must be able to create users in both databases.
      -ParentConnectionString/-ChildConnectionString  your own connection strings: saved as the
          Function's app settings exactly as given, and used by this script for its checks. No
          identities, users or grants are created; their logins must already have the rights.

    Uses the subscription and account you're signed in with in Azure CLI (az login,
    az account set) for the Azure resources. Your IP must be allowed through the SQL firewalls.

.EXAMPLE
    ./quickstart/deploy.ps1 -Suffix acme1 -ParentServer myserver.database.windows.net -ParentDatabase SalesDb -ChildDatabase SalesCopy -Tables "Product,Customer,sales.Order"

.EXAMPLE
    ./quickstart/deploy.ps1 -Suffix acme1 -ParentConnectionString $env:PARENT_DB -ChildConnectionString $env:CHILD_DB -Tables "Product,Customer"

.EXAMPLE
    ./quickstart/deploy.ps1 -Suffix acme1 -SyncNow

.EXAMPLE
    ./quickstart/deploy.ps1 -Suffix acme1 -Destroy
#>

#Requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'Setup')]
param(
    # Short unique suffix for the Azure names: 3-8 lowercase letters or digits
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{3,8}$')]
    [string] $Suffix,

    # Either the server and database names (the Function then signs in with new managed
    # identities) ...
    # Parent server host name, e.g. myserver.database.windows.net
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $ParentServer,

    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $ParentDatabase,

    # Child server; the parent's server when left out
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $ChildServer,

    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $ChildDatabase,

    # ... or your own connection strings: saved as the Function's app settings exactly as given,
    # and used by this script too. No identities, database users or grants are created.
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [Parameter(ParameterSetName = 'SyncNow')]
    [string] $ParentConnectionString,

    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [Parameter(ParameterSetName = 'SyncNow')]
    [string] $ChildConnectionString,

    # Auto mode with your own connection strings: a child connection string that may create
    # tables; ChildConnectionString when left out
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $ChildProvisioningConnectionString,

    # Comma-separated: Customer for dbo.Customer, sales.Order for another schema
    [Parameter(Mandatory, ParameterSetName = 'Setup')]
    [Parameter(Mandatory, ParameterSetName = 'DryRun')]
    [string] $Tables,

    # Auto: the Function creates the child's tables. Manual: this script applies the scaffold
    # tool's scripts and the Function never gets DDL rights.
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [ValidateSet('Auto', 'Manual')]
    [string] $Mode = 'Auto',

    # Turns on SQL Server Change Tracking in the parent for the listed tables that have a
    # primary key (2-day retention if the database doesn't have it yet)
    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [switch] $EnableChangeTracking,

    [Parameter(ParameterSetName = 'Setup')]
    [Parameter(ParameterSetName = 'DryRun')]
    [string] $Location = 'swedencentral',

    [Parameter(Mandatory, ParameterSetName = 'DryRun')]
    [switch] $DryRun,

    [Parameter(Mandatory, ParameterSetName = 'SyncNow')]
    [switch] $SyncNow,

    [Parameter(Mandatory, ParameterSetName = 'Destroy')]
    [switch] $Destroy,

    # Seconds to wait for the first copy; large tables need longer
    [int] $SyncTimeoutSeconds = 600,

    # Skips the script's own "Create ...?" question; Terraform still asks to confirm its plan
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$terraformDir = Join-Path $PSScriptRoot 'terraform'
$outputDir = Join-Path (Join-Path $PSScriptRoot 'generated') $Suffix
$settingsFile = Join-Path $outputDir 'quickstart.tfvars.json'

# Database user names. Fixed, so the scaffold tool's scripts and sql/07_permissions.sql work as
# written; each is tied to its managed identity by SID, not by the identity's name.
$runtimeUser = 'smarttablesync-runtime'
$provisioningUser = 'smarttablesync-child-provisioning'

# Same limit as the engine: larger tables need another way to find changes
$fullRescanRowLimit = 50000

# --- Helpers ------------------------------------------------------------------------------

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Invoke-Native([scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $command"
    }
}

function Assert-Tool([string] $name, [string] $installHint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' was not found. $installHint"
    }
}

# [name] with ] doubled, as SQL Server's QUOTENAME does
function ConvertTo-SqlName([string] $name) { '[' + $name.Replace(']', ']]') + ']' }

# N'text' with ' doubled
function ConvertTo-SqlLiteral([string] $text) { "N'" + $text.Replace("'", "''") + "'" }

# The SID Azure SQL expects for a Microsoft Entra application or managed identity: its client ID
# as uniqueidentifier bytes (the byte order .NET's Guid.ToByteArray uses too)
function ConvertTo-Sid([string] $clientId) {
    '0x' + ((([guid] $clientId).ToByteArray() | ForEach-Object { $_.ToString('X2') }) -join '')
}

# Splits "sales.Order" into schema and table; "Customer" and "dbo.Customer" are dbo.Customer
function Get-TableName([string] $name) {
    $parts = $name.Trim().Split('.')
    if ($parts.Count -gt 2 -or ($parts | Where-Object { -not $_ })) {
        throw "'$name' isn't a valid table name. Use Table or schema.Table."
    }
    $schema = if ($parts.Count -eq 2) { $parts[0] } else { 'dbo' }
    $table = $parts[-1]
    [pscustomobject]@{
        Schema  = $schema
        Table   = $table
        # The name SmartTableSync uses in TableList and in its tracking tables
        Setting = if ($schema -eq 'dbo') { $table } else { "$schema.$table" }
        Quoted  = "$(ConvertTo-SqlName $schema).$(ConvertTo-SqlName $table)"
    }
}

# A database this script connects to: as you (Azure CLI sign-in), or with a given connection string
function New-SqlTarget([string] $server, [string] $database) {
    [pscustomobject]@{
        Server           = $server
        Database         = $database
        ConnectionString = "Server=tcp:$server,1433;Database=$database;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
        SignIn           = @('--authentication-method', 'ActiveDirectoryDefault')
        Password         = $null
    }
}

function ConvertFrom-ConnectionString([string] $connectionString, [string] $name) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    # set_ConnectionString, not .ConnectionString =: PowerShell treats the builder as a dictionary
    # and would add a key named "ConnectionString" instead of parsing the string
    try { $builder.set_ConnectionString($connectionString) } catch { throw "$name isn't a valid connection string: $($_.Exception.Message)" }
    function Get-Value([string[]] $keys) {
        foreach ($key in $keys) { if ($builder.ContainsKey($key)) { return [string] $builder[$key] } }
    }

    $server = Get-Value 'Server', 'Data Source', 'Address', 'Addr', 'Network Address'
    $database = Get-Value 'Database', 'Initial Catalog'
    if (-not $server -or -not $database) { throw "$name needs both a server (Server=...) and a database (Database=...)." }
    $user = Get-Value 'User Id', 'UID', 'User'
    $password = Get-Value 'Password', 'PWD'
    $authentication = (Get-Value 'Authentication') -replace '\s', ''
    $integrated = (Get-Value 'Integrated Security', 'Trusted_Connection') -in @('true', 'yes', 'sspi')

    # The same sign-in, in sqlcmd's terms
    $signIn = if ($integrated) { @('-E') }
        elseif ($authentication) { @('--authentication-method', ($authentication -replace '^ActiveDirectoryMSI$', 'ActiveDirectoryManagedIdentity')) }
        elseif ($user) { @('--authentication-method', 'SqlPassword') }
        else { throw "$name has no sign-in: add User Id and Password, Authentication=..., or Integrated Security=True." }
    if ($user -and -not $integrated) { $signIn += @('-U', $user) }
    if ((Get-Value 'TrustServerCertificate') -in @('true', 'yes')) { $signIn += '-C' }

    [pscustomobject]@{
        # Only the host name, for messages and the firewall check
        Server           = ($server -replace '^tcp:', '' -replace ',\d+$', '')
        Database         = $database
        ConnectionString = $connectionString
        SignIn           = $signIn
        Password         = $password
    }
}

# Runs a SQL file or query on a target. A paused serverless database refuses the first connection
# while it resumes, so failures are retried a few times. A password goes through SQLCMDPASSWORD,
# never on the command line.
function Invoke-Sql {
    param(
        [Parameter(Mandatory)] $Target,
        [string] $File,
        [string] $Query,
        [switch] $Raw
    )

    $arguments = @('-S', $Target.Server, '-d', $Target.Database) + $Target.SignIn + @('-l', '60', '-b')
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Query) }
    # Raw: no headers, "|" between columns, for reading values; otherwise trimmed columns for people
    if ($Raw) { $arguments += @('-h', '-1', '-W', '-s', '|') } else { $arguments += '-W' }

    if ($Target.Password) { $env:SQLCMDPASSWORD = $Target.Password }
    try {
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $output = & sqlcmd @arguments 2>&1 | ForEach-Object { "$_" }
            if ($LASTEXITCODE -eq 0) { return @($output | Where-Object { $_.Trim() }) }
            # This script's own checks (THROW 50000), missing rights and wrong passwords won't go away by retrying
            if ($output -match 'Msg 50000,|permission was denied|do not have permission|Login failed') { break }
            if ($attempt -lt 4) {
                Write-Host "  SQL attempt $attempt failed (a paused database may still be resuming); retrying in 20 s..." -ForegroundColor Yellow
                Start-Sleep -Seconds 20
            }
        }
    }
    finally {
        Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    }
    throw "sqlcmd failed on $($Target.Server)/$($Target.Database):`n$($output -join "`n")"
}

# Your connection strings go to Terraform as environment variables, never into a file
function Set-TerraformConnectionStrings {
    if (-not $ParentConnectionString) { return }
    $env:TF_VAR_parent_connection_string = $ParentConnectionString
    $env:TF_VAR_child_connection_string = $ChildConnectionString
    if ($ChildProvisioningConnectionString) { $env:TF_VAR_child_provisioning_connection_string = $ChildProvisioningConnectionString }
}

function Clear-TerraformConnectionStrings {
    Remove-Item Env:TF_VAR_parent_connection_string, Env:TF_VAR_child_connection_string, Env:TF_VAR_child_provisioning_connection_string -ErrorAction SilentlyContinue
}

# Runs the scaffold tool. In the repository it runs from source; in a project created with
# "dotnet new smarttablesync-azure" it's the local tool in .config/dotnet-tools.json, in the same
# version as the SmartTableSync package the Function uses.
function Invoke-Scaffold([string[]] $arguments) {
    $project = Join-Path $repoRoot 'SmartTableSync.Scaffold'
    if (Test-Path $project) {
        Invoke-Native { dotnet run --project $project -- @arguments }
        return
    }
    Push-Location $repoRoot
    try {
        Invoke-Native { dotnet tool restore }
        Invoke-Native { dotnet smarttablesync-scaffold @arguments }
    }
    finally {
        Pop-Location
    }
}

function Write-SqlFile([string] $name, [string[]] $lines) {
    $path = Join-Path $outputDir $name
    Set-Content -Path $path -Value ($lines -join "`n") -Encoding utf8
    Write-Host "  wrote $(Resolve-Path -Relative $path)"
    $path
}

function Initialize-Terraform {
    Invoke-Native { terraform "-chdir=$terraformDir" init -input=false }
    # One workspace (and state) per suffix, so several quick starts don't overwrite each other
    Invoke-Native { terraform "-chdir=$terraformDir" workspace select -or-create $Suffix }
}

# Stops when the suffix's resource group exists in Azure but this folder's Terraform state for the
# suffix doesn't have it: another quick start (another folder or machine) created it, and Terraform
# would only fail at apply with "already exists". Reads the local state file, so it runs before
# Terraform is initialized.
function Assert-SuffixAvailable {
    $resourceGroup = "rg-smarttablesync-qs-$Suffix"
    $exists = az group exists --name $resourceGroup
    if ($LASTEXITCODE -ne 0) { throw "Could not check whether the resource group $resourceGroup exists." }
    if ($exists -ne 'true') { return }

    $stateFile = Join-Path $terraformDir "terraform.tfstate.d/$Suffix/terraform.tfstate"
    $managed = (Test-Path $stateFile) -and @(
        (Get-Content $stateFile -Raw | ConvertFrom-Json).resources |
            Where-Object { $_.mode -eq 'managed' -and $_.type -eq 'azurerm_resource_group' }).Count -gt 0
    if (-not $managed) {
        throw "Suffix '$Suffix' is already used by another quick start (another folder or machine): the resource group $resourceGroup exists, but this folder's Terraform state doesn't manage it. Choose another -Suffix, or run -Destroy in the folder that created it."
    }
}

function Get-Deployment {
    Initialize-Terraform | Out-Null
    $json = terraform "-chdir=$terraformDir" output -json
    if ($LASTEXITCODE -ne 0) { throw "Could not read Terraform outputs for '$Suffix'. Run the setup first." }
    $outputs = $json | ConvertFrom-Json
    if (-not ($outputs.PSObject.Properties.Name -contains 'function_app_name')) {
        throw "Nothing is deployed for suffix '$Suffix'. Run the setup first."
    }
    # Terraform leaves out an output whose value is null, for example the identity client IDs
    # when the Function uses your own connection strings
    function Get-Output([string] $name) {
        if ($outputs.PSObject.Properties.Name -contains $name) { $outputs.$name.value }
    }
    [pscustomobject]@{
        ResourceGroup        = Get-Output 'resource_group_name'
        FunctionApp          = Get-Output 'function_app_name'
        FunctionHost         = Get-Output 'function_app_hostname'
        RuntimeClientId      = Get-Output 'runtime_client_id'
        ProvisioningClientId = Get-Output 'provisioning_client_id'
    }
}

# --- What each table will get --------------------------------------------------------------

function Get-TableReport($name) {
    $objectName = ConvertTo-SqlLiteral $name.Quoted
    $query = @"
SET NOCOUNT ON;
DECLARE @id INT = OBJECT_ID($objectName, N'U');
SELECT CASE WHEN @id IS NULL THEN 0 ELSE 1 END,
       (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = @id AND type = 'PK'),
       (SELECT COUNT(*) FROM sys.change_tracking_tables WHERE object_id = @id),
       (SELECT COUNT(*) FROM sys.columns WHERE object_id = @id AND system_type_id = 189),
       (SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
         WHERE c.object_id = @id AND c.name = N'LastUpdate' AND t.name IN (N'datetime', N'datetime2', N'smalldatetime', N'date')),
       (SELECT COUNT(*) FROM sys.indexes i
         WHERE i.object_id = @id AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0
           AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                             AND ic.is_included_column = 0 AND c.is_nullable = 1)),
       (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats WHERE object_id = @id AND index_id IN (0, 1));
"@
    $values = @(Invoke-Sql -Target $script:parentTarget -Query $query -Raw)[0].Split('|')
    $exists, $hasPk, $hasCt, $hasRowVersion, $hasLastUpdate, $uniqueIndexes = $values[0..5] | ForEach-Object { [int] $_ }
    $rows = [long] $values[6]
    $willHaveCt = $hasCt -or ($EnableChangeTracking -and $hasPk)

    $strategy, $note = if (-not $exists) { 'MISSING', 'not found in the parent' }
        elseif (-not $hasPk -and -not $uniqueIndexes) { 'NO KEY', 'no primary key or unique NOT NULL index: set SmartTableSync:KeyColumns for it after the setup' }
        elseif ($willHaveCt -and $hasPk) { 'SqlChangeTracking', $(if ($hasCt) { 'Change Tracking is on' } else { 'Change Tracking will be turned on' }) }
        elseif ($hasRowVersion) { 'RowVersion', 'has a rowversion column' }
        elseif ($hasLastUpdate) { 'Timestamp', 'LastUpdate column (must hold UTC time)' }
        elseif ($rows -le $fullRescanRowLimit) { 'FullRescan', 'no way to find changes; small enough to read in full every run' }
        else { 'BLOCKED', "over $fullRescanRowLimit rows with no way to find changes: add a rowversion column (scaffold tool --rowversion-setup) or list it in SmartTableSync:AllowFullRescanFor" }

    [pscustomobject]@{
        Table    = $name.Setting
        Rows     = $rows
        Strategy = $strategy
        Note     = $note
        Name     = $name
        HasPk    = [bool] $hasPk
        HasCt    = [bool] $hasCt
        Exists   = [bool] $exists
    }
}

# --- SQL scripts ---------------------------------------------------------------------------

# Creates a database user for a managed identity from its client ID. Stops when the name is
# already taken by a different identity (for example the demo's), instead of reusing it.
function Get-CreateUserSql([string] $user, [string] $clientId) {
    $sid = if ($clientId) { ConvertTo-Sid $clientId } else { '0x<sid-of-the-identity-after-terraform>' }
    $literal = ConvertTo-SqlLiteral $user
    $quoted = ConvertTo-SqlName $user
    @(
        "IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $literal AND sid <> $sid)"
        "    THROW 50000, 'The database user $($user.Replace("'", "''")) already exists for a different identity. Remove it, or use another database.', 1;"
        "IF DATABASE_PRINCIPAL_ID($literal) IS NULL"
        "    CREATE USER $quoted WITH SID = $sid, TYPE = E;"
        'GO'
    )
}

# -EnableChangeTracking: database level (2-day retention, left as is when already on), then each
# listed table with a primary key that doesn't have it yet. Nothing without the switch.
function Get-ChangeTrackingSql($reports) {
    $toEnable = @($reports | Where-Object { $_.HasPk -and -not $_.HasCt })
    if (-not $EnableChangeTracking -or -not $toEnable) { return @() }
    $lines = @(
        ''
        '-- Change Tracking: database level (2-day retention, left as is when already on), then per table'
        'IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())'
        '    ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);'
        'GO'
    )
    foreach ($report in $toEnable) {
        $lines += @(
            "IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID($(ConvertTo-SqlLiteral $report.Name.Quoted)))"
            "    ALTER TABLE $($report.Name.Quoted) ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);"
            'GO'
        )
    }
    $lines
}

function Get-ParentSql($reports, [string] $runtimeClientId) {
    $runtime = ConvertTo-SqlName $runtimeUser
    $lines = @(
        '-- PARENT database: the runtime identity may read the synced tables, and nothing else.'
        '-- Written by quickstart/deploy.ps1. Safe to re-run.'
        ''
    ) + (Get-CreateUserSql $runtimeUser $runtimeClientId)

    $ctTables = @($reports | Where-Object { $_.HasPk -and ($_.HasCt -or $EnableChangeTracking) })
    $lines += Get-ChangeTrackingSql $reports

    $lines += @('', '-- Read the rows, and see the table''s definition and size when it is profiled')
    foreach ($report in $reports) {
        $lines += "GRANT SELECT, VIEW DEFINITION ON $($report.Name.Quoted) TO $runtime;"
    }
    $lines += "GRANT VIEW DATABASE STATE TO $runtime;"
    foreach ($report in $ctTables) {
        $lines += "GRANT VIEW CHANGE TRACKING ON $($report.Name.Quoted) TO $runtime;"
    }
    $lines + 'GO'
}

# Auto mode: the provisioning identity gets db_ddladmin; the runtime gets data rights on each
# schema it writes to, because the Function creates the objects after these grants are given.
function Get-ChildAutoSql($reports, [string] $runtimeClientId, [string] $provisioningClientId) {
    $runtime = ConvertTo-SqlName $runtimeUser
    $lines = @(
        '-- CHILD database, Auto mode. Written by quickstart/deploy.ps1. Safe to re-run.'
        '-- The provisioning identity creates and changes the synced tables, table types and procedures.'
        '-- The runtime identity reads and writes rows; it has no DDL rights.'
        ''
    ) + (Get-CreateUserSql $runtimeUser $runtimeClientId) + (Get-CreateUserSql $provisioningUser $provisioningClientId)

    $lines += @(
        "ALTER ROLE [db_ddladmin] ADD MEMBER $(ConvertTo-SqlName $provisioningUser);"
        'GO'
        ''
        '-- Schemas the synced tables go into (the tracking tables are always in dbo)'
    )
    $schemas = @('dbo') + @($reports | ForEach-Object { $_.Name.Schema }) | Select-Object -Unique
    foreach ($schema in $schemas) {
        if ($schema -ne 'dbo') {
            $lines += "IF SCHEMA_ID($(ConvertTo-SqlLiteral $schema)) IS NULL EXEC (N'CREATE SCHEMA $((ConvertTo-SqlName $schema).Replace("'", "''"))');"
        }
        $lines += "GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::$(ConvertTo-SqlName $schema) TO $runtime;"
    }
    $lines + 'GO'
}

# Manual mode, after the scaffold tool's scripts: rights on exactly the objects they created
function Get-ChildManualGrantsSql($reports, [string] $runtimeClientId) {
    $runtime = ConvertTo-SqlName $runtimeUser
    $lines = @(
        '-- CHILD database, Manual mode: the runtime identity may write the synced tables and the'
        '-- tracking tables, and run the merge procedures. No DDL rights. Written by quickstart/deploy.ps1.'
        ''
    ) + (Get-CreateUserSql $runtimeUser $runtimeClientId)

    foreach ($report in $reports) {
        $lines += "GRANT SELECT, INSERT, UPDATE, DELETE ON $($report.Name.Quoted) TO $runtime;"
    }
    $lines += @(
        "GRANT SELECT, INSERT, UPDATE ON [dbo].[CustomSyncState] TO $runtime;"
        "GRANT SELECT, INSERT, UPDATE ON [dbo].[SyncBatchLog] TO $runtime;"
        "GRANT SELECT, INSERT, UPDATE ON [dbo].[SyncTableConfig] TO $runtime;"
        'GO'
        ''
        '-- The merge procedures and table types the scaffold step created, in the synced tables'' schemas'
        "DECLARE @grants NVARCHAR(MAX) = N'';"
    )
    $schemaList = (@($reports | ForEach-Object { ConvertTo-SqlLiteral $_.Name.Schema }) | Select-Object -Unique) -join ', '
    $lines += @(
        "SELECT @grants += N'GRANT EXECUTE ON ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' TO $runtime;' + CHAR(10)"
        "FROM sys.procedures WHERE SCHEMA_NAME(schema_id) IN ($schemaList) AND name LIKE N'sp[_]Sync%';"
        "SELECT @grants += N'GRANT EXECUTE ON TYPE::' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' TO $runtime;' + CHAR(10)"
        "FROM sys.table_types WHERE SCHEMA_NAME(schema_id) IN ($schemaList) AND name LIKE N'%TableType';"
        'PRINT @grants;'
        'EXEC sys.sp_executesql @grants;'
        'GO'
    )
    $lines
}

function Get-DropUsersSql {
    @(
        '-- Removes the database users quickstart/deploy.ps1 created. Run on each database yourself;'
        '-- the synced tables, tracking tables and procedures in the child are left in place.'
        ''
        '-- PARENT and CHILD:'
        "DROP USER IF EXISTS $(ConvertTo-SqlName $runtimeUser);"
        ''
        '-- CHILD, Auto mode:'
        "DROP USER IF EXISTS $(ConvertTo-SqlName $provisioningUser);"
    )
}

# --- Running the sync -----------------------------------------------------------------------

# Starts the timer function now instead of waiting for its 5-minute schedule. Right after a
# publish the host may still be starting, so this is retried.
function Start-Sync($deployment) {
    $masterKey = az functionapp keys list --resource-group $deployment.ResourceGroup --name $deployment.FunctionApp --query masterKey --output tsv
    if ($LASTEXITCODE -ne 0 -or -not $masterKey) { throw 'Could not read the Function App master key.' }

    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            Invoke-RestMethod -Method Post `
                -Uri "https://$($deployment.FunctionHost)/admin/functions/SmartTableSync" `
                -Headers @{ 'x-functions-key' = $masterKey } `
                -ContentType 'application/json' -Body '{}' | Out-Null
            return
        }
        catch {
            if ($attempt -eq 6) { throw }
            Write-Host "  the Function isn't ready yet; trying again in 20 s..." -ForegroundColor Yellow
            Start-Sleep -Seconds 20
        }
    }
}

# Waits until every table has a saved position in the child and the same row count as the parent
function Wait-ForSync($settings) {
    $names = @($settings.table_list.Split(',') | ForEach-Object { Get-TableName $_ })
    $deadline = (Get-Date).AddSeconds($SyncTimeoutSeconds)

    do {
        Start-Sleep -Seconds 20
        $results = foreach ($name in $names) {
            # "-" while the child table doesn't exist yet (Auto mode creates it on the first run)
            $count = "SET NOCOUNT ON; IF OBJECT_ID($(ConvertTo-SqlLiteral $name.Quoted), N'U') IS NULL SELECT '-' ELSE SELECT COUNT_BIG(*) FROM $($name.Quoted);"
            $parentRows = @(Invoke-Sql -Target $script:parentTarget -Query $count -Raw)[0]
            $childRows = @(Invoke-Sql -Target $script:childTarget -Query $count -Raw)[0]
            $state = @(Invoke-Sql -Target $script:childTarget -Raw -Query (
                "SET NOCOUNT ON; IF OBJECT_ID(N'dbo.CustomSyncState', N'U') IS NULL SELECT 0 " +
                "ELSE SELECT COUNT(*) FROM dbo.CustomSyncState WHERE TableName = $(ConvertTo-SqlLiteral $name.Setting);"))[0]
            [pscustomobject]@{
                Table  = $name.Setting
                Parent = $parentRows
                Child  = $childRows
                Synced = ($state -eq '1') -and ($parentRows -eq $childRows)
            }
        }
        $pending = @($results | Where-Object { -not $_.Synced })
        Write-Host "  $(Get-Date -Format HH:mm:ss)  synced: $(@($results).Count - $pending.Count)/$(@($results).Count)"
    } while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline)

    $results | Format-Table Table, Synced, Parent, Child -AutoSize | Out-Host
    $pending.Count -eq 0
}

function Show-SyncStatus($settings) {
    Write-Step 'What SmartTableSync chose and recorded (child database)'
    Invoke-Sql -Target $script:childTarget -Query @'
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.SyncTableConfig') IS NOT NULL
    SELECT TableName, IncrementalStrategy, MergeStrategy, BatchSize FROM dbo.SyncTableConfig ORDER BY TableName;
IF OBJECT_ID(N'dbo.SyncBatchLog') IS NOT NULL
    SELECT TOP (10) TableName, Phase, RowsSynced, RowsErrored, Status, StartedAt FROM dbo.SyncBatchLog ORDER BY LogId DESC;
'@ | Out-Host
}

function Show-Troubleshooting {
    Write-Host ''
    Write-Host 'Not every table has synced yet. Large tables take longer: run with -SyncNow later to check again.' -ForegroundColor Yellow
    Write-Host 'If nothing arrives at all:' -ForegroundColor Yellow
    Write-Host '  - Look at the Function''s Log stream in the portal, or "traces" in Application Insights.' -ForegroundColor Yellow
    Write-Host '  - The Function must reach both SQL servers: turn on "Allow Azure services and resources to access this server"' -ForegroundColor Yellow
    Write-Host '    in each server''s Networking page, or connect the Function to their virtual network.' -ForegroundColor Yellow
}

# Best effort: warns when a server in this subscription blocks Azure services. Servers in another
# subscription, or not in Azure, can't be checked from here.
function Test-AzureAccess([string] $server) {
    # Filtered here rather than with --query: az is a .cmd on Windows and mangles quotes and &&
    $found = @(az sql server list --output json 2>$null | ConvertFrom-Json | Where-Object fullyQualifiedDomainName -eq $server)
    if (-not $found) {
        Write-Host "  $server isn't in this subscription; make sure the Function can reach it." -ForegroundColor Yellow
        return
    }
    if ($found[0].publicNetworkAccess -eq 'Disabled') {
        Write-Host "  $server has public access disabled: the Function needs a virtual network connection to reach it." -ForegroundColor Yellow
        return
    }
    $rules = @(az sql server firewall-rule list --resource-group $found[0].resourceGroup --server $found[0].name --output json | ConvertFrom-Json |
        Where-Object { $_.startIpAddress -eq '0.0.0.0' -and $_.endIpAddress -eq '0.0.0.0' })
    if ($rules) {
        Write-Host "  $server allows Azure services: the Function can reach it."
    }
    else {
        Write-Host "  $server doesn't allow Azure services. Turn on 'Allow Azure services and resources to access this server' in its Networking page, or the Function can't connect." -ForegroundColor Yellow
    }
}

# --- Prerequisites ----------------------------------------------------------------------------

Assert-Tool 'az' 'Install the Azure CLI: https://learn.microsoft.com/cli/azure/install-azure-cli'
Assert-Tool 'terraform' 'Install Terraform: https://developer.hashicorp.com/terraform/install'
if (-not $Destroy) {
    $goSqlcmdHint = 'Install go-sqlcmd (winget install sqlcmd): https://learn.microsoft.com/sql/tools/sqlcmd/go-sqlcmd-utility'
    Assert-Tool 'sqlcmd' $goSqlcmdHint
    & sqlcmd --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The 'sqlcmd' on your PATH ($((Get-Command sqlcmd).Source)) is the older ODBC version. $goSqlcmdHint, and make sure it comes first on PATH."
    }
}
if ($PSCmdlet.ParameterSetName -eq 'Setup') {
    Assert-Tool 'dotnet' 'Install the .NET 10 SDK: https://dotnet.microsoft.com/download'
    Assert-Tool 'func' 'Install Azure Functions Core Tools v4: https://learn.microsoft.com/azure/azure-functions/functions-run-local'
}

$account = az account show --query '{name:name, id:id, user:user.name}' --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Not signed in. Run "az login" and "az account set --subscription <id>" first.' }
$env:ARM_SUBSCRIPTION_ID = $account.id

# --- Destroy -----------------------------------------------------------------------------------

if ($Destroy) {
    if (-not (Test-Path $settingsFile)) { throw "No quick start found for suffix '$Suffix' ($settingsFile is missing)." }
    $deployment = Get-Deployment
    Write-Step "Removing the Azure resources of '$Suffix' from subscription '$($account.name)'"
    Invoke-Native { terraform "-chdir=$terraformDir" destroy -input=false "-var-file=$settingsFile" }

    Write-Host ''
    if ($deployment.RuntimeClientId) {
        $dropFile = Write-SqlFile '99_drop_users.sql' (Get-DropUsersSql)
        Write-Host "Your databases were not touched. To remove the database users too, run $dropFile on them." -ForegroundColor Green
    }
    else {
        Write-Host 'Your databases were not touched.' -ForegroundColor Green
    }
    return
}

# --- Sync now -----------------------------------------------------------------------------------

if ($SyncNow) {
    if (-not (Test-Path $settingsFile)) { throw "No quick start found for suffix '$Suffix' ($settingsFile is missing)." }
    $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $deployment = Get-Deployment

    # With your own connection strings there are no saved ones to compare with: pass them again
    if ($deployment.RuntimeClientId) {
        $script:parentTarget = New-SqlTarget $settings.parent_server $settings.parent_database
        $script:childTarget = New-SqlTarget $settings.child_server $settings.child_database
    }
    elseif ($ParentConnectionString -and $ChildConnectionString) {
        $script:parentTarget = ConvertFrom-ConnectionString $ParentConnectionString '-ParentConnectionString'
        $script:childTarget = ConvertFrom-ConnectionString $ChildConnectionString '-ChildConnectionString'
    }
    else {
        throw 'This quick start uses your own connection strings: pass -ParentConnectionString and -ChildConnectionString again, so the script can compare the databases.'
    }

    Write-Step 'Starting a sync and waiting for the child'
    Start-Sync $deployment
    if (-not (Wait-ForSync $settings)) { Show-Troubleshooting }
    Show-SyncStatus $settings
    return
}

# --- Setup (and dry run) ------------------------------------------------------------------------

# Your own connection strings, or server and database names (then the Function gets managed identities)
$useOwnConnectionStrings = [bool] ($ParentConnectionString -or $ChildConnectionString)
if ($useOwnConnectionStrings) {
    if (-not $ParentConnectionString -or -not $ChildConnectionString) { throw 'Give both -ParentConnectionString and -ChildConnectionString.' }
    if ($ParentServer -or $ParentDatabase -or $ChildServer -or $ChildDatabase) {
        throw 'Give either your connection strings or the server and database names, not both.'
    }
    $script:parentTarget = ConvertFrom-ConnectionString $ParentConnectionString '-ParentConnectionString'
    $script:childTarget = ConvertFrom-ConnectionString $ChildConnectionString '-ChildConnectionString'
    # Creates the child's objects: Manual mode in this script, Auto mode in the Function
    $provisioningTarget = if ($ChildProvisioningConnectionString) {
        ConvertFrom-ConnectionString $ChildProvisioningConnectionString '-ChildProvisioningConnectionString'
    } else { $script:childTarget }
}
else {
    if (-not $ParentServer -or -not $ParentDatabase -or -not $ChildDatabase) {
        throw 'Give -ParentServer, -ParentDatabase and -ChildDatabase, or your own -ParentConnectionString and -ChildConnectionString.'
    }
    if (-not $ChildServer) { $ChildServer = $ParentServer }
    $script:parentTarget = New-SqlTarget $ParentServer $ParentDatabase
    $script:childTarget = New-SqlTarget $ChildServer $ChildDatabase
    $provisioningTarget = $script:childTarget
}

# Syncing a database into itself can't work, and is almost always a copy-paste slip in the child's name
if ($script:parentTarget.Server -eq $script:childTarget.Server -and $script:parentTarget.Database -eq $script:childTarget.Database) {
    throw "The parent and the child are the same database ($($script:parentTarget.Server)/$($script:parentTarget.Database)). Check the child's database name."
}

$tableNames = @($Tables.Split(',') | Where-Object { $_.Trim() } | ForEach-Object { Get-TableName $_ })
if (-not $tableNames) { throw 'List at least one table in -Tables.' }
$duplicates = @($tableNames | Group-Object Setting | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicates) { throw "Listed more than once: $($duplicates -join ', ')" }
Assert-SuffixAvailable
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host ''
Write-Host "Subscription : $($account.name) ($($account.id))"
Write-Host "Signed in as : $($account.user)"
Write-Host "Parent       : $($script:parentTarget.Server) / $($script:parentTarget.Database)"
Write-Host "Child        : $($script:childTarget.Server) / $($script:childTarget.Database)"
Write-Host "Mode         : $Mode"
Write-Host "Region       : $Location"
Write-Host "Function     : $(if ($useOwnConnectionStrings) { 'your connection strings, saved as its app settings' } else { 'new managed identities, with database users created by this script' })"

Write-Step '1/5  Checking the databases'
Invoke-Sql -Target $script:childTarget -Query 'SELECT 1' | Out-Null
$reports = @($tableNames | ForEach-Object { Get-TableReport $_ })
$reports | Format-Table Table, Rows, Strategy, Note -AutoSize -Wrap | Out-Host

$missing = @($reports | Where-Object { -not $_.Exists })
if ($missing) { throw "Not found in the parent: $(($missing | ForEach-Object Table) -join ', '). Check the names (schema.Table for schemas other than dbo)." }
$problems = @($reports | Where-Object { $_.Strategy -in @('NO KEY', 'BLOCKED') })
if ($problems) {
    Write-Host "These tables won't sync until you fix what the Note says; the others will: $(($problems | ForEach-Object Table) -join ', ')" -ForegroundColor Yellow
}
Test-AzureAccess $script:parentTarget.Server
if ($script:childTarget.Server -ne $script:parentTarget.Server) { Test-AzureAccess $script:childTarget.Server }

# Saved for -SyncNow and -Destroy, and passed to Terraform. Names only: your connection strings
# never go into this file.
$settings = [ordered]@{
    suffix          = $Suffix
    location        = $Location
    parent_server   = $script:parentTarget.Server
    parent_database = $script:parentTarget.Database
    child_server    = $script:childTarget.Server
    child_database  = $script:childTarget.Database
    table_list      = ($tableNames | ForEach-Object Setting) -join ','
    mode            = $Mode
}
$settings | ConvertTo-Json | Set-Content -Path $settingsFile -Encoding utf8

if ($DryRun) {
    if ($useOwnConnectionStrings) {
        Write-Step 'SQL that the setup would run'
        $ctSql = Get-ChangeTrackingSql $reports
        if ($ctSql) { Write-SqlFile '01_parent_change_tracking.sql' $ctSql | Out-Null }
        Write-Host '  No database users or grants: the logins in your connection strings are used as they are.'
    }
    else {
        Write-Step 'SQL that the setup would run (identity SIDs are filled in after Terraform)'
        Write-SqlFile '01_parent.sql' (Get-ParentSql $reports $null) | Out-Null
        if ($Mode -eq 'Auto') {
            Write-SqlFile '02_child.sql' (Get-ChildAutoSql $reports $null $null) | Out-Null
        }
        else {
            Write-SqlFile '03_child_grants.sql' (Get-ChildManualGrantsSql $reports $null) | Out-Null
        }
    }
    if ($Mode -eq 'Manual') {
        Write-Host '  Manual mode: the child tables, types and procedures come from the scaffold tool during the setup.'
    }

    Write-Step 'What Terraform would create'
    Initialize-Terraform
    Set-TerraformConnectionStrings
    try {
        Invoke-Native { terraform "-chdir=$terraformDir" plan -input=false "-var-file=$settingsFile" }
    }
    finally {
        Clear-TerraformConnectionStrings
    }
    Write-Host ''
    Write-Host 'Dry run only: nothing was created or changed.' -ForegroundColor Green
    return
}

if (-not $Yes) {
    $answer = Read-Host 'Create the Azure resources and start syncing? Terraform shows its plan next. (y/N)'
    if ($answer -notin @('y', 'Y', 'yes')) {
        Write-Host 'Cancelled.'
        return
    }
}

Write-Step '2/5  Creating the Azure resources (Terraform asks you to confirm its plan)'
Initialize-Terraform
Set-TerraformConnectionStrings
try {
    Invoke-Native { terraform "-chdir=$terraformDir" apply -input=false "-var-file=$settingsFile" }
}
finally {
    Clear-TerraformConnectionStrings
}
$deployment = Get-Deployment

if ($useOwnConnectionStrings) {
    Write-Step '3/5  Parent: nothing to create, your connection string''s login reads the tables'
    $ctSql = Get-ChangeTrackingSql $reports
    if ($ctSql) {
        $ctFile = Write-SqlFile '01_parent_change_tracking.sql' $ctSql
        Invoke-Sql -Target $script:parentTarget -File $ctFile | Out-Host
    }
}
else {
    Write-Step '3/5  Parent: database user and read access for the runtime identity'
    $parentFile = Write-SqlFile '01_parent.sql' (Get-ParentSql $reports $deployment.RuntimeClientId)
    Invoke-Sql -Target $script:parentTarget -File $parentFile | Out-Host
}

if ($Mode -eq 'Auto') {
    if ($useOwnConnectionStrings) {
        Write-Step '4/5  Child: nothing to create, the Function creates the tables itself with your child connection string'
    }
    else {
        Write-Step '4/5  Child: database users and grants (the Function creates the tables itself)'
        $childFile = Write-SqlFile '02_child.sql' (Get-ChildAutoSql $reports $deployment.RuntimeClientId $deployment.ProvisioningClientId)
        Invoke-Sql -Target $script:childTarget -File $childFile | Out-Host
    }
}
else {
    Write-Step '4/5  Child: tracking tables, synced tables and merge procedures from SmartTableSync.Scaffold'
    $scaffoldDir = Join-Path $outputDir 'child'
    if (Test-Path $scaffoldDir) { Get-ChildItem $scaffoldDir -Filter '*.sql' | Remove-Item }

    $env:ParentIntrospectionConnectionString = $script:parentTarget.ConnectionString
    $env:ChildProvisioningConnectionString = $provisioningTarget.ConnectionString
    try {
        Invoke-Scaffold @('--infrastructure', '--output', $scaffoldDir)
        Invoke-Scaffold (@($reports | ForEach-Object Table) + @('--output', $scaffoldDir))
    }
    finally {
        Remove-Item Env:ParentIntrospectionConnectionString, Env:ChildProvisioningConnectionString -ErrorAction SilentlyContinue
    }

    # Tables first (or the changes an existing one needs), then table types, then procedures
    $childScripts = @(Join-Path $scaffoldDir '00_infrastructure.sql') +
        @($reports | ForEach-Object { Join-Path $scaffoldDir "$($_.Table).sql" } | Where-Object { Test-Path $_ }) +
        @(Get-ChildItem $scaffoldDir -Filter '*_AddColumns.sql' | ForEach-Object FullName) +
        @(Get-ChildItem $scaffoldDir -Filter '*_AlterColumns.sql' | ForEach-Object FullName) +
        @(Get-ChildItem $scaffoldDir -Filter '*TableType.sql' | ForEach-Object FullName) +
        @(Get-ChildItem $scaffoldDir -Filter '*sp_Sync*.sql' | ForEach-Object FullName)
    foreach ($sqlFile in $childScripts) {
        Write-Host "  applying $(Split-Path $sqlFile -Leaf)"
        Invoke-Sql -Target $provisioningTarget -File $sqlFile | Out-Null
    }

    if (-not $useOwnConnectionStrings) {
        $grantsFile = Write-SqlFile '03_child_grants.sql' (Get-ChildManualGrantsSql $reports $deployment.RuntimeClientId)
        Invoke-Sql -Target $script:childTarget -File $grantsFile | Out-Host
    }
}

Write-Step '5/5  Publishing the Function, starting the first sync and waiting for it'
Push-Location (Join-Path $repoRoot 'SmartTableSyncFunction')
try {
    # The runtime is given, because only local.settings.json (git-ignored, so missing in a fresh
    # clone or a project made from the template) would tell func otherwise
    Invoke-Native { func azure functionapp publish $deployment.FunctionApp --dotnet-isolated }
}
finally {
    Pop-Location
}

$saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
Start-Sync $deployment
if (-not (Wait-ForSync $saved)) { Show-Troubleshooting }
Show-SyncStatus $saved

$syncNowHint = if ($useOwnConnectionStrings) { ' -ParentConnectionString <...> -ChildConnectionString <...>' } else { '' }
Write-Host ''
Write-Host "Done. The Function '$($deployment.FunctionApp)' now syncs every 5 minutes." -ForegroundColor Green
Write-Host "  ./quickstart/deploy.ps1 -Suffix $Suffix -SyncNow$syncNowHint    # sync now and compare" -ForegroundColor Green
Write-Host "  ./quickstart/deploy.ps1 -Suffix $Suffix -Destroy    # remove the Azure resources" -ForegroundColor Green
