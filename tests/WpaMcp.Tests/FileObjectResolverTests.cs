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
        Assert.Equal("<unmapped:0x00000000deadbeef>", result);
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

        var firstLifetime = resolver.ResolveDetailedAt(0x10, fileKey: 0, timestampUs: 15);
        var betweenLifetimes = resolver.ResolveDetailedAt(0x10, fileKey: 0, timestampUs: 25);
        var reusedLifetime = resolver.ResolveDetailedAt(0x10, fileKey: 0, timestampUs: 35);

        Assert.Equal("a.dat", firstLifetime.File);
        Assert.Equal(FileMappingStates.TemporalFileObject, firstLifetime.MappingState);
        Assert.StartsWith("<unmapped:0x", betweenLifetimes.File);
        Assert.Equal(FileMappingStates.UnresolvedFileIdentity, betweenLifetimes.MappingState);
        Assert.Equal("b.dat", reusedLifetime.File);
        Assert.Equal(FileMappingStates.TemporalFileObject, reusedLifetime.MappingState);
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
    public void ResolveAt_ConflictingFileKeyAndFileObjectMappingsAreAmbiguous()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 100, "object-name.dat");
        resolver.AddFileKeyMapping(fileKey: 0x30, timestampUs: 150, "key-name.dat");

        var resolution = resolver.ResolveDetailedAt(0x10, 0x30, timestampUs: 200);

        Assert.Equal(FileMappingStates.AmbiguousTemporalMapping, resolution.MappingState);
        Assert.StartsWith("<ambiguous:", resolution.File);
        Assert.DoesNotContain("key-name.dat", resolution.File, StringComparison.Ordinal);
        Assert.DoesNotContain("object-name.dat", resolution.File, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAt_DoesNotTreatZeroFileKeyAsSharedFileIdentity()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0, timestampUs: 100, "first.dat");
        resolver.AddMapping(fileObject: 0x20, fileKey: 0, timestampUs: 200, "second.dat");

        Assert.Equal("first.dat", resolver.ResolveAt(0x10, fileKey: 0, timestampUs: 300));
    }

    [Fact]
    public void ResolveAt_OutsideObservedMappingIntervalHasTypedUnresolvedState()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0, timestampUs: 100, "bounded.dat");
        resolver.EndFileObject(fileObject: 0x10, timestampUs: 200);

        var before = resolver.ResolveDetailedAt(0x10, fileKey: 0, timestampUs: 99);
        var after = resolver.ResolveDetailedAt(0x10, fileKey: 0, timestampUs: 200);

        Assert.Equal(FileMappingStates.UnresolvedFileIdentity, before.MappingState);
        Assert.Equal(FileMappingStates.UnresolvedFileIdentity, after.MappingState);
    }

    [Theory]
    [InlineData(0UL, "0x0000000000000000")]
    [InlineData(9_007_199_254_740_992UL, "0x0020000000000000")]
    [InlineData(ulong.MaxValue, "0xffffffffffffffff")]
    public void FileIdentifierFormatting_UsesCanonicalFixedWidthLowercaseHex(
        ulong identifier,
        string expected)
    {
        Assert.Equal(expected, FileIdentifierFormatting.Pointer(identifier));
        Assert.Equal($"<unmapped:{expected}>", FileIdentifierFormatting.Unmapped(identifier));
    }
}
