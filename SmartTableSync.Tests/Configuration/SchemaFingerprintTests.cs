using SmartTableSync.Planning;
using SmartTableSync.Introspection;
using Xunit;

namespace SmartTableSync.Tests.Configuration;

public class SchemaFingerprintTests
{
    private static TableProfile CreateProfile(
        IReadOnlyList<ColumnProfile>? columns = null,
        IReadOnlyList<string>? primaryKeyColumns = null,
        string? identityColumn = null) =>
        new(
            TableName: "TestTable",
            Columns: columns ?? [new ColumnProfile("Id", "int", 4, false), new ColumnProfile("Name", "nvarchar", 100, true)],
            PrimaryKeyColumns: primaryKeyColumns ?? ["Id"],
            IdentityColumn: identityColumn,
            IndexedLobColumns: [],
            ReferencesOtherTables: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>());

    [Fact]
    public void Compute_SameProfileTwice_ProducesSameHash()
    {
        var profile = CreateProfile();

        Assert.Equal(SchemaFingerprint.Compute(profile), SchemaFingerprint.Compute(profile));
    }

    [Fact]
    public void Compute_FullProfileOnlyFields_DoNotChangeHash()
    {
        // SyncTableProvisioner fingerprints a first-seen table from the full profile but later
        // checks drift with the cheap one, so the sampling-only fields must not affect the hash.
        var cheap = CreateProfile(columns: [
            new ColumnProfile("Id", "int", 4, false),
            new ColumnProfile("Body", "nvarchar", -1, true),
        ]);
        var full = cheap with
        {
            IndexedLobColumns = ["Body"],
            ReferencesOtherTables = true,
            RowCount = 1_000_000,
            AvgRowSizeBytes = 4_096,
            MaxLobColumnSizeBytes = new Dictionary<string, long> { ["Body"] = 50_000_000 },
        };

        Assert.Equal(SchemaFingerprint.Compute(cheap), SchemaFingerprint.Compute(full));
    }

    [Fact]
    public void Compute_EnablingChangeTracking_ProducesDifferentHash()
    {
        // A DBA enabling (or disabling) CT must trigger re-provisioning, so the table's
        // incremental strategy gets re-derived.
        var original = CreateProfile();
        var withChangeTracking = original with { IsChangeTrackingEnabled = true };

        Assert.NotEqual(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(withChangeTracking));
    }

