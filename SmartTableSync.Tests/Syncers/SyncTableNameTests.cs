using SmartTableSync.Sql;
using Xunit;

namespace SmartTableSync.Tests.Syncers;

public class SyncTableNameTests
{
    [Theory]
    [InlineData("Order", "Order")]
    [InlineData("dbo.Order", "Order")]
    [InlineData("DBO.Order", "Order")]
    [InlineData(" sales . Order ", "sales.Order")]
    [InlineData("sales.Order", "sales.Order")]
    public void Normalize_DboIsUnqualified_OtherSchemasAreKept(string configured, string expected)
    {
        Assert.Equal(expected, SyncTableName.Normalize(configured));
    }

    [Theory]
    [InlineData("a.b.c")]
    [InlineData(".Order")]
    [InlineData("sales.")]
    [InlineData("")]
    public void Normalize_InvalidName_Throws(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => SyncTableName.Normalize(configured));
    }

    [Fact]
    public void Normalize_LongerThanTheTrackingColumns_Throws()
    {
        var name = "sales." + new string('x', SyncTableName.MaxLength);

        var ex = Assert.Throws<InvalidOperationException>(() => SyncTableName.Normalize(name));
        Assert.Contains("100", ex.Message);
    }

    [Theory]
    [InlineData("Order", "dbo", "Order")]
    [InlineData("sales.Order", "sales", "Order")]
    public void SchemaAndObject_SplitTheName(string tableName, string schema, string objectName)
    {
        Assert.Equal(schema, SyncTableName.Schema(tableName));
        Assert.Equal(objectName, SyncTableName.Object(tableName));
    }

    [Fact]
    public void TableTypeName_CarriesNoSchema()
    {
        Assert.Equal("OrderTableType", SyncTableName.TableTypeName("Order"));
        Assert.Equal("OrderTableType", SyncTableName.TableTypeName("sales.Order"));
    }

    [Fact]
    public void SqlIdentifierTable_QuotesSchemaAndName()
    {
        Assert.Equal("[dbo].[Order]", SqlIdentifier.Table("Order"));
        Assert.Equal("[sales].[Order]", SqlIdentifier.Table("sales.Order"));
        Assert.Equal("[sales].[sp_SyncOrder]", SqlIdentifier.InTableSchema("sales.Order", "sp_SyncOrder"));
    }
}
