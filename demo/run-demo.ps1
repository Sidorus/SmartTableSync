<#
.SYNOPSIS
    Deploys the SmartTableSync demo to your Azure subscription and shows a parent database
    being synced into a child database.

.DESCRIPTION
    Default (full setup):
      1. Terraform creates the resource group, SQL server, parent and child databases,
         the runtime managed identity, the Function App and monitoring.
      2. Creates and seeds the demo tables in the parent, grants the runtime identity read
         access, and enables Change Tracking on Product with SmartTableSync.Scaffold.
      3. Generates the child's SQL with SmartTableSync.Scaffold, applies it, and grants the
         runtime identity access to the child.
      4. Publishes the Function from source, triggers one sync and compares both databases.

    -Changes   inserts, updates and deletes rows in the parent, syncs again and compares.
    -SyncNow   only triggers a sync and compares, for changes you made yourself.
    -Destroy   removes every Azure resource the demo created.

    Uses the Azure subscription and account you are signed in with in Azure CLI (az login,
    az account set). It shows both and asks before creating anything. Everything SQL-side runs
    as you (Microsoft Entra, through that sign-in); the Function itself only ever uses its
    managed identity.

.EXAMPLE
    ./run-demo.ps1 -Suffix rs01
    Full setup. "rs01" is your own choice: 3-8 lowercase letters or digits.

.EXAMPLE
    ./run-demo.ps1 -Suffix rs01 -Changes
    Changes rows in the parent, syncs and compares again. Use the same suffix as the setup.

.EXAMPLE
    ./run-demo.ps1 -Suffix rs01 -SyncNow
    Syncs right away after you changed parent rows yourself, instead of waiting for the timer.

.EXAMPLE
    ./run-demo.ps1 -Suffix rs01 -Destroy
    Removes everything the demo created.
#>

#Requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'Setup')]
param(
    # Short unique suffix for globally unique Azure names: 3-8 lowercase letters or digits
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{3,8}$')]
    [string] $Suffix,

    # The SQL server's Microsoft Entra admin. Defaults to the account you're signed in with in
    # Azure CLI, which is also the identity Terraform makes the admin, so normally leave it out.
    [string] $SqlAdminLogin,

    [string] $Location = 'swedencentral',

    # Public IP allowed through the SQL firewall; detected when not given
    [string] $ClientIp,

    [Parameter(ParameterSetName = 'Changes')]
    [switch] $Changes,

    [Parameter(ParameterSetName = 'SyncNow')]
    [switch] $SyncNow,

    [Parameter(ParameterSetName = 'Destroy')]
    [switch] $Destroy,

    # Seconds to wait for the child to catch up after a sync is triggered
    [int] $SyncTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$terraformDir = Join-Path $PSScriptRoot 'terraform'
$sqlDir = Join-Path $PSScriptRoot 'sql'
$generatedDir = Join-Path $PSScriptRoot 'generated'
$demoTables = @('Product', 'Customer', 'Document')

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

# Runs a native command and stops the script when it fails
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

function Get-TerraformVariables {
    @(
        "-var=suffix=$Suffix",
        "-var=location=$Location",
        "-var=sql_admin_login=$SqlAdminLogin",
        "-var=client_ip=$ClientIp"
    )
}

function Get-DemoOutputs {
    $json = terraform "-chdir=$terraformDir" output -json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read Terraform outputs. Run the full setup first.' }
    $outputs = $json | ConvertFrom-Json
    [pscustomobject]@{
        ResourceGroup  = $outputs.resource_group_name.value
        FunctionApp    = $outputs.function_app_name.value
        FunctionHost   = $outputs.function_app_hostname.value
        SqlServer      = $outputs.sql_server_fqdn.value
        ParentDatabase = $outputs.parent_database_name.value
        ChildDatabase  = $outputs.child_database_name.value
    }
}

# Runs a SQL file (or a query) as the signed-in Azure CLI user. Serverless databases pause
# when idle and refuse the first connection while resuming, so failures are retried.
function Invoke-Sql {
    param(
        [Parameter(Mandatory)] [string] $Database,
        [string] $File,
        [string] $Query,
        [switch] $Raw
    )

    $arguments = @('-S', $script:demo.SqlServer, '-d', $Database,
        '--authentication-method', 'ActiveDirectoryDefault', '-l', '60', '-b')
    if ($File) { $arguments += @('-i', $File) } else { $arguments += @('-Q', $Query) }
    # Raw: no headers, trimmed values, "|" between columns, for comparing results
    if ($Raw) { $arguments += @('-h', '-1', '-W', '-s', '|') }

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $output = & sqlcmd @arguments 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -eq 0) { return @($output | Where-Object { $_.Trim() }) }
        if ($attempt -lt 4) {
            Write-Host "  SQL attempt $attempt failed (a paused database may still be resuming); retrying in 20 s..." -ForegroundColor Yellow
            Start-Sleep -Seconds 20
        }
    }
    throw "sqlcmd failed on ${Database}:`n$($output -join "`n")"
}

# Runs SmartTableSync.Scaffold with your Entra sign-in; it only reads schemas and writes files
function Invoke-Scaffold([string[]] $scaffoldArgs) {
    $env:ParentIntrospectionConnectionString = Get-AdminConnectionString $script:demo.ParentDatabase
    $env:ChildProvisioningConnectionString = Get-AdminConnectionString $script:demo.ChildDatabase
    try {
        Invoke-Native { dotnet run --project (Join-Path $repoRoot 'SmartTableSync.Scaffold') -- @scaffoldArgs }
    }
    finally {
        Remove-Item Env:ParentIntrospectionConnectionString, Env:ChildProvisioningConnectionString -ErrorAction SilentlyContinue
    }
}

# Empties a folder of generated scripts before the scaffold tool writes it again, so a re-run
# applies only what the tool wrote this time, never a CREATE TABLE left from an earlier run
function Clear-GeneratedFolder([string] $folder) {
    if (Test-Path $folder) {
        Get-ChildItem $folder -Filter '*.sql' | Remove-Item
    }
}

function Get-AdminConnectionString([string] $database) {
    "Server=tcp:$($script:demo.SqlServer),1433;Database=$database;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
}

# Starts the timer function now instead of waiting for its 5-minute schedule
function Start-Sync {
    $masterKey = az functionapp keys list --resource-group $script:demo.ResourceGroup --name $script:demo.FunctionApp --query masterKey --output tsv
    if ($LASTEXITCODE -ne 0 -or -not $masterKey) { throw 'Could not read the Function App master key.' }

    Invoke-RestMethod -Method Post `
        -Uri "https://$($script:demo.FunctionHost)/admin/functions/SmartTableSync" `
        -Headers @{ 'x-functions-key' = $masterKey } `
        -ContentType 'application/json' -Body '{}' | Out-Null
}

# Compares row counts and checksums per table until parent and child match or time runs out
function Wait-ForSync {
    $fingerprints = Join-Path $sqlDir '05_table_fingerprints.sql'
    $deadline = (Get-Date).AddSeconds($SyncTimeoutSeconds)

    do {
        Start-Sleep -Seconds 15
        $parentRows = Invoke-Sql -Database $script:demo.ParentDatabase -File $fingerprints -Raw
        $childRows = try { Invoke-Sql -Database $script:demo.ChildDatabase -File $fingerprints -Raw } catch { @() }

        $results = foreach ($line in $parentRows) {
            $table = ($line -split '\|')[0]
            $childLine = $childRows | Where-Object { ($_ -split '\|')[0] -eq $table }
            [pscustomobject]@{
                Table  = $table
                Parent = $line
                Child  = $childLine ?? '(not synced yet)'
                Match  = $line -eq $childLine
            }
        }

        $pending = @($results | Where-Object { -not $_.Match })
        Write-Host "  $(Get-Date -Format HH:mm:ss)  in sync: $(@($results).Count - $pending.Count)/$(@($results).Count)"
    } while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline)

    $results | Format-Table Table, Match, Parent, Child -AutoSize | Out-Host
    if ($pending.Count -gt 0) {
        Write-Host ('Not every table matches yet. Deletes in Customer (RowVersion) and Document (Timestamp) ' +
            'arrive at the next delete reconciliation, which runs on a fresh instance''s first sync and then hourly. ' +
            'Run with -SyncNow later to compare again, or check the logs in Application Insights.') -ForegroundColor Yellow
    }
}

