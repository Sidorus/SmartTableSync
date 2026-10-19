# SmartTableSync.Scaffold

A command-line tool that reads a parent table's schema and **writes** the SQL that
[SmartTableSync](https://github.com/Sidorus/SmartTableSync) needs, for a person to review and
apply. It never executes anything against a database: it only reads schema metadata and
writes `.sql` files.

For the whole setup, including which identity applies which script, see the
[setup guide](https://github.com/Sidorus/SmartTableSync/blob/main/docs/setup.md).

## Install

From NuGet, once published:

```bash
dotnet tool install --global SmartTableSync.Scaffold
smarttablesync-scaffold --help
```

From source, without installing:

```bash
dotnet run --project SmartTableSync.Scaffold -- --help
```

Or install your own build as a global tool:

```bash
dotnet pack SmartTableSync.Scaffold --output ./nupkg
dotnet tool install --global SmartTableSync.Scaffold --add-source ./nupkg
```

## Commands

| Command | Writes | Apply to | Needs |
|---|---|---|---|
| `--infrastructure` | `00_infrastructure.sql`: the tracking tables `CustomSyncState`, `SyncBatchLog`, `SyncTableConfig` | child, once | nothing |
| `<Table> [<Table> ...]` | per table: `<Table>.sql` when the child table doesn't exist yet, or `<Table>_AddColumns.sql` when the parent has columns the existing child table lacks; `<Table>TableType.sql` (TVP tables; drops and recreates an out-of-date type first); `sp_Sync<Table>Batch.sql` or `sp_Sync<Table>.sql` | child | both connection strings |
| `--change-tracking-setup <Table> [...] [--retention-days <N>]` | `<Table>_ChangeTrackingSetup.sql`: enables Change Tracking on the database (default 2-day retention) and the table, and grants `VIEW CHANGE TRACKING` to `smarttablesync-runtime` | parent, by a DBA | parent connection string |
| `--rowversion-setup <Table> [...] [--column <Name>]` | `<Table>_RowVersionSetup.sql`: adds a rowversion column (default `RowVer`) and its index | parent, by a DBA | parent connection string |

Every command takes `--output <dir>`; the default is `sql/generated/` under the current
directory.

For each table, the per-table command prints the plan it derived, which is the same plan the
sync engine derives at runtime:

```text
=== Product ===
  MergeStrategy=Tvp, BatchSize=1000, BulkCopyTimeoutSeconds=120, IncrementalStrategy=SqlChangeTracking
```

Apply the child files in this order: tables (or added columns), table types, procedures.

For an existing child table, the tool compares it with the parent, the same way Auto mode does.
New columns get the `_AddColumns.sql` script. Changes that could lose data (a changed type or
length, a changed primary key, a removed `NOT NULL` column, a column that now allows `NULL` in
the parent but not in the child) are printed as `NEEDS A MANUAL CHANGE` and left to you.

The Change Tracking script grants `VIEW CHANGE TRACKING` to `smarttablesync-runtime`. If your
runtime identity has another name, change it in the script before applying.

## Connection strings

Set as environment variables:

| Variable | Identity | Rights it needs |
|---|---|---|
| `ParentIntrospectionConnectionString` | parent introspection | on the parent: `VIEW DEFINITION`, `SELECT` on the tables, `VIEW DATABASE STATE` |
| `ChildProvisioningConnectionString` | child provisioning | on the child: `db_ddladmin`. The tool only reads the existing child schema (tables, columns, table types) to decide what to write; it never changes anything |

With your own Microsoft Entra sign-in (through Azure CLI, Visual Studio or environment
credentials):

```powershell
$env:ParentIntrospectionConnectionString = "Server=tcp:<server>.database.windows.net,1433;Database=<parent>;Authentication=Active Directory Default;Encrypt=True"
$env:ChildProvisioningConnectionString  = "Server=tcp:<server>.database.windows.net,1433;Database=<child>;Authentication=Active Directory Default;Encrypt=True"
```

With SQL authentication, use `User Id=...;Password=...` instead of `Authentication=...`.

## Settings that change the plan

The tool reads the same settings the sync engine does, from environment variables, so the
scripts it writes match what the engine will expect. Set them the same way as for the Function:

| Variable | Effect |
|---|---|
| `SmartTableSync__TimestampColumns__<Table>` | the datetime column the Timestamp strategy uses (default `LastUpdate`) |
| `SmartTableSync__AllowFullRescanFor` | comma-separated large tables allowed to fall back to a full re-scan |
| `SmartTableSync__IncrementalStrategyOverrides__<Table>` | force `SqlChangeTracking`, `RowVersion`, `Timestamp` or `FullRescan` for a table |

If a setting can't work for a table, for example a forced strategy the table doesn't support,
the tool reports the same error the engine would and writes nothing for that table.

## Example

```bash
smarttablesync-scaffold --infrastructure --output out/child
smarttablesync-scaffold Product Customer Document --output out/child
smarttablesync-scaffold --change-tracking-setup Product --output out/parent
```
