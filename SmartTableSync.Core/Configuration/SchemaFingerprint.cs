using System.Security.Cryptography;
using System.Text;

namespace SmartTableSync.Configuration;

/// <summary>
/// Pure hash of the parts of a TableProfile that determine sync behavior: column
/// name/type/length/nullability, primary key columns (in order — order matters for
/// composite keys), identity column, and whether SQL Server Change Tracking is enabled on
/// the table (it selects the incremental strategy). Deliberately excludes row count/size/FK data,
/// which change constantly without requiring re-provisioning.
///
/// Non-key columns are sorted by name before hashing, so a pure physical column reorder
/// (no name/type change) doesn't spuriously invalidate the fingerprint — application code
/// always maps by column name (SELECT *), never position. Primary key column ORDER is
/// preserved exactly as given, since reordering a composite key changes the keyset
/// predicate and resume-key format and must be detected.
/// </summary>
internal static class SchemaFingerprint
{
    public static string Compute(TableProfile profile)
    {
        var sb = new StringBuilder();

        sb.Append("Table:").Append(profile.TableName).Append('|');

        foreach (var col in profile.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            sb.Append(col.Name).Append(':')
              .Append(col.SqlTypeName).Append(':')
              .Append(col.MaxLength).Append(':')
              .Append(col.IsNullable).Append(';');
        }

        sb.Append("|PK:").Append(string.Join(",", profile.PrimaryKeyColumns));
        sb.Append("|Identity:").Append(profile.IdentityColumn ?? "(none)");

        // Appended only when enabled, so every existing table without CT keeps exactly the
        // fingerprint it had before this field existed (no spurious re-provisioning on
        // upgrade), while a DBA enabling or disabling CT does trigger one.
        if (profile.IsChangeTrackingEnabled)
            sb.Append("|ChangeTracking:on");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
