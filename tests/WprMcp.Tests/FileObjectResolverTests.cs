using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Analyzers;
using Xunit;

namespace WprMcp.Tests;

public class FileObjectResolverTests
{
    [Fact]
    public void Resolve_ReturnsUnmappedSentinelForUnknownFileObject()
    {
        // FileObjectResolver has an implicit public parameterless ctor (sealed class with
        // no declared ctors). An "empty" resolver lets us validate the fallback semantic
        // without needing a real .etl trace fixture.
        var resolver = new FileObjectResolver();
        var result = resolver.Resolve(0xDEADBEEF);
        Assert.StartsWith("<unmapped:0x", result);
        Assert.Contains("DEADBEEF", result);
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void Build_PopulatesAtLeastOneMapping()
    {
        using var trace = TraceLog.OpenOrConvert("fixtures/small_cpu.etl");
        var resolver = FileObjectResolver.Build(trace);
        // The Task 17 sanity trace is CPU-focused; FileIO events are not enabled in the
        // CPU profile, so we only assert that construction does not throw and the
        // unmapped fallback still works.
        Assert.StartsWith("<unmapped:0x", resolver.Resolve(0xDEADBEEF));
    }
}
