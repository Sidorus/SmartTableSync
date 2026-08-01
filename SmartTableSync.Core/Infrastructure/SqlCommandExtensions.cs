namespace SmartTableSync.Infrastructure;

public static class SqlCommandExtensions
{
    /// <summary>
    /// Adds a parameter, converting null / DBNull to DBNull.Value.
    /// Pass size = -1 for MAX types (nvarchar(max), varbinary(max)).
    /// </summary>
    public static void AddParam(
        this SqlCommand cmd,
        string name,
        SqlDbType type,
        object? value,
        int size = 0)
    {
        var parameter = size != 0
            ? cmd.Parameters.Add(name, type, size)
            : cmd.Parameters.Add(name, type);

        parameter.Value = value is null or DBNull ? DBNull.Value : value;
    }
}
