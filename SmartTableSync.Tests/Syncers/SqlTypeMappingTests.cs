using SmartTableSync.Introspection;
using SmartTableSync.Syncers.Sql;
using Xunit;

namespace SmartTableSync.Tests.Syncers;

public class SqlTypeMappingTests
{
    [Theory]
    [InlineData("int", System.Data.SqlDbType.Int)]
    [InlineData("nvarchar", System.Data.SqlDbType.NVarChar)]
    [InlineData("varchar", System.Data.SqlDbType.VarChar)]
    [InlineData("datetime", System.Data.SqlDbType.DateTime)]
    [InlineData("bit", System.Data.SqlDbType.Bit)]
    [InlineData("real", System.Data.SqlDbType.Real)]
    [InlineData("varbinary", System.Data.SqlDbType.VarBinary)]
    [InlineData("smalldatetime", System.Data.SqlDbType.SmallDateTime)]
    [InlineData("datetimeoffset", System.Data.SqlDbType.DateTimeOffset)]
    [InlineData("time", System.Data.SqlDbType.Time)]
    [InlineData("text", System.Data.SqlDbType.Text)]
    [InlineData("ntext", System.Data.SqlDbType.NText)]
    [InlineData("timestamp", System.Data.SqlDbType.Timestamp)]
    [InlineData("rowversion", System.Data.SqlDbType.Timestamp)]
    public void ToSqlDbType_MapsKnownTypes(string sqlTypeName, System.Data.SqlDbType expected)
    {
        Assert.Equal(expected, SqlTypeMapping.ToSqlDbType(sqlTypeName));
    }

    [Fact]
    public void ToSqlDbType_UnknownType_Throws()
    {
        Assert.Throws<NotSupportedException>(() => SqlTypeMapping.ToSqlDbType("hierarchyid"));
    }

    [Theory]
    [InlineData("nvarchar", true)]
    [InlineData("varchar", true)]
    [InlineData("text", true)]
    [InlineData("ntext", true)]
    [InlineData("int", false)]
    [InlineData("datetime", false)]
    public void IsStringType_ClassifiesCorrectly(string sqlTypeName, bool expected)
    {
        Assert.Equal(expected, SqlTypeMapping.IsStringType(sqlTypeName));
    }

    [Fact]
    public void ToSqlDbType_TextAndNText_NoLongerThrowsDespiteBeingClassifiedAsStringTypes()
    {
        // Regression test for the exact inconsistency this item fixes: IsStringType already
        // listed text/ntext, but ToSqlDbType had no case for either - any RowByRow-strategy
        // table with such a column threw NotSupportedException the moment it was touched.
        SqlTypeMapping.ToSqlDbType("text");
        SqlTypeMapping.ToSqlDbType("ntext");
    }

    [Fact]
    public void DefaultKeyComponent_NumericColumn_IsZero()
    {
        // Matches the original hand-written syncers' "no resume key yet" sentinel for
        // IDENTITY/int keys (resumeKey != null ? int.Parse(resumeKey) : 0).
        Assert.Equal("0", SqlTypeMapping.DefaultKeyComponent("int"));
    }

    [Fact]
    public void DefaultKeyComponent_StringColumn_IsEmptyString()
    {
        // Matches ProductCatalogItemSyncer's original: resumeKey ?? string.Empty.
        Assert.Equal(string.Empty, SqlTypeMapping.DefaultKeyComponent("nvarchar"));
    }

    [Theory]
    [InlineData("45231", "int", 45231)]
    [InlineData("en", "nvarchar", "en")]
    public void ParseKeyComponent_ParsesToCorrectClrType(string value, string sqlTypeName, object expected)
    {
        Assert.Equal(expected, SqlTypeMapping.ParseKeyComponent(value, sqlTypeName));
    }

    // DateTime/DateTimeOffset/TimeSpan aren't valid attribute-argument types, so these three
    // (newly key-eligible per this item - smalldatetime/datetimeoffset/time can legitimately
    // be PK/keyset columns, unlike text/ntext, which SQL Server disallows in PRIMARY KEY
    // constraints) get their own round-trip Facts instead of joining the Theory above.

    [Fact]
    public void ParseKeyComponent_SmallDateTime_RoundTripsViaInvariantCulture()
    {
        var expected = new DateTime(2026, 1, 15, 10, 30, 0);
        var formatted = expected.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, SqlTypeMapping.ParseKeyComponent(formatted, "smalldatetime"));
    }

    [Fact]
    public void ParseKeyComponent_DateTimeOffset_RoundTripsViaInvariantCulture()
    {
        var expected = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));
        var formatted = expected.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, SqlTypeMapping.ParseKeyComponent(formatted, "datetimeoffset"));
    }

    [Fact]
    public void ParseKeyComponent_Time_RoundTripsViaInvariantCulture()
    {
        var expected = new TimeSpan(10, 30, 15);
        var formatted = expected.ToString("c", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, SqlTypeMapping.ParseKeyComponent(formatted, "time"));
    }

    [Fact]
    public void GetParameterSize_MaxType_ReturnsNegativeOne()
    {
        var column = new ColumnProfile("Description", "nvarchar", -1, false);
        Assert.Equal(-1, SqlTypeMapping.GetParameterSize(column));
    }

    [Fact]
    public void GetParameterSize_NVarChar_HalvesByteLengthToCharacterLength()
    {
        // sys.columns.max_length for nvarchar(200) is 400 (bytes, 2 bytes/char) - the
        // SqlParameter needs 200 (characters), matching the original hand-written
        // "sp.AddParam(\"@Slug\", SqlDbType.NVarChar, row[\"Slug\"], 200)" for NVARCHAR(200).
        var column = new ColumnProfile("Slug", "nvarchar", 400, true);
        Assert.Equal(200, SqlTypeMapping.GetParameterSize(column));
    }

    [Fact]
    public void GetParameterSize_VarChar_UsesByteLengthDirectly()
    {
        var column = new ColumnProfile("Title", "varchar", 250, true);
        Assert.Equal(250, SqlTypeMapping.GetParameterSize(column));
    }

    [Fact]
    public void GetParameterSize_NonVariableLengthType_ReturnsZero()
    {
        var column = new ColumnProfile("AttributeValueId", "int", 4, false);
        Assert.Equal(0, SqlTypeMapping.GetParameterSize(column));
    }

    [Theory]
    [InlineData("smalldatetime")]
    [InlineData("datetimeoffset")]
    [InlineData("time")]
    [InlineData("text")]
    [InlineData("ntext")]
    public void GetParameterSize_NewlySupportedTypes_ReturnZero(string sqlTypeName)
    {
        // text/ntext report a non-MAX, non-actual max_length in sys.columns (a known SQL
        // Server legacy-LOB quirk) - confirms AddParam's "0 = no explicit size" convention
        // still applies rather than passing through a misleading byte count.
        var column = new ColumnProfile("TestColumn", sqlTypeName, 16, true);
        Assert.Equal(0, SqlTypeMapping.GetParameterSize(column));
    }
}
