using SmartTableSync.Planning;
using Xunit;

namespace SmartTableSync.Tests.Planning;

public class BatchSizeBackoffTests
{
    [Theory]
    [InlineData(200, 1, 100)]
    [InlineData(5, 1, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(1, 1, 1)]
    public void Halve_ReducesTowardFloor(int current, int floor, int expected)
    {
        Assert.Equal(expected, BatchSizeBackoff.Halve(current, floor));
    }

    [Fact]
    public void Halve_NeverGoesBelowFloor()
    {
        var result = BatchSizeBackoff.Halve(current: 1, floor: 5);
        Assert.Equal(5, result);
    }
}
