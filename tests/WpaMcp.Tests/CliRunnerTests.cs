using WpaMcp.Cli;
using Xunit;

namespace WpaMcp.Tests;

public class CliRunnerTests
{
    [Fact]
    public void IsCliInvocation_RecognizesAllKnownVerbs()
    {
        Assert.True(CliRunner.IsCliInvocation(new[] { "--list-processes", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--process-create-timing", "x.etl", "1234" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--cpu-top", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--cpu-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--wait-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--image-load-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--hard-fault-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--file-io-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--disk-io-top-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--disk-io-caller-callee", "x.etl", "fn" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--wait-analysis", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--wait-top-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--image-load-timing", "x.etl", "1234" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--image-load-top-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--image-load-top-gaps", "x.etl", "1234" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--hard-fault-top-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--file-io-top-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--diagnose-slow-startup", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--find-marker", "x.etl", "Sample" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--probe-stacks", "x.etl" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--help" }));
        Assert.True(CliRunner.IsCliInvocation(new[] { "-h" }));
    }

    [Fact]
    public void IsCliInvocation_RejectsUnknownAndEmpty()
    {
        Assert.False(CliRunner.IsCliInvocation(Array.Empty<string>()));
        Assert.False(CliRunner.IsCliInvocation(new[] { "--bogus" }));
        Assert.False(CliRunner.IsCliInvocation(new[] { "list-processes" })); // missing leading "--"
    }

    [Fact]
    public void Run_HelpVerbReturnsZero()
    {
        Assert.Equal(0, CliRunner.Run(new[] { "--help" }));
    }

    [Fact]
    public void Run_NoArgsPrintsHelpAndReturnsZero()
    {
        // No-arg invocation should NOT exit 2 (which is "user error"): Program.Main routes
        // empty-args to the MCP server, but Cli.Run itself defaults to help-on-stdout (rc=0).
        Assert.Equal(0, CliRunner.Run(Array.Empty<string>()));
    }

    [Fact]
    public void Run_UnknownVerbReturnsErrorCode()
    {
        Assert.Equal(2, CliRunner.Run(new[] { "--bogus", "x.etl" }));
    }

    [Fact]
    public void Run_CatchesAnalyzerExceptions_AndReturnsCode2()
    {
        // Missing trace path → FileNotFoundException inside TraceCache.Get; CliRunner must catch.
        var rc = CliRunner.Run(new[] { "--list-processes", "definitely-not-a-real-trace.etl" });
        Assert.Equal(2, rc);
    }
}
