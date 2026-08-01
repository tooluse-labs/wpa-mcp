using System.ComponentModel;
using WpaMcp.Core;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class ReadyThreadContractTests
{
    private const string FixturePath = "fixtures/small_wait_bound.etl";

    [Fact]
    public void StackTools_DescribeAssociationEvidenceWithoutClaimingCausality()
    {
        var methods = new[]
        {
            typeof(ReadyThreadTools).GetMethod(nameof(ReadyThreadTools.ReadyThreadTopStacks))!,
            typeof(ReadyThreadTools).GetMethod(nameof(ReadyThreadTools.ReadyThreadCallerCallee))!,
        };

        foreach (var method in methods)
        {
            var description = Assert.IsType<DescriptionAttribute>(
                Attribute.GetCustomAttribute(method, typeof(DescriptionAttribute))).Description;

            Assert.Contains("associated readier/wakeup stack evidence", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("awakenedPid", description, StringComparison.Ordinal);
            Assert.Contains("window", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not paired one-to-one", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot alone establish root cause", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("who unblocked", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("closing the", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("explained by", description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StackResponses_AlwaysWarnThatReadyThreadEvidenceIsAssociationOnly()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new ReadyThreadTools(cache);
        var top = tools.ReadyThreadTopStacks(FixturePath, top: 10);
        var focus = Assert.Single(top.Rows.Take(1)).Function;
        var callerCallee = tools.ReadyThreadCallerCallee(FixturePath, focus, top: 10);

        AssertAssociationOnlyWarning(top.Warnings);
        AssertAssociationOnlyWarning(callerCallee.Warnings);
    }

    private static void AssertAssociationOnlyWarning(IReadOnlyList<string> warnings)
    {
        var warning = Assert.Single(
            warnings,
            item => item.StartsWith("association_only:", StringComparison.Ordinal));

        Assert.Contains("awakenedPid", warning, StringComparison.Ordinal);
        Assert.Contains("requested window", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not paired one-to-one", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subsequent CSwitch", warning, StringComparison.Ordinal);
        Assert.Contains("cannot alone establish root cause", warning, StringComparison.OrdinalIgnoreCase);
    }
}
