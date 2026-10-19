# How SmartTableSync finds what changed

Every run (as often as you schedule it; every 5 minutes in the example Function), SmartTableSync asks the parent for the rows that changed
since the last successful run and writes them into the child. How it finds those rows depends on
the table. There are four ways, called **incremental strategies**. Each table gets the first one
that works for it, in this order, unless you force one with
`SmartTableSync:IncrementalStrategyOverrides:<Table>`:

| Strategy | What the parent table needs | Finds inserts and updates by | Finds deletes |
|---|---|---|---|
| [SqlChangeTracking](#sqlchangetracking) | Change Tracking enabled by a DBA | SQL Server's change log | Every run |
| [RowVersion](#rowversion) | A `rowversion` column | The row's version number | Hourly reconciliation |
| [Timestamp](#timestamp) | A datetime column the application updates (`LastUpdate`) | The time of the last change | Hourly reconciliation |
| [FullRescan](#fullrescan) | Nothing, but only for small tables | Reading the whole table | Hourly reconciliation |

Two rules hold for all four:

- **Reading a row twice is harmless.** Every write into the child is a `MERGE`, so a row that is
  read again ends up exactly the same. The strategies use this on purpose to never miss a row.
- **The position moves only after success.** Each run saves where it got to in the child's
  `CustomSyncState` table, and only when every row was applied. If a run fails, the next run
  starts from the same place again.

The diagrams below all use the same example: a `dbo.Customer` table where customer 2 was
updated, customer 4 was inserted and customer 3 was deleted since the last run.

## SqlChangeTracking

The best choice when a DBA can enable it. SQL Server itself records which keys changed, so
nothing is added to your table and deletes are found on every run.

![SqlChangeTracking: the change table and commit table joined by CHANGETABLE](diagrams/sync-change-tracking.svg)

1. **During the statement** (①), SQL Server writes one row per changed key into an internal
   change table: the key, the operation (insert, update or delete) and the transaction id.
2. **At commit** (②), the commit table gets the transaction id and its **version number**.
   Versions are handed out in commit order, so they only grow. Transaction 54 has not committed
   yet, so it has no version.
3. **SmartTableSync reads** with `CHANGETABLE(CHANGES dbo.Customer, 100)`, where 100 is the version
   saved after the last run. It returns one row per changed key with the latest version and the
   operation. Transaction 54 is skipped until it commits.
4. It joins those keys to `dbo.Customer` to get the current values. Key 3 has no row there, so it
   is a delete.
5. It applies the changes to the child and saves the database's current version (`CT:103`),
   which it captured **before** reading.

Good to know:

- Turn it on with `smarttablesync-scaffold --change-tracking-setup <Table>` (a script for a DBA to
  review). See [setup step 2](setup.md#2-optional-prepare-the-parent-tables).
- SQL Server keeps the change log for a limited time (the retention period, 2 days by default).
  If the child has not synced within that time, SmartTableSync notices and runs one full re-scan
  of the table instead.
- The delete reconciliation still runs once a day as a safety net.

## RowVersion

For tables where Change Tracking isn't allowed but a column can be added.

![RowVersion: rows with RowVer at or above the saved position are read](diagrams/sync-rowversion.svg)

1. The table has a `rowversion` column. **SQL Server sets it by itself** on every insert and
   update, to a number that is higher than any before it in that database. The application
   doesn't do anything and can't forget it.
2. A run starts **from** the position saved last time (`0x…07D4`).
3. It captures **to** with `MIN_ACTIVE_ROWVERSION()` before reading. If a transaction is still
   open, this stays below that transaction's rows, so they are read next run instead of being
   skipped.
4. It reads the rows with `RowVer >= 0x…07D4`, page by page in key order (keys 2 and 4), and
   merges them into the child.
5. If everything succeeded, it saves `0x…07DB` as the new position.

Good to know:

- Add the column and its index with `smarttablesync-scaffold --rowversion-setup <Table>`.
- **Deletes are not seen**: a deleted row leaves nothing to read. The delete reconciliation
  compares the parent's and child's keys every hour and removes key 3.
- An update that writes the same values (`SET Name = Name`) still gets a new number, so the row
  is synced again. That only costs a little extra work.

## Timestamp

For tables that already have a "last changed" datetime column, `LastUpdate` by default (another
name with `SmartTableSync:TimestampColumns:<Table>`).

![Timestamp: rows with LastUpdate after the last sync minus one minute are read](diagrams/sync-timestamp.svg)

1. **The application sets `LastUpdate`** whenever it changes a row. SQL Server doesn't do this
   for you.
2. A run takes the last sync time saved in the child (10:00) and goes back one minute (09:59).
   The overlap covers small clock differences and transactions that committed a moment after
   they set the column.
3. It reads the rows with `LastUpdate >= 09:59`, page by page in key order (keys 2, 4 and 6), and
   merges them into the child. Key 6 was already synced last time; reading it again is harmless.
4. If everything succeeded, it saves the time the run started, taken from the **parent's** clock
   (10:05), so the time compares correctly with `LastUpdate`.

Good to know:

- **A change that doesn't update `LastUpdate` is never seen.** For example a script or another
  application that forgets the column. If you can't rely on that, use RowVersion or Change
  Tracking instead.
- Deletes come from the hourly delete reconciliation, as with RowVersion.

## FullRescan

The fallback for tables with no way to tell what changed.

![FullRescan: the whole table is read and merged every run](diagrams/sync-full-rescan.svg)

1. Every run reads **the whole table**, page by page in key order, and merges every row into the
   child.
2. There is no position to save: the next run reads everything again.
3. Deletes come from the hourly delete reconciliation.

Good to know:

- It is only allowed for tables with **50,000 rows or fewer**, or tables listed in
  `SmartTableSync:AllowFullRescanFor`. A larger table with no other strategy fails at setup with
  a message naming the setting, so it never re-reads millions of rows every 5 minutes by accident.
- It is also used once, automatically, when a table's history can't be trusted: Change Tracking
  data that has expired, or a new column added to the parent in Auto mode.

## Delete reconciliation

RowVersion, Timestamp and FullRescan can't see deletes. Once an hour (`ReconcileInterval`) each of
those tables compares the list of keys in the parent with the list in the child and deletes child
rows whose key no longer exists in the parent. Tables on SqlChangeTracking already get deletes on
every run and do this only once a day (`ChangeTrackingReconcileInterval`) as a safety net.
