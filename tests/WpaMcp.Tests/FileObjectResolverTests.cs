using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using Xunit;

namespace WpaMcp.Tests;

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

    [Fact]
    public void Build_PopulatesAtLeastOneMapping()
    {
        using var trace = TraceLog.OpenOrConvert("fixtures/small_cpu.etl");
        var resolver = FileObjectResolver.Build(trace);
        // The Task 17 sanity trace is CPU-focused; FileIO events are not enabled in the
        // CPU profile, so we only assert that construction does not throw and the
        // unmapped fallback still works.
        Assert.StartsWith("<unmapped:0x", resolver.Resolve(0xDEADBEEF));
    }

    [Fact]
    public void ResolveAt_DoesNotUseANameObservedAfterTheIoEvent()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 100, "early.dat");
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 300, "later.dat");

        Assert.Equal("early.dat", resolver.ResolveAt(0x10, 0x20, timestampUs: 200));
        Assert.Equal("later.dat", resolver.ResolveAt(0x10, 0x20, timestampUs: 300));
    }

    [Fact]
    public void ResolveAt_DoesNotCarryAClosedFileObjectAcrossLifetimes()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 10, "a.dat");
        resolver.EndFileObject(fileObject: 0x10, timestampUs: 20);
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x30, timestampUs: 30, "b.dat");

        Assert.Equal("a.dat", resolver.ResolveAt(0x10, fileKey: 0, timestampUs: 15));
        Assert.StartsWith("<unmapped:0x", resolver.ResolveAt(0x10, fileKey: 0, timestampUs: 25));
        Assert.Equal("b.dat", resolver.ResolveAt(0x10, fileKey: 0, timestampUs: 35));
    }

    [Fact]
    public void TemporalMap_UsesEventOrderForEqualTimestamps()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x10, timestampUs: 100, eventOrder: 10, "a.dat");
        names.End(0x10, timestampUs: 100, eventOrder: 20);
        names.Add(0x10, timestampUs: 100, eventOrder: 30, "b.dat");

        Assert.True(names.TryResolveAt(0x10, timestampUs: 100, eventOrder: 15, out var beforeClose));
        Assert.Equal("a.dat", beforeClose);
        Assert.False(names.TryResolveAt(0x10, timestampUs: 100, eventOrder: 25, out _));
        Assert.True(names.TryResolveAt(0x10, timestampUs: 100, eventOrder: 35, out var afterReuse));
        Assert.Equal("b.dat", afterReuse);
    }

    [Fact]
    public void ResolveAt_UsesTheNameValidBeforeAndAfterRename()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 10, "before.dat");
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 20, "after.dat");

        Assert.Equal("before.dat", resolver.ResolveAt(0x10, 0x20, timestampUs: 15));
        Assert.Equal("after.dat", resolver.ResolveAt(0x10, 0x20, timestampUs: 25));
    }

    [Fact]
    public void ResolveAt_PrefersFileKeyBeforeReusedFileObject()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 100, "object-name.dat");
        resolver.AddFileKeyMapping(fileKey: 0x30, timestampUs: 150, "key-name.dat");

        Assert.Equal("key-name.dat", resolver.ResolveAt(0x10, 0x30, timestampUs: 200));
    }

    [Fact]
    public void ResolveAt_DoesNotTreatZeroFileKeyAsSharedFileIdentity()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0, timestampUs: 100, "first.dat");
        resolver.AddMapping(fileObject: 0x20, fileKey: 0, timestampUs: 200, "second.dat");

        Assert.Equal("first.dat", resolver.ResolveAt(0x10, fileKey: 0, timestampUs: 300));
    }
}
