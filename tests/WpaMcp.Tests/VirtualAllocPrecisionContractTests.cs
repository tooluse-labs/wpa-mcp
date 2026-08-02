using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class VirtualAllocPrecisionContractTests
{
    [Fact]
    public void OperationTotals_SeparateAllocAndFreeWithoutFloatPrecisionLoss()
    {
        const long aboveFloatIntegerPrecision = (1L << 24) + 1;
        var totals = new VirtualAllocOperationAccumulator();

        totals.ObserveAllocation(aboveFloatIntegerPrecision);
        totals.ObserveAllocation(4_096);
        totals.ObserveFree(8_192);

        Assert.Equal(aboveFloatIntegerPrecision + 4_096, totals.AllocatedBytes);
        Assert.Equal(2, totals.AllocatedCount);
        Assert.Equal(8_192, totals.FreedBytes);
        Assert.Equal(1, totals.FreedCount);
        Assert.Equal(aboveFloatIntegerPrecision + 12_288, totals.TotalOperationBytes);
        Assert.Equal(aboveFloatIntegerPrecision - 4_096, totals.NetObservedOperationBytes);
        Assert.Equal(3, totals.TotalOperationCount);
    }

    [Fact]
    public void OperationTotals_CountZeroLengthEventsButKeepTheirByteTotalsZero()
    {
        var totals = new VirtualAllocOperationAccumulator();

        totals.ObserveAllocation(0);
        totals.ObserveFree(0);

        Assert.Equal(1, totals.AllocatedCount);
        Assert.Equal(1, totals.FreedCount);
        Assert.Equal(2, totals.TotalOperationCount);
        Assert.Equal(0, totals.TotalOperationBytes);
        Assert.Equal(0, totals.NetObservedOperationBytes);
    }

    [Fact]
    public void DomainCoverage_DeclaresExactLongAccountingAboveFloatPrecisionBoundary()
    {
        const long aboveFloatIntegerPrecision = (1L << 24) + 1;
        var coverage = new DomainStackCoverageAccumulator("virtual_alloc", "virtualMemoryOperationBytes");

        coverage.Observe(hasStack: true, aboveFloatIntegerPrecision);

        var snapshot = coverage.Snapshot();
        Assert.Equal(aboveFloatIntegerPrecision, snapshot.TotalMetric);
        Assert.Equal(aboveFloatIntegerPrecision, snapshot.StackedMetric);
        Assert.Equal("exact_long", snapshot.MetricAccounting);
    }

    [Fact]
    public void EveryStackTopResponse_ExposesMachineReadableMetricAccounting()
    {
        var responseTypes = new Dictionary<Type, string>
        {
            [typeof(CpuTopFunctionsResponse)] = "exact_integer_count",
            [typeof(WaitTopStacksResponse)] = "exact_long",
            [typeof(FileIoStacksResponse)] = "exact_long",
            [typeof(DiskIoStacksResponse)] = "exact_long",
            [typeof(HardFaultStacksResponse)] = "exact_long",
            [typeof(ImageLoadStacksResponse)] = "exact_integer_count",
            [typeof(VirtualAllocStacksResponse)] = "exact_long",
            [typeof(NetIoStacksResponse)] = "exact_long",
            [typeof(RegistryStacksResponse)] = "exact_integer_count",
            [typeof(ReadyThreadStacksResponse)] = "exact_integer_count",
            [typeof(InterruptStacksResponse)] = "exact_long",
            [typeof(AlpcStacksResponse)] = "exact_integer_count",
            [typeof(ClrAllocStacksResponse)] = "exact_long",
            [typeof(ClrExceptionStacksResponse)] = "exact_integer_count",
            [typeof(ClrContentionStacksResponse)] = "exact_long",
            [typeof(HeapAllocStacksResponse)] = "exact_long",
            [typeof(GenericEventStacksResponse)] = "exact_integer_count",
        };

        foreach (var (responseType, expectedPrecision) in responseTypes)
        {
            Assert.NotNull(responseType.GetProperty("MetricPrecision"));
            Assert.NotNull(responseType.GetProperty("RowMetricAccounting"));
            Assert.NotNull(responseType.GetProperty("ExactTotalAccounting"));

            var parameters = Assert.Single(responseType.GetConstructors()).GetParameters();
            Assert.Equal(expectedPrecision, Assert.Single(parameters,
                parameter => parameter.Name == "MetricPrecision").DefaultValue);
            Assert.Equal(expectedPrecision, Assert.Single(parameters,
                parameter => parameter.Name == "RowMetricAccounting").DefaultValue);
            Assert.Equal("exact_long", Assert.Single(parameters,
                parameter => parameter.Name == "ExactTotalAccounting").DefaultValue);
        }

        Assert.NotNull(typeof(CallerCalleeResponse).GetProperty("MetricPrecision"));
        Assert.NotNull(typeof(CallerCalleeResponse).GetProperty("RowMetricAccounting"));
        Assert.NotNull(typeof(CallerCalleeResponse).GetProperty("ExactTotalAccounting"));
    }

    [Theory]
    [InlineData("count", "exact_integer_count")]
    [InlineData("samples", "exact_integer_count")]
    [InlineData("readyEvents", "exact_integer_count")]
    [InlineData("bytes", "exact_long")]
    [InlineData("us", "exact_long")]
    [InlineData("virtualMemoryOperationBytes", "exact_long")]
    public void PrecisionClassifier_SeparatesUnitCountsFromWeightedMetrics(
        string metricName,
        string expected)
    {
        Assert.Equal(expected, StackMetricAccounting.ForMetric(metricName));
    }
}
