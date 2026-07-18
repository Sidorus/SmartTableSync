using SmartTableSync.Introspection;
using SmartTableSync.Syncers;
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
    [InlineData("int", false)]
    [InlineData("datetime", false)]
    public void IsStringType_ClassifiesCorrectly(string sqlTypeName, bool expected)
    {
        Assert.Equal(expected, SqlTypeMapping.IsStringType(sqlTypeName));
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
}
