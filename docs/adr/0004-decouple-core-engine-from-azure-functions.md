# ADR-0004: Decouple the sync engine from Azure Functions into a standalone library

## Status
Accepted

## Date
2026-07-18

## Context
The engine started as logic embedded directly in the Azure Functions host project. Only one
class (`Functions/SmartTableSyncTimerFunction.cs`) actually needs the Azure Functions Worker
SDK - orchestration, sync logic, schema introspection, and planning have nothing
Azure-Functions-specific about them.

## Decision
Extract everything except the timer-trigger entry point into `SmartTableSync.Core`, a plain
class library with no Azure Functions dependency, referenced by the thin
`SmartTableSyncFunction` host. Package `Core` (and the `SmartTableSync.Scaffold` CLI) with real
NuGet metadata so either could ship as a standalone package usable from any .NET host.

## Alternatives Considered
- **Keep it embedded in the Functions project.** Rejected - ties the entire engine to Azure
  Functions specifically, foreclosing reuse from a console app, a different scheduler, or a
  future non-Azure host, for no benefit (nothing in the engine actually needs the Functions
  runtime).

## Consequences
**Positive:** `Core` compiles and tests with zero Azure-specific dependencies (verified - its
`.csproj` references only `Microsoft.Data.SqlClient` and `Microsoft.Extensions.*.Abstractions`
packages). This is the structural precondition for
[ADR-0013](0013-position-as-general-data-sync-replacement.md)'s broader positioning - a
general-purpose replacement can't be Azure-Functions-only.

**Negative:** `SyncPlan`/`SyncTableConfig` are public positional records in a package meant to
ship externally - adding fields to them (already done twice) is a source-breaking change for
any consumer constructing them positionally. Remains an open, unresolved risk - not yet
mitigated with a stable builder/`with`-based construction path or a documented compatibility
contract.
