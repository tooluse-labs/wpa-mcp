using WprMcp.Core;

namespace WprMcp.Tests;

public class SymbolHintCatalogTests
{
    [Fact]
    public void Match_FfmpegName_ReturnsNoPdbEntry()
    {
        var entry = SymbolHintCatalog.Match("ffmpeg");
        Assert.NotNull(entry);
        Assert.Null(entry!.ServerUrl);
        Assert.Contains("no public PDB server", entry.DiagnoseHint);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("chromium")]
    [InlineData("electron")]
    [InlineData("cef")]
    public void Match_ChromiumModuleName_ReturnsChromiumEntry(string moduleName)
    {
        var entry = SymbolHintCatalog.Match(moduleName);
        Assert.NotNull(entry);
        Assert.Equal("https://chromium-browser-symsrv.commondatastorage.googleapis.com", entry!.ServerUrl);
        Assert.Equal("Chromium-based browser (Chrome / Electron / CEF)", entry.LoadTraceReason);
        Assert.Contains("Add Chromium symbol server", entry.DiagnoseHint);
    }
}
