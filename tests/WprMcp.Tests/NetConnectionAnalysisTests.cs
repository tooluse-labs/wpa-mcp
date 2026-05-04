using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class NetConnectionAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void NetConnections_NoNetworkTrace_EmptyAndWarns()
    {
        // small_cpu.etl doesn't enable the NetworkTrace keyword.
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        var resp = tools.NetConnections(FixturePath);
        Assert.Empty(resp.Connections);
        Assert.Equal(0, resp.TotalConnections);
        Assert.Contains(resp.Warnings, w => w.Contains("NetworkTrace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NetConnections_PidFilterReflectedInResponse()
    {
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        var resp = tools.NetConnections(FixturePath, pid: 999_999);
        Assert.Equal(999_999, resp.Pid);
        Assert.Empty(resp.Connections);
    }

    [Fact]
    public void NetConnections_RejectsBadTop()
    {
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.NetConnections("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.NetConnections("nonexistent.etl", top: 1001));
    }
}
