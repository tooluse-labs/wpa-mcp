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
}
