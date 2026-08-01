using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class TraceCapabilitiesDetectorTests
{
    [Theory]
    [InlineData((int)ClrCapabilityEndpoint.GcStop)]
    [InlineData((int)ClrCapabilityEndpoint.GcSuspendEeStart)]
    [InlineData((int)ClrCapabilityEndpoint.GcRestartEeStop)]
    public void GcEndpointAlone_AdvertisesGcCapability(
        int endpointValue)
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();

        capabilities.Observe((ClrCapabilityEndpoint)endpointValue);

        Assert.True(capabilities.HasClrGc);
        Assert.False(capabilities.HasClrJit);
        Assert.False(capabilities.HasClrContention);
    }

    [Fact]
    public void MethodLoadVerboseAlone_AdvertisesJitCapability()
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();

        capabilities.Observe(ClrCapabilityEndpoint.MethodLoadVerbose);

        Assert.True(capabilities.HasClrJit);
        Assert.False(capabilities.HasClrGc);
        Assert.False(capabilities.HasClrContention);
    }

    [Fact]
    public void ManagedContentionStopAlone_AdvertisesCapabilityWithoutCoverage()
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();
        capabilities.Observe(ClrCapabilityEndpoint.ContentionStop);
        var process = new ProcessInstanceKey(12, 10);
        var thread = new ThreadInstanceKey(process, 77, 1);
        var pairer = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();
        pairer.AddStop(thread, 130, new ContentionStopData());

        var intervals = pairer.Complete();
        var coverage = TraceCapabilitiesDetector.ProjectClrContentionCoverage(intervals);

        Assert.True(capabilities.HasClrContention);
        Assert.Empty(intervals.Pairs);
        Assert.Single(intervals.UnmatchedStops);
        Assert.Equal("no_events", coverage.CoverageState);
        Assert.Equal(0, coverage.TotalEventCount);
    }
}
