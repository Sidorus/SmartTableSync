# Configuration reference

Every setting SmartTableSync reads, with its default, what it accepts and an example. The
[README](../README.md#configuration) has the short table; this page is the full description.

## Where settings go

The engine reads ordinary .NET configuration, so the same names work in any host. Only the way
you write the name changes:

| Where | How a name is written | Example |
|---|---|---|
| `appsettings.json` (your own host) | nested JSON sections | `"SmartTableSync": { "TableList": "Product,Customer" }` |
| `local.settings.json` (the example Function, locally) | flat, with `:` | `"SmartTableSync:TableList": "Product,Customer"` |
| Azure Function App / App Service settings on **Linux** | flat, with `__` instead of `:` | `SmartTableSync__TableList = Product,Customer` |
| Azure settings on **Windows** | flat, with `:` (or `__`) | `SmartTableSync:TableList = Product,Customer` |
| Environment variables | flat, with `__` | `SmartTableSync__TableList=Product,Customer` |

Per-table settings end with the table's name: `SmartTableSync:TimestampColumns:Customer`, or
`SmartTableSync:TimestampColumns:sales.Order` for a table outside `dbo`. Table names aren't
case-sensitive, and `dbo.Customer` means the same table as `Customer`.

The scaffold tool reads the same `SmartTableSync:*` settings, from environment variables, so the
scripts it writes match what the engine will expect.

## Connection strings

| Name | Required | Used for |
|---|---|---|
| `ParentDbConnectionString` | yes | reading the parent, as the runtime identity |
| `ChildDbConnectionString` | yes | writing the child and the tracking tables, as the runtime identity |
| `ChildProvisioningConnectionString` | only in Auto mode | creating and changing the child's tables, table types and procedures, as the provisioning identity |

They're read as top-level settings (not from a `ConnectionStrings` section). A missing one makes
the sync fail with a message naming it, for example *"ParentDbConnectionString is not
configured."*

Examples:

```text
# Azure SQL, user-assigned managed identity (no password)
Server=tcp:myserver.database.windows.net,1433;Database=parentdb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True

# Azure SQL, system-assigned managed identity: leave out User Id
Server=tcp:myserver.database.windows.net,1433;Database=parentdb;Authentication=Active Directory Managed Identity;Encrypt=True

# Your own sign-in while developing (Azure CLI, Visual Studio)
Server=tcp:myserver.database.windows.net,1433;Database=parentdb;Authentication=Active Directory Default;Encrypt=True

# SQL Server on-premises, Windows authentication
Server=sql01.corp.local;Database=ParentDb;Integrated Security=True;Encrypt=True

# SQL Server, SQL login
Server=sql01.corp.local;Database=ParentDb;User Id=smarttablesync-runtime;Password=<secret>;Encrypt=True
```

Any `Authentication=Active Directory ...` method needs the
`Microsoft.Data.SqlClient.Extensions.Azure` package in the host; the example Function has it.
Keep passwords out of source control: use a managed identity, or a secret store such as Key
Vault references.

## Engine settings

### `SmartTableSync:TableList`

**Required.** The tables to sync, comma-separated: `Customer` for a table in `dbo`,
`sales.Order` for any other schema.

```json
"SmartTableSync:TableList": "Product,Customer,sales.Order"
```

- Spaces around names are ignored; `dbo.Customer` is stored as `Customer`.
- The host doesn't start when the list is empty, lists a table twice, or has a name with more
  than one dot or longer than 100 characters, with a message saying which.
- Tables are synced in this order on every run. A table that fails doesn't stop the others.

### `SmartTableSync:Mode`

**Default `Manual`.** Who creates the child's tables, table types and procedures.

```json
"SmartTableSync:Mode": "Auto"
```

| Value | Meaning |
|---|---|
| `Manual` | You create them from the scaffold tool's scripts. The engine only reads and writes data. |
| `Auto` | The engine creates them itself the first time it sees a table, and adds columns that appear in the parent later. Needs `ChildProvisioningConnectionString`. |

The value isn't case-sensitive. **An unknown value quietly means `Manual`**, so check the
spelling if Auto mode doesn't seem to do anything. See [Manual or Auto mode](../README.md#manual-or-auto-mode).

### `SmartTableSync:TimestampColumns:<Table>`

**Default `LastUpdate`.** The column the Timestamp strategy reads to find changed rows, for tables
that use a different name.

```json
"SmartTableSync:TimestampColumns:Customer": "ModifiedAt",
"SmartTableSync:TimestampColumns:sales.Order": "ChangedOnUtc"
```

- The column must be `datetime`, `datetime2`, `smalldatetime` or `date`, hold **UTC** time, and be
  updated by the application on every change. See [Timestamp](sync-strategies.md#timestamp).
- A configured column of another type stops the table at setup with a clear message. A
  configured column that doesn't exist means the Timestamp strategy isn't available for the
  table, and the next strategy is tried.
- A table without this setting uses `LastUpdate`, if it has such a column.
- The old name `SmartTableSync:ChangeTrackingColumns:<Table>` is still read; the new name wins
  when both are set.
- Only read when the table is set up or its schema changes; see
  [changing a table's strategy later](how-it-works.md#changing-a-tables-strategy-later).

### `SmartTableSync:AllowFullRescanFor`

**Default: none.** Tables over 50,000 rows that may use the FullRescan strategy anyway, which
reads the whole table on every run.

```json
"SmartTableSync:AllowFullRescanFor": "PriceList,sales.Region"
```

- Only matters for a table with no other way to find changes (no Change Tracking, no
  `rowversion` column, no timestamp column). Without it, such a large table stops at setup with a
  message naming this setting, so a big table is never re-read every run by accident.
- Tables of 50,000 rows or fewer don't need to be listed.
- Consider the cost first: every run reads and merges every row. A `rowversion` column (scaffold
  tool `--rowversion-setup`) is usually the better fix. See [FullRescan](sync-strategies.md#fullrescan).

### `SmartTableSync:IncrementalStrategyOverrides:<Table>`

**Default: none (picked automatically).** Forces how a table's changes are found.

```json
"SmartTableSync:IncrementalStrategyOverrides:Customer": "Timestamp",
"SmartTableSync:IncrementalStrategyOverrides:sales.Order": "FullRescan"
```

| Value | Needs |
|---|---|
| `SqlChangeTracking` | Change Tracking enabled on the table, and a primary key |
| `RowVersion` | a `rowversion` column |
| `Timestamp` | the timestamp column (see `TimestampColumns`) |
| `FullRescan` | nothing; forcing it counts as consent for a large table too |

- Without an override the first that fits wins, in the order of the table above.
- An override the table can't satisfy stops the table at setup with a clear message.
- **An unknown value is quietly ignored** and the strategy is picked automatically, so check the
  spelling (case doesn't matter).
- Only read when the table is set up or its schema changes; see
  [changing a table's strategy later](how-it-works.md#changing-a-tables-strategy-later).

### `SmartTableSync:KeyColumns:<Table>`

**Default: none.** The key for a table **without a primary key**: comma-separated column names,
in key order.

```json
"SmartTableSync:KeyColumns:Ledger": "Region,Code",
"SmartTableSync:KeyColumns:sales.Import": "BatchId,LineNo"
```

- A table without a primary key and without this setting uses its unique index whose key columns
  are all `NOT NULL` (not filtered or disabled). This setting is for tables that don't have one.
- The columns must exist and be `NOT NULL`, and must be unique together: the parent's data is
  checked for duplicates when the table is set up, and duplicates stop it with a message.
- Setting it for a table that **has** a primary key is a mistake and stops the table: the primary
  key is always the key.
- The child table gets these columns as its primary key. Change Tracking still needs a real
  primary key, so such a table uses RowVersion, Timestamp or FullRescan.
- See [setup: tables without a primary key](setup.md#tables-without-a-primary-key).

### `SmartTableSync:ReconcileIntervalHours`

**Default `1`.** How often, in whole hours, tables on RowVersion, Timestamp and FullRescan compare
the parent's and the child's keys to remove rows deleted in the parent.

```json
"SmartTableSync:ReconcileIntervalHours": "6"
```

- Those strategies can't see deletes, so this is the only way deletes reach the child: a higher
  value means deleted rows stay longer in the child, a lower one means more reads of all keys.
- It runs at most once per interval **per host process**, and also on the first run after the host
  starts. See [delete reconciliation](sync-strategies.md#delete-reconciliation).
- `0`, a negative number or anything that isn't a whole number keeps the default of 1 hour.

### `SmartTableSync:ChangeTrackingReconcileIntervalHours`

**Default `24`.** The same key comparison for Change Tracking tables. They already get deletes on
every run, so this is only a safety net, for example for a row changed by hand in the child.

```json
"SmartTableSync:ChangeTrackingReconcileIntervalHours": "0"
```

- `0` turns the periodic comparison off; it then runs only after a fallback full re-scan.
- A negative number or anything that isn't a whole number keeps the default of 24 hours.

### `SmartTableSync:ConnectionOpenAttempts`

**Default `5`.** How many times opening a database connection is tried when it fails with a
temporary error (Azure SQL failover, throttling, a serverless database resuming), with a growing
pause between attempts.

```json
"SmartTableSync:ConnectionOpenAttempts": "8"
```

- `1` turns retries off. Values outside 1-60 keep the default.
- Only opening a connection is retried. A command that fails makes the table fail for this run,
  and the next run tries it again from where it stopped.

## A complete example

A Function App on Linux, Manual mode, with one table outside `dbo` and one without a primary key:

```text
ParentDbConnectionString                                    = Server=tcp:myserver.database.windows.net,1433;Database=parentdb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True
ChildDbConnectionString                                     = Server=tcp:myserver.database.windows.net,1433;Database=childdb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True
SmartTableSync__TableList                                   = Product,Customer,sales.Order,Ledger
SmartTableSync__Mode                                        = Manual
SmartTableSync__TimestampColumns__Customer                  = ModifiedAt
SmartTableSync__IncrementalStrategyOverrides__sales.Order   = RowVersion
SmartTableSync__KeyColumns__Ledger                          = Region,Code
SmartTableSync__ReconcileIntervalHours                      = 2
```

The same as `appsettings.json` in your own host:

```json
{
  "ParentDbConnectionString": "Server=tcp:myserver.database.windows.net,1433;Database=parentdb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True",
  "ChildDbConnectionString": "Server=tcp:myserver.database.windows.net,1433;Database=childdb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True",
  "SmartTableSync": {
    "TableList": "Product,Customer,sales.Order,Ledger",
    "Mode": "Manual",
    "TimestampColumns": { "Customer": "ModifiedAt" },
    "IncrementalStrategyOverrides": { "sales.Order": "RowVersion" },
    "KeyColumns": { "Ledger": "Region,Code" },
    "ReconcileIntervalHours": "2"
  }
}
```

## When settings take effect

| Setting | Takes effect |
|---|---|
| `TableList`, `Mode`, the intervals, `ConnectionOpenAttempts`, connection strings | on the next start of the host |
| `TimestampColumns`, `AllowFullRescanFor`, `IncrementalStrategyOverrides`, `KeyColumns` | when the table is set up, or after its schema changes. For a table already set up, [reset its plan](how-it-works.md#changing-a-tables-strategy-later). |

Changing an Azure Function App's settings restarts it, so the first group applies within a minute.
