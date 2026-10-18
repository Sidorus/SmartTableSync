using System.Text;
using System.Text.RegularExpressions;

namespace SmartTableSync.IntegrationTests.Support;

/// <summary>
/// sql/07_permissions.sql, the documented least-privilege grants, split by identity section
/// ("1. Child-provisioning", "2. Parent-introspection", "3. Runtime") and by the database each
/// part runs on (its "run on the PARENT / CHILD database" marker). Tests run these parts as
/// written, so a grant missing from the documentation fails a test instead of a deployment.
/// </summary>
internal sealed class PermissionsScript
{
    public const string ChildProvisioning = "child-provisioning";
    public const string ParentIntrospection = "parent-introspection";
    public const string Runtime = "runtime";

    public const string ParentDatabase = "parent";
    public const string ChildDatabase = "child";

    private static readonly Regex _sectionHeader =
        new(@"^--\s*\d+\.\s*(Child-provisioning|Parent-introspection|Runtime)\b", RegexOptions.IgnoreCase);

    private static readonly Regex _databaseMarker = new(@"run on the (PARENT|CHILD) database", RegexOptions.IgnoreCase);

    // The Auto mode alternative is documented as a commented-out statement, since it replaces
    // the per-object CHILD grants rather than adding to them.
    private static readonly Regex _autoModeSchemaGrant = new(@"^--\s*(GRANT\s+.+\s+ON\s+SCHEMA::.+;)\s*$", RegexOptions.IgnoreCase);

    private readonly Dictionary<(string Identity, string Database), StringBuilder> _sections = [];
    private readonly StringBuilder _autoModeChildRuntimeGrants = new();

    private PermissionsScript()
    {
    }

    /// <summary>The schema-level runtime grants documented for Auto mode, uncommented.</summary>
    public string AutoModeChildRuntimeGrants => _autoModeChildRuntimeGrants.ToString();

    public static PermissionsScript Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sql", "07_permissions.sql");
        var script = new PermissionsScript();
        string? identity = null;
        string? database = null;

        foreach (var line in File.ReadLines(path))
        {
            var header = _sectionHeader.Match(line);
            if (header.Success)
            {
                identity = header.Groups[1].Value.ToLowerInvariant();
                database = null;
            }

            var marker = _databaseMarker.Match(line);
            if (identity is not null && marker.Success)
            {
                database = marker.Groups[1].Value.Equals("PARENT", StringComparison.OrdinalIgnoreCase) ? ParentDatabase : ChildDatabase;
            }

            if (identity is null)
            {
                continue;
            }

            var autoModeGrant = _autoModeSchemaGrant.Match(line);
            if (autoModeGrant.Success)
            {
                script._autoModeChildRuntimeGrants.AppendLine(autoModeGrant.Groups[1].Value);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (database is null)
            {
                throw new InvalidOperationException(
                    $"07_permissions.sql: statement in the {identity} section before any \"run on the PARENT/CHILD database\" marker: {line}");
            }

            if (!script._sections.TryGetValue((identity, database), out var section))
            {
                section = new StringBuilder();
                script._sections[(identity, database)] = section;
            }

            section.AppendLine(line);
        }

        return script;
    }

    /// <summary>The statements the given identity's section runs on the given database.</summary>
    public string For(string identity, string database) =>
        _sections.TryGetValue((identity, database), out var section)
            ? section.ToString()
            : throw new InvalidOperationException($"07_permissions.sql has no {identity} statements for the {database} database.");
}
