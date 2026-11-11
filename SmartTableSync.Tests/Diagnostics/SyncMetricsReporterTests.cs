using System.Diagnostics.Metrics;
using SmartTableSync.Diagnostics;

namespace SmartTableSync.Tests.Diagnostics;

public class SyncMetricsReporterTests
{
    [Fact]
    public void TableSyncFailed_AddsOneToTheFailuresCounterTaggedWithTheTable()
    {
        // Arrange - a table name unique to this test, so measurements from other tests are ignored
        var table = $"FailedTable_{Guid.NewGuid():N}";
        var measurements = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SyncMetricsReporter.MeterName && instrument.Name == "smarttablesync.table_sync_failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "TableName" && Equals(tag.Value, table))
                    measurements.Add(value);
            }
        });
        listener.Start();
        using var reporter = new SyncMetricsReporter();

        // Act
        reporter.TableSyncFailed(table);
        reporter.TableSyncFailed(table);

        // Assert
        Assert.Equal([1L, 1L], measurements);
    }
}
