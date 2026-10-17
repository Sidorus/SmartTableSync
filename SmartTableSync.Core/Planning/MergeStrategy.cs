namespace SmartTableSync.Planning;

/// <summary>How a batch of rows is merged into the child table.</summary>
internal enum MergeStrategy
{
    /// <summary>One stored-procedure call per batch, through a table-valued parameter. Chosen
    /// when the table references no other table and its rows are small.</summary>
    Tvp,

    /// <summary>One stored-procedure call per row, so one failing row doesn't fail the batch.
    /// Chosen when the table references another table (a row can arrive before the row it
    /// references and fail the FK check until it does) or when rows are large.</summary>
    RowByRow,
}
