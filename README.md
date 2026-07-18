# SmartTableSync

A .NET 10 Azure Function that fills a gap Azure SQL Data Sync can't cover: keeping specific tables synchronized between two Azure SQL databases when their schema or row size falls outside what Data Sync supports.

## The problem

Azure SQL Database doesn't support cross-database queries — you can't `JOIN` or even `SELECT` across two separate Azure SQL databases in one query, unlike on-prem SQL Server or Azure SQL Managed Instance. **Azure SQL Data Sync** is Microsoft's managed answer to that: it replicates tables between databases on a schedule, no custom code required.

Data Sync works well for most tables, but it has two hard limits:

- It can't use an `nvarchar(max)` or `varbinary(max)` column as part of an **indexed key** — unsupported type for that role.
- Each synced row must stay under a **24 MB batch limit**.

Any table that hits either limit simply can't be included in Data Sync's sync group. Real-world schemas frequently have a handful of tables like this — a search-key column that's `nvarchar(max)`, or a document/media table with large binary content. SmartTableSync exists to synchronize exactly those exception tables, using a custom transport layer and stored-procedure-based `MERGE` instead of Data Sync's built-in replication.

## What it does

A timer-triggered Azure Function that:

- Runs on a 5-minute schedule
- Connects to both databases directly (since the platform can't do it in SQL)
- **Initial sync**: bulk-copies each table from empty, using keyset pagination (never `OFFSET`) so a crash or timeout can resume from the last completed batch instead of restarting from row 1
- **Incremental sync**: after the first full sync, only pulls rows changed since the last run and merges them via a stored procedure `MERGE` — TVP-batched where there's no risk of a per-row error blocking the batch, row-by-row where isolation matters (e.g. tables with foreign-key timing dependencies on other, Data-Sync-managed tables)
- **Delete reconciliation**: periodically removes rows from the target that no longer exist in the source
- Emits custom metrics (rows synced/errored/deleted, duration, lag) to Application Insights per table per run

## Example tables

The repo ships with three representative example tables, each demonstrating a different reason a table might need this instead of Data Sync:

| Table | Why it can't use Data Sync |
|---|---|
| `ProductAttributeValue` | Has an `nvarchar(max)` column used as an indexed key |
| `ProductCatalogItem` | Same — a wide table with an `nvarchar(max)` search-key column |
| `DocumentAsset` | Rows can exceed the 24 MB batch limit (binary content columns) |

## Getting started

```bash
cd SmartTableSyncFunction
dotnet build
```

Deploy the SQL scripts under `sql/` to your target database in numbered order, configure `ParentDbConnectionString` / `ChildDbConnectionString`, and publish the Function.
