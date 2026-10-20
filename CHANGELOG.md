# Changelog

All notable changes to SmartTableSync. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/). Still open: see
[Known limitations](docs/limitations.md).

## [Unreleased]

### Fixed

- The quick start in a project created from the `smarttablesync-azure` template stopped at
  publishing the Function ("Can't determine project language from files"): `func` read the
  runtime from `local.settings.json`, which such a project, like a fresh clone, doesn't have.
  `deploy.ps1` and the demo's `run-demo.ps1` now pass `--dotnet-isolated`.

### Changed

- Turning on Change Tracking is now the first step of the quick start, in the README's *Get
  started*, the NuGet guide and the template's READMEs.

## [0.5.1] - 2026-10-20

The quick start now comes from NuGet: a new template package creates the Azure Function and the
deploy script as a project of your own, so there's no need to clone the repository. The library
and the scaffold tool are unchanged apart from the version.

### Added

- **SmartTableSync.Templates**, a `dotnet new` template package. `dotnet new install
  SmartTableSync.Templates`, then `dotnet new smarttablesync-azure -n MySync`, creates a project
  of your own with the Azure Function (using the SmartTableSync package), the quick start script
  and its Terraform, and the scaffold tool as a local tool, all in the template's version. The
  quick start no longer needs a clone of the repository.

### Changed

- `quickstart/deploy.ps1` runs the scaffold tool from NuGet (the project's
  `.config/dotnet-tools.json`) when it runs in a project created from the template, and from
  source in the repository, as before.
- The Manual mode guide and the setup guide install the scaffold tool from NuGet instead of running
  it from the repository.

### Upgrading

Nothing to change: no new settings, rights or schema scripts. Update with
`dotnet add package SmartTableSync` and `dotnet tool update --global SmartTableSync.Scaffold`.

### Tested

- Unit and integration tests (298 and 72) pass.
- A project created from the template builds against the release's SmartTableSync package with no
  warnings, and runs the release's scaffold tool through its tool manifest; the release workflow
  checks this before publishing.
- The quick start script's scaffold step runs both in a project created from the template and in
  the repository.

## [0.5.0] - 2026-10-20

