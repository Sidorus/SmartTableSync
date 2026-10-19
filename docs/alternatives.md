# The alternatives and what they cost

SmartTableSync was built to solve a real production problem, and it runs in production. This page
compares it with the options Microsoft lists for replacing Azure SQL Data Sync, and shows how the
cost estimate on the [main page](../README.md#why-not-the-alternatives) is calculated.

## The alternatives

Microsoft's
[Data Sync migration guide](https://learn.microsoft.com/azure/azure-sql/database/sql-data-sync-retirement-migration)
lists the alternatives. None of them is a ready-made, table-level, one-way sync from one Azure SQL
Database into another that also keeps the child's own tables:

| Alternative | Why it doesn't fit this case | Cost |
|---|---|---|
| Transactional replication | Azure SQL Database can only be a subscriber, never the publisher | — |
| Read replicas, active geo-replication, database copy | Whole database only, read-only or a one-time snapshot; the child can't have its own tables | A full secondary database |
| Fabric mirroring | Replicates into OneLake as read-only Delta tables, not into another Azure SQL Database; needs a running Fabric capacity | F2 capacity ≈ **$320/month** |
| Azure Data Factory, incremental copy | Works, but every run is billed, and deletes and upserts need extra pipeline design | ≈ **$170 per table per month** at a 5-minute schedule |
| Azure Functions | Named in the guide, but you write the sync yourself | This project is that sync, ready-made, with an example Function host |

## Data Factory: the estimate

Per table, every 5 minutes (8,640 runs a month):

- **Copy activity:** at least 4 DIUs (data integration units), billed per minute and rounded up,
  even when one row changed: 4 DIU × 1 min × $0.25/DIU-hour ≈ $0.017 a run, so **≈ $144**.
- **Orchestration:** watermark lookup, copy and watermark update are 3 activity runs at $1 per
  1,000, so **≈ $26**.
- That's ≈ **$170 per table**, ≈ **$1,700 a month for 10 tables**, before deletes are handled.

## SmartTableSync: the estimate

All tables are synced in one Function run: 8,640 executions a month, which usually stays inside
the Azure Functions monthly free grant. What remains is the storage account and logging, typically
a few dollars a month. In your own host (a background service, Hangfire, Quartz.NET) it costs
nothing beyond the host you already run.

## Sources

Prices are Microsoft list prices from the
[Azure Retail Prices API](https://learn.microsoft.com/rest/api/cost-management/retail-prices/azure-retail-prices)
(West Europe, USD, pay-as-you-go, October 2026). The billing rules come from
[Data Factory DIUs](https://learn.microsoft.com/azure/data-factory/copy-activity-performance-features#data-integration-units),
[integration runtime charges](https://learn.microsoft.com/azure/data-factory/better-understand-different-integration-runtime-charges)
and [Fabric mirroring cost](https://learn.microsoft.com/fabric/mirroring/overview#cost-of-mirroring).
Check your own numbers in the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/).