function Show-SyncStatus {
    Write-Step 'What SmartTableSync decided and recorded (child database)'
    Invoke-Sql -Database $script:demo.ChildDatabase -File (Join-Path $sqlDir '06_child_sync_status.sql') | Out-Host
}

# --- Prerequisites ----------------------------------------------------------------------

Assert-Tool 'az' 'Install the Azure CLI: https://learn.microsoft.com/cli/azure/install-azure-cli'
Assert-Tool 'terraform' 'Install Terraform: https://developer.hashicorp.com/terraform/install'

if (-not $Destroy) {
    $goSqlcmdHint = 'Install go-sqlcmd (winget install sqlcmd): https://learn.microsoft.com/sql/tools/sqlcmd/go-sqlcmd-utility'
    Assert-Tool 'sqlcmd' $goSqlcmdHint
    Assert-Tool 'dotnet' 'Install the .NET 10 SDK: https://dotnet.microsoft.com/download'
    Assert-Tool 'func' 'Install Azure Functions Core Tools v4: https://learn.microsoft.com/azure/azure-functions/functions-run-local'

    # The older ODBC sqlcmd that ships with SQL Server can't sign in with your Azure CLI
    # identity; only go-sqlcmd understands --version and --authentication-method.
    & sqlcmd --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The 'sqlcmd' on your PATH ($((Get-Command sqlcmd).Source)) is the older ODBC version. $goSqlcmdHint, and make sure it comes first on PATH."
    }
}

$account = az account show --query '{name:name, id:id, user:user.name}' --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Not signed in. Run "az login" and "az account set --subscription <id>" first.' }

# Terraform deploys into exactly the subscription shown below, whatever its provider version
# would pick by itself, and the SQL admin is the signed-in account unless given explicitly.
$env:ARM_SUBSCRIPTION_ID = $account.id
if (-not $SqlAdminLogin) { $SqlAdminLogin = $account.user }

if (-not $ClientIp) {
    $ClientIp = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
}

# --- Destroy ----------------------------------------------------------------------------

if ($Destroy) {
    Write-Step "Removing the demo from subscription '$($account.name)'"
    Invoke-Native { terraform "-chdir=$terraformDir" destroy -input=false @(Get-TerraformVariables) }
    return
}

# --- Changes ----------------------------------------------------------------------------

if ($Changes) {
    $script:demo = Get-DemoOutputs

    Write-Step 'Inserting, updating and deleting rows in the parent (04_parent_changes.sql)'
    Invoke-Sql -Database $demo.ParentDatabase -File (Join-Path $sqlDir '04_parent_changes.sql') | Out-Host

    Write-Step 'Triggering a sync and waiting for the child to catch up'
    Start-Sync
    Wait-ForSync
    Show-SyncStatus
    return
}

# --- Sync now ---------------------------------------------------------------------------

if ($SyncNow) {
    $script:demo = Get-DemoOutputs

    Write-Step 'Triggering a sync and waiting for the child to catch up'
    Start-Sync
    Wait-ForSync
    Show-SyncStatus
    return
}

# --- Full setup -------------------------------------------------------------------------

Write-Host ''
Write-Host "Subscription : $($account.name) ($($account.id))"
Write-Host "Signed in as : $($account.user)"
Write-Host "SQL admin    : $SqlAdminLogin"
Write-Host "Region       : $Location"
Write-Host "Client IP    : $ClientIp (allowed through the SQL firewall)"
$answer = Read-Host 'Create the demo resources in this subscription? Terraform shows its plan next. (y/N)'
if ($answer -notin @('y', 'Y', 'yes')) {
    Write-Host 'Cancelled.'
    return
}

Write-Step '1/6  Creating the Azure resources (Terraform asks you to confirm its plan)'
Invoke-Native { terraform "-chdir=$terraformDir" init -input=false }
Invoke-Native { terraform "-chdir=$terraformDir" apply -input=false @(Get-TerraformVariables) }
$script:demo = Get-DemoOutputs

