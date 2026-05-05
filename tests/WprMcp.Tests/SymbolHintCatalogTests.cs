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

    [Theory]
    [InlineData("ntoskrnl")]
    [InlineData("ntdll")]
    [InlineData("kernel32")]
    [InlineData("msvcp")]
    [InlineData("mpengine")]
    [InlineData("tcpip.sys")]   // kernel-driver pattern: extension is part of Name
    [InlineData("msedge")]       // corrected routing: MS, not Chromium
    [InlineData("msedgewebview2")] // covered by `msedge` substring
    public void Match_MicrosoftModuleName_ReturnsMicrosoftEntry(string moduleName)
    {
        var entry = SymbolHintCatalog.Match(moduleName);
        Assert.NotNull(entry);
        Assert.Equal("https://msdl.microsoft.com/download/symbols", entry!.ServerUrl);
        Assert.Equal("Microsoft public symbols", entry.LoadTraceReason);
        Assert.Contains("Add Microsoft symbol server", entry.DiagnoseHint);
    }

    [Fact]
    public void Match_UnknownModule_ReturnsNull()
    {
        Assert.Null(SymbolHintCatalog.Match("MyAppPrivateDll"));
        Assert.Null(SymbolHintCatalog.Match(""));
        Assert.Null(SymbolHintCatalog.Match(null!));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        Assert.NotNull(SymbolHintCatalog.Match("NTOSKRNL"));
        Assert.NotNull(SymbolHintCatalog.Match("FFmpeg"));
        Assert.NotNull(SymbolHintCatalog.Match("Chrome"));
    }

    [Fact]
    public void Match_NoPdbTakesPrecedenceOverMicrosoft()
    {
        // A name containing both "ffmpeg" and "ntdll" must match the no-PDB tier first
        // because no-PDB precedes Microsoft in the catalog. This locks in the
        // first-match-wins ordering invariant.
        var entry = SymbolHintCatalog.Match("ffmpeg-with-ntdll-in-name");
        Assert.NotNull(entry);
        Assert.Null(entry!.ServerUrl);
    }

    [Fact]
    public void Entries_HasExactlyThreeTiers()
    {
        // Sanity guard: extending the catalog with a 4th tier should be a deliberate
        // decision, not an accident.  Adding a tier: bump this assertion AND add a
        // precedence test pinning the new tier's relative position to existing tiers
        // (see Match_NoPdbTakesPrecedenceOverMicrosoft for the existing example).
        Assert.Equal(3, SymbolHintCatalog.Entries.Count);
    }
}
