# How a sync run works, in detail

The [README](../README.md#how-it-works) shows the short version. This page shows every step of
one run, and where each step lives in the code.

```mermaid
flowchart TD
    Start(["Every run, e.g.<br/>every 5 minutes"]) --> Busy{"Is the previous sync<br/>still running?"}
    Busy -- Yes --> Skip["Skip this time"]
    Busy -- No --> Infra["Auto mode only: create the<br/>tracking tables if missing"]
    Infra --> Each["For each table in the list"]
    Each --> Columns{"New table, or did the<br/>parent's columns change?"}
    Columns -- No --> Plan["Use the saved plan"]
    Columns -- Yes --> CT

    subgraph Once["Only when the table is new or its columns changed"]
        CT{"Change Tracking<br/>enabled on the table?"} -- Yes --> SCT["SqlChangeTracking"]
        CT -- No --> RV{"Has a rowversion<br/>column?"}
        RV -- Yes --> SRV["RowVersion"]
        RV -- No --> TS{"Has a LastUpdate<br/>column?"}
        TS -- Yes --> STS["Timestamp"]
        TS -- No --> Small{"Small table, or listed in<br/>AllowFullRescanFor?"}
        Small -- Yes --> SFR["FullRescan"]
        Small -- No --> Fail["Stop with an error that<br/>names the setting to fix"]
        SCT & SRV & STS & SFR --> HowWrite["Pick how to write rows:<br/>in batches, or row by row"]
        HowWrite --> Mode{"Auto mode?"}
        Mode -- Yes --> Ddl["Create or update the child table;<br/>re-scan once if columns were added"]
        Mode -- No --> Ready["The child table must already match<br/>(from the scaffold tool's script)"]
        Ddl --> Save["Save the new plan"]
        Ready --> Save
    end

    Save --> Plan
    Plan --> Copied{"Copied once<br/>already?"}
    Copied -- No --> First["First copy: all rows in batches,<br/>continues where it stopped"]
    Copied -- Yes --> Changes["Read only the rows<br/>changed since last time"]
    Changes --> Write["Insert or update<br/>them in the child"]
    First --> Child[("Child database")]
    Write --> Child
    Child --> Due{"Delete check due?"}
    Due -- Yes --> Deletes["Remove rows that were<br/>deleted in the parent"]
    Due -- No --> Progress["Save progress,<br/>only if no row failed"]
    Deletes --> Progress
```

## The steps

1. **Previous sync still running.** Only one sync may work on a child database at a time, even
   with several Function instances. A run that finds another one busy does nothing.
2. **New table, or did the columns change.** The parent table's columns are compared with what
   was seen last time. Usually nothing changed, and the saved plan is used as it is.
3. **Pick how to find changes.** Only for a new table or changed columns. The four checks run in
   this order and the first match wins. To choose a strategy yourself, set
   `SmartTableSync:IncrementalStrategyOverrides:<Table>`; it still has to fit the table, or this
   step fails. Each strategy is explained, with a diagram, in
   [how SmartTableSync finds what changed](sync-strategies.md).
4. **Pick how to write rows.** In batches through one stored-procedure call, or row by row for
   tables with very large rows or references to other tables. See
   [merge strategies](../README.md#merge-strategies).
5. **Make the child table match.** In Auto mode SmartTableSync creates the child table, or adds
   new columns, and then reads the whole table once so existing rows get the new values. In
   Manual mode nothing is changed: you create the child objects from the scaffold tool's scripts
   beforehand. See the [setup guide](setup.md#manual-or-auto-mode).
6. **First copy.** A new table is copied in batches, in key order. If a run stops halfway, the
   next run continues from the last batch that finished.
7. **Rows changed since last time.** After the first copy, each run reads only the changed rows,
   using the strategy picked in step 3, and inserts or updates them in the child.
8. **Delete check.** RowVersion, Timestamp and FullRescan can't see deletes, so about once an hour
   the parent's keys are compared with the child's and missing rows are removed. Change Tracking
   reports deletes itself, so for those tables the check runs once a day as a safety net.
9. **Save progress.** Where the run got to is saved only if every row was applied. If a table
   fails, the error is logged and the next run starts that table from the same place again. The
   other tables carry on.

## Where each step lives in the code

| Step | Code |
|---|---|
| Previous sync still running | `ChildDatabaseRunLock` (`sp_getapplock` on the child) plus an in-process lock in `SyncOrchestrator` |
| Tracking tables (Auto mode) | `ChildDdlProvisioner.EnsureInfrastructureExistsAsync` |
| New table or changed columns | `SchemaReader` and `SchemaFingerprint`, used by `SyncTableProvisioner.PlanAsync` |
| Pick how to find changes | `SyncPlanHeuristic.SelectIncrementalStrategy`, in the order of `IncrementalStrategies.All` |
| Pick how to write rows | `SyncPlanHeuristic` |
| Make the child table match | `ChildDdlProvisioner` (Auto) or the scaffold tool (Manual) |
| Save the new plan | `SyncTableProvisioner.SaveAsync`, into `dbo.SyncTableConfig` |
| First copy | `InitialSyncRunner`: `SqlBulkCopy` in key order, resume key in `dbo.SyncBatchLog` |
| Rows changed since last time | the runners under `Strategies/`, all based on `ChangeWindowSyncRunner` |
| Insert or update | `MERGE` through a table-valued parameter, or row by row |
| Delete check | delete reconciliation, `ReconcileInterval` / `ChangeTrackingReconcileInterval` |
| Save progress | `dbo.CustomSyncState` |