    [Fact]
    public void Compute_ChangeTrackingDisabled_MatchesHashFromBeforeTheFieldExisted()
    {
        // Existing tables without CT must keep their persisted fingerprint across the upgrade
        // that added IsChangeTrackingEnabled - otherwise every table re-provisions once for
        // no reason. This is the SHA-256 of the exact pre-CT input string for CreateProfile().
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "Table:TestTable|Id:int:4:False;Name:nvarchar:100:True;|PK:Id|Identity:(none)")));

        Assert.Equal(expected, SchemaFingerprint.Compute(CreateProfile()));
    }

    [Fact]
    public void Compute_AddingAColumn_ProducesDifferentHash()
    {
        var original = CreateProfile();
        var withNewColumn = original with
        {
            Columns = [.. original.Columns, new ColumnProfile("Extra", "int", 4, true)],
        };

        Assert.NotEqual(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(withNewColumn));
    }

    [Fact]
    public void Compute_RemovingAColumn_ProducesDifferentHash()
    {
        var original = CreateProfile();
        var withColumnRemoved = original with { Columns = [original.Columns[0]] };

        Assert.NotEqual(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(withColumnRemoved));
    }

    [Fact]
    public void Compute_ChangingAColumnType_ProducesDifferentHash()
    {
        var original = CreateProfile();
        var withTypeChanged = original with
        {
            Columns = [original.Columns[0], new ColumnProfile("Name", "varchar", 100, true)],
        };

        Assert.NotEqual(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(withTypeChanged));
    }

    [Fact]
    public void Compute_ReorderingCompositePrimaryKeyColumns_ProducesDifferentHash()
    {
        // Composite key column ORDER matters (changes the keyset predicate / resume-key
        // format), so this must be detected — unlike a pure non-key column reorder.
        var original = CreateProfile(primaryKeyColumns: ["A", "B"]);
        var reordered = original with { PrimaryKeyColumns = ["B", "A"] };

        Assert.NotEqual(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(reordered));
    }

    [Fact]
    public void Compute_ReorderingNonKeyColumnsOnly_ProducesSameHash()
    {
        // A pure physical column reorder with no name/type/nullability change shouldn't
        // invalidate the fingerprint — application code always maps by column name.
        var original = CreateProfile(columns: [
            new ColumnProfile("Id", "int", 4, false),
            new ColumnProfile("Name", "nvarchar", 100, true),
            new ColumnProfile("Description", "nvarchar", -1, true),
        ]);
        var reordered = original with
        {
            Columns = [original.Columns[2], original.Columns[0], original.Columns[1]],
        };

        Assert.Equal(SchemaFingerprint.Compute(original), SchemaFingerprint.Compute(reordered));
    }

    [Fact]
    public void Compute_AddingIdentityColumn_ProducesDifferentHash()
    {
        var withoutIdentity = CreateProfile(identityColumn: null);
        var withIdentity = CreateProfile(identityColumn: "Id");

        Assert.NotEqual(SchemaFingerprint.Compute(withoutIdentity), SchemaFingerprint.Compute(withIdentity));
    }

    [Theory]
    [InlineData("UniqueIndex")]
    [InlineData("Configured")]
    public void Compute_KeyNotFromPrimaryKey_ProducesDifferentHash(string keySourceName)
    {
        // The same columns becoming a real PRIMARY KEY must re-provision the table: Change
        // Tracking becomes possible, and a configured key no longer applies.
        var primaryKey = CreateProfile();
        var otherSource = primaryKey with { KeySource = Enum.Parse<SyncKeySource>(keySourceName) };

        Assert.NotEqual(SchemaFingerprint.Compute(primaryKey), SchemaFingerprint.Compute(otherSource));
    }

    [Fact]
    public void Compute_UniqueIndexAndConfiguredKey_ProduceDifferentHashes()
    {
        var uniqueIndex = CreateProfile() with { KeySource = SyncKeySource.UniqueIndex };
        var configured = CreateProfile() with { KeySource = SyncKeySource.Configured };

        Assert.NotEqual(SchemaFingerprint.Compute(uniqueIndex), SchemaFingerprint.Compute(configured));
    }

    [Fact]
    public void Compute_DecimalScaleChange_ChangesHash()
    {
        var before = CreateProfile(columns: [new ColumnProfile("Id", "int", 4, false), new ColumnProfile("Price", "decimal", 9, false, 10, 2)]);
        var after = CreateProfile(columns: [new ColumnProfile("Id", "int", 4, false), new ColumnProfile("Price", "decimal", 9, false, 10, 4)]);

        Assert.NotEqual(SchemaFingerprint.Compute(before), SchemaFingerprint.Compute(after));
    }

    [Fact]
    public void Compute_PrecisionOfOtherTypes_DoesNotChangeHash()
    {
        // Only decimal and numeric take part, so a table without them keeps the fingerprint it
        // had before precision and scale were read, and isn't planned again on upgrade.
        var withoutPrecision = CreateProfile(columns: [new ColumnProfile("Id", "int", 4, false), new ColumnProfile("At", "datetime2", 8, false)]);
        var withPrecision = CreateProfile(columns: [new ColumnProfile("Id", "int", 4, false, 10, 0), new ColumnProfile("At", "datetime2", 8, false, 27, 7)]);

        Assert.Equal(SchemaFingerprint.Compute(withoutPrecision), SchemaFingerprint.Compute(withPrecision));
    }
}
