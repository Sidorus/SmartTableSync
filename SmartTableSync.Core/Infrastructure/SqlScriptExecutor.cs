using System.Text.RegularExpressions;

namespace SmartTableSync.Infrastructure;

/// <summary>Executes a multi-batch SQL script (splitting on GO separators, same convention
/// every script in this repo already uses) against a live connection. The production
/// counterpart to the test-only script runner in MsSqlTestFixture - used by
/// ChildDdlProvisioner to execute what SmartTableSync.Scaffold otherwise only writes to a
/// file for a human to apply by hand.</summary>
internal static class SqlScriptExecutor
{
    private static readonly Regex _goSeparator = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task ExecuteAsync(SqlConnection connection, string script, CancellationToken ct, int commandTimeout = 60)
    {
        foreach (var batch in _goSeparator.Split(script))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            await using var command = new SqlCommand(trimmed, connection) { CommandTimeout = commandTimeout };
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
