using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public class TraceCacheTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured by fixtures/capture_all.ps1

    [Fact]
    public void Get_ReturnsSameInstanceAcrossCalls()
    {
        var cache = new TraceCache(capacity: 2);
        var t1 = cache.Get(FixturePath);
        var t2 = cache.Get(FixturePath);
        Assert.Same(t1, t2);
    }

    [Fact]
    public void Get_EvictsAndReloadsAfterMtimeBump()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wpa-mcp-mtime-{Guid.NewGuid():N}.etl");
        File.Copy(FixturePath, tmp);
        try
        {
            var cache = new TraceCache(capacity: 2);
            var t1 = cache.Get(tmp);

            // Bump mtime by re-copying.
            File.Copy(FixturePath, tmp, overwrite: true);
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddSeconds(5));

            var t2 = cache.Get(tmp);
            Assert.NotSame(t1, t2);
        }
        finally
        {
            try { File.Delete(tmp); File.Delete(tmp + "x"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Unload_RemovesEntryAndAllowsReload()
    {
        var cache = new TraceCache(capacity: 2);
        var t1 = cache.Get(FixturePath);
        Assert.True(cache.Unload(FixturePath));
        var t2 = cache.Get(FixturePath);
        Assert.NotSame(t1, t2);
    }

    [Fact]
    public void Get_ThrowsForMissingFile()
    {
        var cache = new TraceCache(capacity: 2);
        Assert.Throws<FileNotFoundException>(() => cache.Get("nonexistent.etl"));
    }
}
