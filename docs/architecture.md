# Project layout and architecture

For contributors, and anyone who wants to know where things are in the code.

## Projects

| Project | What it is |
|---|---|
| `SmartTableSync.Core` | The engine (NuGet package `SmartTableSync`) |
| `SmartTableSyncFunction` | Example Azure Functions host (timer trigger, every 5 minutes) |
| `SmartTableSync.Worker` | Container host with its `Dockerfile`: syncs on an interval, or once (`--once`) for a scheduler; see [containers](guides/containers.md) |
| `SmartTableSync.Scaffold` | CLI (`dotnet tool`, `smarttablesync-scaffold`) that writes reviewable SQL; never executes DDL |
| `SmartTableSync.Tests` | Unit tests |
| `SmartTableSync.IntegrationTests` | Tests against a real, disposable SQL Server (Testcontainers; needs Docker), including syncs that run as the three identities with exactly the grants in `sql/07_permissions.sql` |
| `demo/` | Terraform, SQL and a script that deploy and run the whole setup on Azure, with its own databases and demo tables |
| `quickstart/` | Terraform and a script that set up the sync between two Azure SQL databases you already have |
| `sql/` | The reference grant script, `07_permissions.sql` |
| `docs/` | Guides, reference pages and the architecture decision records |

## Inside the engine

`SmartTableSync.Core` is organized by feature: `Abstractions`, `Hosting`, `Orchestration`,
`Planning`, `Strategies/<strategy>`, `Merge`, `Reconciliation`, `State`, `Sql`, `Diagnostics`.

Each incremental strategy has its own folder under `Strategies/` holding the strategy (when it
applies, which column it reads) and its runner. Every runner derives from
`ChangeWindowSyncRunner<TPosition>` in `Strategies/Abstractions/`, which owns paging, deletes,
merging and the position rule; see
[ADR-0017](adr/0017-strategy-registry-and-template-method-runners.md).

Where each step of a run lives in the code: [how a sync run works](how-it-works.md#where-each-step-lives-in-the-code).

## Architecture decisions

Significant decisions (what was chosen, what was rejected, and why) are recorded as
[Architecture Decision Records](adr/README.md).