Write-Step '2/6  Parent: demo tables, seed rows and read access for the runtime identity'
Invoke-Sql -Database $demo.ParentDatabase -File (Join-Path $sqlDir '01_parent_schema.sql') | Out-Host
Invoke-Sql -Database $demo.ParentDatabase -File (Join-Path $sqlDir '02_parent_grants.sql') | Out-Host

Write-Step '3/6  Parent: Change Tracking on Product, generated by SmartTableSync.Scaffold'
$parentGeneratedDir = Join-Path $generatedDir 'parent'
Clear-GeneratedFolder $parentGeneratedDir
Invoke-Scaffold @('--change-tracking-setup', 'Product', '--output', $parentGeneratedDir)
Invoke-Sql -Database $demo.ParentDatabase -File (Join-Path $parentGeneratedDir 'Product_ChangeTrackingSetup.sql') | Out-Host

Write-Step '4/6  Child: tracking tables, synced tables and merge procedures, generated by SmartTableSync.Scaffold'
$childGeneratedDir = Join-Path $generatedDir 'child'
Clear-GeneratedFolder $childGeneratedDir
Invoke-Scaffold @('--infrastructure', '--output', $childGeneratedDir)
Invoke-Scaffold (@($demoTables) + @('--output', $childGeneratedDir))

# Tables first (new ones, or the changes an existing one needs), then the table types the
# procedures use, then the procedures. On a re-run the scaffold tool writes no CREATE TABLE for a
# table that exists, only the scripts that bring it in line with the parent.
$childScripts = @(Join-Path $childGeneratedDir '00_infrastructure.sql') +
    @($demoTables | ForEach-Object { Join-Path $childGeneratedDir "$_.sql" } | Where-Object { Test-Path $_ }) +
    @(Get-ChildItem $childGeneratedDir -Filter '*_AddColumns.sql' | ForEach-Object FullName) +
    @(Get-ChildItem $childGeneratedDir -Filter '*_AlterColumns.sql' | ForEach-Object FullName) +
    @(Get-ChildItem $childGeneratedDir -Filter '*TableType.sql' | ForEach-Object FullName) +
    @(Get-ChildItem $childGeneratedDir -Filter 'sp_Sync*.sql' | ForEach-Object FullName)
foreach ($sqlFile in $childScripts) {
    Write-Host "  applying $(Split-Path $sqlFile -Leaf)"
    Invoke-Sql -Database $demo.ChildDatabase -File $sqlFile | Out-Null
}

# A column fixed by <Table>_AlterColumns.sql (for example a decimal created as decimal(18,0) by
# an earlier version) still holds the values that were rounded. As that script describes, clearing
# the table's tracking rows makes the next sync copy it again with exact values. The demo table
# names are plain dbo names, so the table name is the file name's prefix.
foreach ($alterFile in Get-ChildItem $childGeneratedDir -Filter '*_AlterColumns.sql') {
    $table = $alterFile.BaseName -replace '_AlterColumns$', ''
    Write-Host "  $table had columns with the wrong precision or scale: it is copied again on the next sync"
    Invoke-Sql -Database $demo.ChildDatabase -Query "DELETE FROM dbo.SyncBatchLog WHERE TableName = N'$table'; DELETE FROM dbo.CustomSyncState WHERE TableName = N'$table';" | Out-Null
}

Invoke-Sql -Database $demo.ChildDatabase -File (Join-Path $sqlDir '03_child_grants.sql') | Out-Host

Write-Step '5/6  Publishing the Function from source'
Push-Location (Join-Path $repoRoot 'SmartTableSyncFunction')
try {
    Invoke-Native { func azure functionapp publish $demo.FunctionApp }
}
finally {
    Pop-Location
}

Write-Step '6/6  Triggering the first sync and waiting for the child to catch up'
Start-Sync
Wait-ForSync
Show-SyncStatus

Write-Host ''
Write-Host 'Done. Next:' -ForegroundColor Green
Write-Host "  ./demo/run-demo.ps1 -Suffix $Suffix -Changes    # sync inserts, updates and deletes" -ForegroundColor Green
Write-Host "  ./demo/run-demo.ps1 -Suffix $Suffix -Destroy    # remove everything" -ForegroundColor Green
