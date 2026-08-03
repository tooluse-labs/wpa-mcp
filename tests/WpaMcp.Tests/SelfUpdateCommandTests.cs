using WpaMcp.Cli;

namespace WpaMcp.Tests;

public sealed class SelfUpdateCommandTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("--update")]
    public void TryParseArguments_AcceptsDefaultInvocation(string command)
    {
        var accepted = SelfUpdateCommand.TryParseArguments([command], out var stopRunning);

        Assert.True(accepted);
        Assert.False(stopRunning);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("--update")]
    public void TryParseArguments_AcceptsExplicitStopRunning(string command)
    {
        var accepted = SelfUpdateCommand.TryParseArguments(
            [command, "--stop-running"],
            out var stopRunning);

        Assert.True(accepted);
        Assert.True(stopRunning);
    }

    [Fact]
    public void TryParseArguments_RejectsAmbiguousForceAndExtraArguments()
    {
        Assert.False(SelfUpdateCommand.TryParseArguments(
            ["update", "--force"],
            out _));
        Assert.False(SelfUpdateCommand.TryParseArguments(
            ["update", "--stop-running", "extra"],
            out _));
    }

    [Fact]
    public void BlockingMessage_IsActionableAndNonDestructiveByDefault()
    {
        var message = SelfUpdateCommand.BuildBlockingProcessMessage(
            @"C:\Users\admin3\.local\bin\wpa-mcp.exe",
            [91100, 96992]);

        Assert.Contains("91100, 96992", message, StringComparison.Ordinal);
        Assert.Contains("update --stop-running", message, StringComparison.Ordinal);
        Assert.Contains("No process was terminated", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsEqual_IsCaseInsensitiveButPathExact()
    {
        Assert.True(SelfUpdateCommand.PathsEqual(
            @"C:\Users\Admin3\.local\bin\WPA-MCP.EXE",
            @"c:\users\admin3\.local\bin\wpa-mcp.exe"));
        Assert.False(SelfUpdateCommand.PathsEqual(
            @"C:\other\wpa-mcp.exe",
            @"C:\Users\admin3\.local\bin\wpa-mcp.exe"));
    }

    [Fact]
    public void ApplyHelper_UsesExactPathPolicyAndPersistentLog()
    {
        var script = SelfUpdateCommand.ApplyUpdateScriptForTests;

        Assert.Contains("$process.MainModule.FileName", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $process.Id -Force", script, StringComparison.Ordinal);
        Assert.Contains("Move-ExecutableWithPolicy", script, StringComparison.Ordinal);
        Assert.Contains(".wpa-mcp-update.log", script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", script, StringComparison.OrdinalIgnoreCase);
    }
}
