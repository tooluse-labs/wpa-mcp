using System.Text.RegularExpressions;

namespace WprMcp.Tests;

// Regression coverage for the installer scripts in scripts/.  Every check here pins
// a fix to a real, reported failure — read the per-test commentary for the symptom
// each one is preventing.
public class InstallerScriptTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WprMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string LocateScript(string name)
    {
        var path = Path.Combine(LocateRepoRoot(), "scripts", name);
        Assert.True(File.Exists(path), $"Expected script at {path}");
        return path;
    }

    // -- scripts/install.ps1 -----------------------------------------------
    // The documented one-liner
    //   iex "& { $(irm $URL) } -InstallArgs '...'"
    // failed with cascading parse errors when install.ps1 was saved with a
    // UTF-8 BOM. `irm` decodes UTF-8 but does not strip the BOM, so the U+FEFF
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

    [Fact]
    public void InstallScriptHasNoUtf8Bom()
    {
        var bytes = File.ReadAllBytes(LocateScript("install.ps1"));
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
        var content = File.ReadAllText(LocateScript("install.ps1"));
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

    // -- scripts/setup.ps1 -------------------------------------------------
    // A fresh install via the web one-liner failed with
    //   '8.0' is not a supported value for -Quality option.
    // because Ensure-DotNet splatted the bootstrap args as a string array:
    //   $bootstrapArgs = @('-Channel', '8.0'); & $bootstrapPath @bootstrapArgs
    // PowerShell's array-splat semantics pass the elements POSITIONALLY (the
    // `-Channel` token is treated as a literal value, not a parameter name),
    // so dotnet-install.ps1's first positional param ($Channel) received the
    // string '-Channel' and '8.0' fell through to the second positional
    // ($Quality), which threw the user-visible error.
    //
    // Hashtable splat (`@{ Channel = '8.0' }`) binds by name and avoids the
    // trap.  The check below pins the fix in place: the bootstrap args must
    // be assembled as a hashtable, not an array of flag strings.

    [Fact]
    public void SetupScriptUsesHashtableSplatForDotNetBootstrap()
    {
        var content = File.ReadAllText(LocateScript("setup.ps1"));

        Assert.DoesNotMatch(@"\$bootstrapArgs\s*=\s*@\(", content);
        Assert.Matches(@"\$bootstrapArgs\s*=\s*@\{[^}]*Channel\s*=", content);
    }

    // -- scripts/install.sh -------------------------------------------------
    // Fresh installs via `curl ... | bash` on MSYS2/Git Bash failed inside
    // dotnet-install.ps1 with
    //   Could not find a part of the path
    //   '<tmpdir>\wpa-mcp-install-XXXXXX.ps1\<random>.tmp'.
    //
    // Cause: install.sh did `TMP=$(mktemp ...); ...; TMP="$TMP.ps1"`.  TMP is
    // already an exported env var on MSYS2, so the plain assignment updates
    // the exported value.  When the script then `exec`'d powershell.exe,
    // PowerShell inherited $env:TMP pointing at the temp script FILE instead
    // of the temp DIRECTORY.  dotnet-install.ps1 calls .NET's
    // Path.GetTempPath() (which prefers $TMP) and tried to write a sub-file
    // inside what it thought was a directory.
    //
    // The same hazard exists for TEMP and TMPDIR.  This check forbids
    // assignments to any of those names anywhere in install.sh.

    [Fact]
    public void InstallShellScriptDoesNotShadowTempEnvVars()
    {
        var path = LocateScript("install.sh");
        var lines = File.ReadAllLines(path);
        var offenders = new List<string>();
        var pattern = new Regex(@"^(?:export\s+)?(TMP|TEMP|TMPDIR)\s*=");

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#")) continue;
            if (pattern.IsMatch(trimmed))
                offenders.Add($"  line {i + 1}: {lines[i]}");
        }

        Assert.True(offenders.Count == 0,
            "install.sh assigns to TMP / TEMP / TMPDIR. On MSYS2/Git Bash these " +
            "are already-exported env vars, so the assignment updates the export " +
            "and exec'd powershell.exe inherits a broken $env:TMP. Inside " +
            "dotnet-install.ps1, .NET's Path.GetTempPath() then resolves to a " +
            "FILE path and the runtime download fails with " +
            "\"Could not find a part of the path\". Use a non-env variable " +
            "name (e.g. SCRIPT_FILE):\n" + string.Join("\n", offenders));
    }
}
