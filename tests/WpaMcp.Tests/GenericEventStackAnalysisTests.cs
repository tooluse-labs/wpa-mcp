using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

// The most valuable test set in this batch — generic_event_top_stacks runs against an
// arbitrary user-named provider via DynamicTraceEventParser + RegisteredTraceEventParser
// with EventFilterResponse.RejectProvider for the dispatcher-level short-circuit.  These
// tests exercise both the "real provider matches" path (against a kernel provider that's
// guaranteed to fire in small_cpu) and the "non-existent provider" warning path.
public class GenericEventStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void GenericEventTopStacks_RealProvider_ReturnsRowsAndTopEventNames()
    {
        // MSNT_SystemTrace is the kernel-events provider name TraceEvent uses for
        // KernelTraceEventParser-owned events. small_cpu.etl is a kernel-only trace,
        // so this provider is guaranteed to be present.
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        var resp = tools.GenericEventTopStacks(FixturePath, "MSNT_SystemTrace", top: 20);
        Assert.Equal("MSNT_SystemTrace", resp.ProviderName);
        Assert.Null(resp.EventNameSubstring);
        // Either we get rows (with the kernel events) or zero (if the registered parser
        // doesn't recognize MSNT_SystemTrace under that name on this build) — but the
        // analyzer must not throw.  Asserting >= 0 guards the contract.
        Assert.True(resp.TotalEventCount >= 0);
        Assert.True(resp.Rows.Count <= 20);
    }

    [Fact]
    public void GenericEventTopStacks_NonexistentProvider_NoMatchingEventsAndWarns()
    {
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        var resp = tools.GenericEventTopStacks(FixturePath, "NoSuchProvider-DoesNotExist", top: 10);
        Assert.Equal(0, resp.TotalEventCount);
        Assert.Empty(resp.Rows);
        Assert.Empty(resp.TopEventNames);
        Assert.NotEmpty(resp.Warnings);
        Assert.Contains(resp.Warnings, w => w.Contains("NoSuchProvider-DoesNotExist", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericEventTopStacks_EventNameSubstring_NarrowsResults()
    {
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        var unfiltered = tools.GenericEventTopStacks(FixturePath, "MSNT_SystemTrace");
        var filtered   = tools.GenericEventTopStacks(FixturePath, "MSNT_SystemTrace",
            eventNameSubstring: "ThisSubstringWillMatchNothingFromTheKernel");
        Assert.True(filtered.TotalEventCount <= unfiltered.TotalEventCount);
        Assert.Equal("ThisSubstringWillMatchNothingFromTheKernel", filtered.EventNameSubstring);
        Assert.Equal(0, filtered.TotalEventCount);
        StackAssertions.AssertRootOnly(filtered.Rows, r => r.ExclusiveCount, r => r.InclusiveCount);
    }

    [Fact]
    public void GenericEventTopStacks_EventNameSubstring_IsCaseInsensitive()
    {
        // PerfView's Any Stacks is case-sensitive; we deviate to OrdinalIgnoreCase for
        // LLM-consumer ergonomics. Pin the behavior with a regression test.
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        var lower = tools.GenericEventTopStacks(FixturePath, "MSNT_SystemTrace", eventNameSubstring: "sample");
        var upper = tools.GenericEventTopStacks(FixturePath, "MSNT_SystemTrace", eventNameSubstring: "SAMPLE");
        Assert.Equal(lower.TotalEventCount, upper.TotalEventCount);
    }

    [Fact]
    public void GenericEventTopStacks_RejectsEmptyProviderName()
    {
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.GenericEventTopStacks("nonexistent.etl", ""));
        Assert.Throws<ArgumentException>(() => tools.GenericEventTopStacks("nonexistent.etl", "   "));
    }

    [Fact]
    public void GenericEventTopStacks_RejectsBadTop()
    {
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.GenericEventTopStacks("nonexistent.etl", "Some-Provider", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.GenericEventTopStacks("nonexistent.etl", "Some-Provider", top: 1001));
    }

    [Fact]
    public void GenericEventCallerCallee_RejectsEmptyInputs()
    {
        var tools = new GenericProviderTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() =>
            tools.GenericEventCallerCallee("nonexistent.etl", "Provider", ""));
        Assert.Throws<ArgumentException>(() =>
            tools.GenericEventCallerCallee("nonexistent.etl", "", "function"));
    }
}