The first release on NuGet, as [SmartTableSync](https://www.nuget.org/packages/SmartTableSync)
and [SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold). It syncs
tables in any schema and tables without a primary key, copies `decimal` values exactly, runs in a
Linux container as well as in Azure Functions, and connects to Azure SQL with a managed identity
using the one package. A quick start sets up the sync between two existing Azure SQL databases with
one script.

### Added

- **Publishing to NuGet** (`.github/workflows/publish.yml`). Pushing a tag such as `v0.5.0`
  builds, tests and publishes `SmartTableSync` and `SmartTableSync.Scaffold` with that version,
  through nuget.org trusted publishing (no API key), after a maintainer approves the run. The
  version comes from the tag only; the project files hold a local default, `0.0.0-dev`. See
  [releasing](docs/releasing.md).

- **Container host for Linux** (`SmartTableSync.Worker` and its `Dockerfile`). It keeps running
  and syncs every `SmartTableSync:Worker:IntervalMinutes` (default 5), or syncs once and exits
  with `--once`, for a Kubernetes CronJob or an Azure Container Apps job. Settings come from
  environment variables, logs go to standard output, and metrics go over OTLP when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set. CI builds the image. See
  [run it in a container](docs/guides/containers.md).

- **Tables in schemas other than `dbo`.** Name them `schema.Table` in `SmartTableSync:TableList`,
  the per-table settings and the scaffold tool (a plain `Table` still means `dbo.Table`). The
  child keeps the parent's schema; the table's procedures and table type are created in that
  schema, so tables with the same name in two schemas don't collide. The tracking tables stay in
  `dbo`. See [ADR-0020](docs/adr/0020-tables-in-schemas-other-than-dbo.md).
- **Tables without a primary key.** Such a table is keyed by its best unique index (all key
  columns `NOT NULL`, not filtered, not disabled; fewest columns, then lowest `index_id`), or by
  the columns in the new `SmartTableSync:KeyColumns:<Table>` setting (`"ColA,ColB"`), which are
  checked for duplicate values whenever the table is set up. The child table gets that key as its
  primary key. Change Tracking still requires a real primary key. Tables with a primary key are
  unaffected (same key, same schema fingerprint). See
  [ADR-0021](docs/adr/0021-key-fallback-for-tables-without-a-primary-key.md).

- **Quick start for existing databases** (`quickstart/`). One script takes a parent and a child
  Azure SQL database you already have and a table list. It shows which strategy each table will
  get, creates the Function App with its managed identities (Terraform, nothing on the databases),
  creates the database users by SID with only the rights each needs, in Auto or Manual mode, and
  runs and checks the first sync. It also has `-DryRun`, `-SyncNow` and `-Destroy`. See
  [quickstart/README.md](quickstart/README.md).

### Changed

- **The `SmartTableSync` package includes Microsoft Entra sign-in.** It now depends on
  `Microsoft.Data.SqlClient.Extensions.Azure`, so managed identities and every other
  `Authentication=Active Directory ...` connection string work with this one package; a host no
  longer adds that package itself (one that already did can remove it). Hosts that only use SQL
  logins or Windows authentication get the Azure sign-in libraries too, unused.
- The scaffold tool prefixes the files of a non-dbo table with the schema
  (`sales.sp_SyncOrderBatch.sql`); file names for dbo tables are unchanged.
- A table name with more than one dot, or longer than 100 characters, is rejected at startup with
  a clear message. Names that contain a dot were never meant to work and are now read as
  `schema.Table`.
- A table with no usable key at all now stops when it is set up, with a message naming
  `SmartTableSync:KeyColumns:<Table>`, instead of failing later in its first page query.
- **Documentation by task.** The README starts with a guide for each way to run SmartTableSync:
  the demo, the quick start, [Manual mode](docs/guides/manual-mode.md) and
  [Auto mode](docs/guides/auto-mode.md) step by step (every scaffold command and SQL statement),
  and [your own app](docs/guides/hosting.md) (background service, Hangfire, Quartz.NET, with
  complete `Program.cs` files). Known limitations moved to [their own page](docs/limitations.md);
  the README keeps a summary and links to it and to the roadmap.

### Fixed

- **`decimal` and `numeric` values were rounded in the child.** Precision and scale weren't read
  from the parent, so the scaffold tool and Auto mode created `decimal(10,2)` as `decimal`,
  which SQL Server makes `decimal(18,0)`: 99.95 arrived as 100, with no error. Tables, table types
  and procedures now get the parent's exact type, for `datetime2(n)`, `time(n)` and
  `datetimeoffset(n)` too. A `decimal` or `numeric` column whose precision or scale differs from
  the parent is now detected, and the scaffold tool writes `<Table>_AlterColumns.sql` for it
  (see *Upgrading* below). This also closes the former limitation that such a change in the
  parent wasn't detected.
- Measuring the largest value of `(max)` columns while profiling a table queried it without a
  schema, so it depended on the identity's default schema.
- Running the demo a second time failed with *"There is already an object named 'Product'"*:
  the script applied a `CREATE TABLE` script left from the first run. It now empties its
  generated-scripts folders first, applies `*_AddColumns.sql` and `*_AlterColumns.sql`, and has a
  table fixed by `*_AlterColumns.sql` copied again on the next sync.
- The example Function sent only warnings and errors to Application Insights, and no
  `smarttablesync.*` metrics at all: the Application Insights SDK keeps only warnings from the
  worker by default, and no exporter was registered for the `SmartTableSync` meter. Its logs now
  go through the Functions host, so they arrive with their level and appear in the portal's Log
  stream like any Function's, and the metrics go through OpenTelemetry with the Azure Monitor
  exporter. See [logs and metrics](docs/setup.md#logs-and-metrics).

### Upgrading

- **Tables with `decimal` or `numeric` columns** are planned again on the first run after the
  upgrade (their schema fingerprint now includes precision and scale; other tables keep theirs).
  A child created by an earlier version has those columns as `decimal(18,0)` and rounded values:
  - The table stops with *"column ... is decimal(18,0) in the child but decimal(10,2) in the
    parent"*. Run the scaffold tool for it and apply `<Table>_AlterColumns.sql` to the child.
  - **Auto mode** then copies the table again by itself on the next run, so every value is exact.
  - **Manual mode:** also run the two `DELETE` statements at the end of that script, so the next
    run copies the table again; then apply the regenerated table type and procedure scripts.
- **Hosts that added `Microsoft.Data.SqlClient.Extensions.Azure` themselves** can remove it; the
  package now brings it.

### Tested

- **Automated, on every change:** 298 unit tests and 72 integration tests against a real SQL
  Server 2022 (Testcontainers), including syncs that run as the three identities with exactly the
  documented grants, Auto mode creating and evolving the child's tables, tables in other schemas
  and without a primary key, and exact `decimal` values. CI also builds the container image and
  checks the Terraform, the NuGet packages for known vulnerabilities, and the code with Semgrep
  and CodeQL.
- **On Azure, with the demo** (Azure SQL Database, Azure Functions Flex Consumption, managed
  identity): about 20,000 inserted, updated and deleted rows per table on Change Tracking,
  RowVersion and Timestamp tables, plus key changes, deleted and re-inserted rows, rows inserted
  and deleted between two runs, the same row updated 50 times, NULL and empty values, Unicode,
  `decimal` limits and rows over 1 MB. Parent and child matched row by row.
- **On Azure, with the quick start** in Auto mode on two new databases: four tables on four
  strategies, then inserts, updates, deletes and a column added in the parent, which Auto mode
  added to the child. Parent and child matched row by row.
- **The container host**, run once and running continuously, against SQL Server in Docker.
- **The NuGet packages:** the release workflow published them, they install from nuget.org, the
  [NuGet guide](docs/guides/nuget.md) was followed step by step, and an app that references only
  `SmartTableSync` signs in to Azure SQL with Microsoft Entra.

## [0.4.0] - 2026-10-18

### Fixed

| Problem | Fix |
|---|---|
| Signing in with **Managed Identity** or any `Authentication=Active Directory ...` connection string failed with *"Cannot find an authentication provider"*: Microsoft.Data.SqlClient 7.0 moved those providers into a separate package. | The Function and the scaffold tool reference `Microsoft.Data.SqlClient.Extensions.Azure`. Other hosts need it too (README). |
| The scaffold tool's `CREATE TABLE` failed with SQL error 1709 for any table **without** a large-object column, because `TEXTIMAGE_ON` was always added. | The clause is added only when the table has a `(max)`, `xml` or legacy LOB column. |
| Under the documented least-privilege grants, the **first sync of every table failed**: profiling reads `sys.dm_db_partition_stats`, which needs `VIEW DATABASE STATE`. | `sql/07_permissions.sql` grants it, with `VIEW DEFINITION` on the synced tables, to the runtime and parent-introspection identities. |
| Under the documented grants, **every table merged in TVP batches failed**: passing a table-valued parameter also needs `EXECUTE` on its table type. | `sql/07_permissions.sql` grants `EXECUTE` on each table type. |
| Tables with an **`IDENTITY` column** needed `ALTER` on the child table for the always-on runtime identity (to `SET IDENTITY_INSERT`), the one exception to "the runtime has no DDL rights". | New child tables are created **without** `IDENTITY`; key values are copied from the parent as plain values ([ADR-0018](docs/adr/0018-child-tables-without-identity.md)). Existing child tables with `IDENTITY` keep working as before. |
| In **Auto mode**, a column added to the parent broke the table on every run: the child table wasn't altered, the table type couldn't change, and the regenerated procedure referenced a missing column. | Auto mode adds new columns to the child, recreates an out-of-date table type and its procedure, and re-scans the table once so existing rows get the new values ([ADR-0019](docs/adr/0019-auto-mode-schema-evolution.md)). |
| In **Auto mode**, the new plan was saved **before** the child DDL ran, so a failed DDL step was never retried: the next run saw a matching plan and skipped it. | The plan is saved only after the child changes (and the re-scan) succeeded; a failure is retried on the next run. |
| A table seen for the first time read its schema metadata twice. | Profiled once. |

### Added

- **Least-privilege integration tests**: the three identities are created with no rights, `sql/07_permissions.sql` is run as written (Manual and Auto mode), and each identity then does its real job. Removing any documented grant fails a test.
- **Auto-mode schema evolution tests** under least privilege: an added column (including one that only the re-scan can deliver), a recreated table type, and a risky change that is refused on every run.
- **Scaffold tool, existing child tables**: writes `<Table>_AddColumns.sql` for new parent columns and a drop-and-recreate table type script when the type is out of date; reports changes that need a person as `NEEDS A MANUAL CHANGE`.
- **CI pipeline** (`.github/workflows/ci.yml`): build with a vulnerable-package check and packing of both packages, all unit and integration tests, a Terraform check for the demo, and a pinned Semgrep scan.
- **One-script Azure demo** (`demo/`): Terraform for an Entra-only SQL server, parent and child databases, managed identity and Function App; demo tables for three strategies; scaffold, grants, publish, sync and compare.
- **Documentation**: a [setup guide](docs/setup.md) (identities and their exact rights, Manual and Auto mode, troubleshooting), a [scaffold tool README](SmartTableSync.Scaffold/README.md) shipped in its package, a comparison with Microsoft's Data Sync alternatives and their cost, and ADRs 0018 and 0019.

### Changed

- **Breaking for code that used the engine's namespaces:** `SmartTableSync.Core` is organized by feature (`Abstractions`, `Hosting`, `Orchestration`, `Planning`, `Strategies/<strategy>`, `Merge`, `Reconciliation`, `State`, `Sql`, `Diagnostics`), and public types moved namespace (for example `ISyncOrchestrator` is now in `SmartTableSync.Abstractions`). Hosts that only call `AddSmartTableSync()` are unaffected.
- License changed from MIT to **Apache-2.0**, with a `NOTICE` file in both packages.
- In Auto mode, a parent schema change on an already-synced table runs **one full re-scan** of that table (metric reason `schema_changed`).
- SQL built from table and column names (which can't be parameters) is quoted by `SqlIdentifier` and marked as reviewed for Semgrep's SQL-injection rule; every value is a parameter.

### Upgrading from 0.3.0

- Hosts that only call `AddSmartTableSync()` are unaffected.
- Code that uses the engine's types directly needs new `using` lines, because the public types
  moved namespace (for example `ISyncOrchestrator` is now in `SmartTableSync.Abstractions`).
- Existing child tables keep working as they are, including ones created with `IDENTITY`.

## [0.3.0]

SQL Server Change Tracking as the third incremental strategy, the strategy registry and
template-method runners, a minimal public API, and production hardening (run lock, connection
retries, delete reconciliation intervals). See
[ADR-0016](docs/adr/0016-implement-change-tracking-as-third-strategy.md) and
[ADR-0017](docs/adr/0017-strategy-registry-and-template-method-runners.md).

### Upgrading from 0.2.0

- Not drop-in compatible for code that used engine types directly: the public API is now only
  what a host needs (`AddSmartTableSync`, the settings types, `ISyncOrchestrator`, and the
  replaceable services `ISyncRunLock`, `IDbConnectionFactory`, `ISyncMetricsReporter`).
- Hosts that only call `AddSmartTableSync()` are unaffected; the
  `SmartTableSync:ChangeTrackingColumns` setting keeps working.
