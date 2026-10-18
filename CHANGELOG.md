# Changelog

All notable changes to SmartTableSync. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/). Still open: see
[Known limitations](README.md#known-limitations).

## [Unreleased]

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

## [0.3.0]

SQL Server Change Tracking as the third incremental strategy, the strategy registry and
template-method runners, a minimal public API, and production hardening (run lock, connection
retries, delete reconciliation intervals). See
[ADR-0016](docs/adr/0016-implement-change-tracking-as-third-strategy.md) and
[ADR-0017](docs/adr/0017-strategy-registry-and-template-method-runners.md).
