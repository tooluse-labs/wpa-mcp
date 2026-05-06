namespace WprMcp.Tests;

// Regression coverage for commit 1252eaf — the documented one-liner
//   iex "& { $(irm $URL) } -InstallArgs '...'"
// failed with cascading parse errors because scripts/install.ps1 was saved with
// a UTF-8 BOM. `irm` decodes UTF-8 but does not strip the BOM, so the U+FEFF
// landed mid-string between `& {` and `<#`, and PS 5.1's parser could not
// tokenize the comment-block opener that followed.
//
// Two coupled invariants keep the script safe end-to-end:
//   1. No leading UTF-8 BOM, so the `irm | iex` wrapper form parses.
//   2. ASCII-only content, because a BOM-less .ps1 is read by PS 5.1 via the
//      current ANSI codepage and any non-ASCII byte would be corrupted on
//      local execution (`.\install.ps1`).
// Removing the BOM is only safe if the file is also ASCII-only — they have to
// move together, which is why both are asserted here.
public class WebInstallerScriptTests
{
    private static string LocateInstallScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WprMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "scripts", "install.ps1");
        Assert.True(File.Exists(path), $"Expected install script at {path}");
        return path;
    }

    [Fact]
    public void InstallScriptHasNoUtf8Bom()
    {
        var bytes = File.ReadAllBytes(LocateInstallScript());
        Assert.True(bytes.Length >= 3, "scripts/install.ps1 is suspiciously short.");

        var hasBom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom,
            "scripts/install.ps1 starts with a UTF-8 BOM. This re-breaks " +
            "the documented `iex \"& { $(irm $URL) } -InstallArgs '...'\"` " +
            "one-liner: `irm` decodes UTF-8 without stripping the BOM, and " +
            "PS 5.1 cannot tokenize `<#` after a U+FEFF interpolated mid-string. " +
            "Resave as UTF-8 *without* BOM.");
    }

    [Fact]
    public void InstallScriptIsAsciiOnly()
    {
        var content = File.ReadAllText(LocateInstallScript());
        var offenders = new List<string>();
        var line = 1;
        var col = 0;
        foreach (var ch in content)
        {
            col++;
            if (ch == '\n') { line++; col = 0; continue; }
            if (ch > 0x7F)
                offenders.Add($"  line {line} col {col}: U+{(int)ch:X4} '{ch}'");
        }

        Assert.True(offenders.Count == 0,
            "scripts/install.ps1 contains non-ASCII characters. Without a BOM " +
            "(see InstallScriptHasNoUtf8Bom) PS 5.1 reads .ps1 files via the " +
            "ANSI codepage, which corrupts any non-ASCII byte on local " +
            "execution. Replace with ASCII equivalents (e.g. `--` for em dash, " +
            "`<=` for U+2264):\n" + string.Join("\n", offenders));
    }
}
