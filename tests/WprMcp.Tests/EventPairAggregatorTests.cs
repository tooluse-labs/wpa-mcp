using WprMcp.Analyzers;

namespace WprMcp.Tests;

public sealed class EventPairAggregatorTests
{
    [Fact]
    public void Complete_PairsStartStopByKeyInFifoOrder()
    {
        var aggregator = new EventPairAggregator();

        aggregator.AddStart("scan-1", 10, Fields("Path", "a"));
        aggregator.AddStart("scan-1", 20, Fields("Path", "b"));
        aggregator.AddStop("scan-1", 35, Fields("Status", "ok"));
        aggregator.AddStop("scan-1", 60, Fields("Status", "ok"));

        var result = aggregator.Complete();

        Assert.Equal([25, 40], result.Pairs.Select(pair => pair.DurationUs).ToArray());
        Assert.Equal(["a", "b"], result.Pairs.Select(pair => pair.StartFields["Path"]).ToArray());
        Assert.Empty(result.UnmatchedStarts);
        Assert.Empty(result.UnmatchedStops);
    }

    [Fact]
    public void Complete_ReportsUnmatchedStartsAndStops()
    {
        var aggregator = new EventPairAggregator();

        aggregator.AddStop("missing-start", 10, Fields("Status", "early"));
        aggregator.AddStart("missing-stop", 20, Fields("Path", "late"));

        var result = aggregator.Complete();

        Assert.Empty(result.Pairs);
        var unmatchedStart = Assert.Single(result.UnmatchedStarts);
        var unmatchedStop = Assert.Single(result.UnmatchedStops);
        Assert.Equal("missing-stop", unmatchedStart.Key);
        Assert.Equal("missing-start", unmatchedStop.Key);
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        var aggregator = new EventPairAggregator();
        aggregator.AddStart("scan-1", 10, Fields("Path", "a"));

        var first = aggregator.Complete();
        var second = aggregator.Complete();

        Assert.Same(first.UnmatchedStarts, second.UnmatchedStarts);
        Assert.Single(second.UnmatchedStarts);
    }

    private static IReadOnlyDictionary<string, string> Fields(string name, string value) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = value,
        };
}
