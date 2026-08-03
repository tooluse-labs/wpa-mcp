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
    public void ApplyHelper_LaunchesTrustedInstallBinExecutableWithoutPowerShell()
    {
        var installRoot = Path.Combine(
            Environment.CurrentDirectory,
            "test-install");
        var trustedHelperExecutable = Path.Combine(
            installRoot,
            "bin",
            "wpa-mcp-update-helper-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.exe");
        var handoffPath = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-update-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "apply-update.v2.json");

        var startInfo = SelfUpdateApplyCommand.CreateApplyHelperStartInfo(
            trustedHelperExecutable,
            handoffPath);

        Assert.Equal(Path.GetFullPath(trustedHelperExecutable), startInfo.FileName);
        Assert.Equal(["--apply-update", Path.GetFullPath(handoffPath)], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(SelfUpdateApplyCommand.IsSafeApplyHelperPath(
            trustedHelperExecutable,
            installRoot));
        Assert.False(SelfUpdateApplyCommand.IsSafeApplyHelperPath(
            Path.Combine(Path.GetTempPath(), "wpa-mcp.exe"),
            installRoot));
        Assert.DoesNotContain("powershell", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalHelperInvocation_AndStageRoot_AreFailClosed()
    {
        var validRoot = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-update-0123456789abcdef0123456789abcdef");

        Assert.True(SelfUpdateApplyCommand.IsInvocation(
            ["--apply-update", Path.Combine(validRoot, "apply-update.v2.json")]));
        Assert.True(SelfUpdateApplyCommand.IsSafeUpdateStageRoot(validRoot));
        Assert.False(SelfUpdateApplyCommand.IsSafeUpdateStageRoot(Path.GetTempPath()));
        Assert.False(SelfUpdateApplyCommand.IsSafeUpdateStageRoot(
            Path.Combine(Path.GetTempPath(), "wpa-mcp-update-not-a-guid")));
    }
}
