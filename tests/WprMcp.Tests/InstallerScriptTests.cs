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

    private static string LocateRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { LocateRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Expected file at {path}");
        return path;
    }

    // -- scripts/install.ps1 -----------------------------------------------
    // The documented one-liner
    //   iex "& { $(irm $URL) } -Tag v0.2.8"
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
            "the documented `iex \"& { $(irm $URL) } -Tag v0.2.8\"` " +
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

    [Fact]
    public void InstallScriptDownloadsSelfContainedExeAndDoesNotRunSetup()
    {
        var content = File.ReadAllText(LocateScript("install.ps1"));

        Assert.Contains("wpa-mcp-win-x64.exe", content);
        Assert.Contains("wpa-mcp.exe", content);
        Assert.Contains("claude mcp add $ServerName --scope $ClaudeScope -- $BinaryPath @serverArgs", content);
        Assert.Contains("command = $commandToml", content);
        Assert.Contains("args = [$argsToml]", content);
        Assert.DoesNotContain("Expand-Archive", content);
        Assert.DoesNotContain("setup.ps1", content);
        Assert.DoesNotContain("dotnet-install.ps1", content);
        Assert.DoesNotContain("wpa-mcp-$Tag.zip", content);
    }

    [Fact]
    public void InstallScriptUsesClaudeAddSyntaxThatDoesNotSwallowServerArgs()
    {
        var content = File.ReadAllText(LocateScript("install.ps1"));

        Assert.Contains("claude mcp add $ServerName --scope $ClaudeScope -- $BinaryPath @serverArgs", content);
        Assert.DoesNotContain("claude mcp add --scope $ClaudeScope $ServerName -- $BinaryPath @serverArgs", content);
        Assert.DoesNotContain("claude mcp add $ServerName --scope $ClaudeScope $BinaryPath @serverArgs", content);
    }

    [Fact]
    public void InstallScriptSkipsDownloadWhenInstalledExeMatchesReleaseAsset()
    {
        var content = File.ReadAllText(LocateScript("install.ps1"));

        Assert.Contains("function Test-UsableBinary", content);
        Assert.Contains("function Test-InstalledBinaryMatchesRelease", content);
        Assert.Contains("Find-ReleaseAsset -Release $release -AssetName $assetName", content);
        Assert.Contains("Get-FileHash -Algorithm SHA256 -LiteralPath $BinaryPath", content);
        Assert.DoesNotContain("$ReleaseAsset.size", content);
        Assert.Contains("if (-not $force -and (Test-InstalledBinaryMatchesRelease -BinaryPath $binaryPath -ReleaseAsset $releaseAsset))", content);
        Assert.Contains("Write-Ok \"Using existing complete $binaryPath\"", content);
        Assert.Contains("[switch]$ForceDownload", content);
        Assert.Contains("Test-TruthyEnv $env:WPA_MCP_FORCE_DOWNLOAD", content);
    }

    [Fact]
    public void InstallScriptRenamesExistingExeBeforeMove()
    {
        var content = File.ReadAllText(LocateScript("install.ps1"));

        Assert.Contains("Get-ChildItem -LiteralPath $destDir -Filter \"$destName.old-*\" -Name", content);
        Assert.Contains("Move-Item -LiteralPath $Destination -Destination $aside -Force", content);
        Assert.Contains("Move-Item -LiteralPath $Source -Destination $Destination -Force", content);
        Assert.DoesNotContain("Move-Item -Path $Source -Destination $Destination -Force", content);
    }

    [Fact]
    public void InstallScriptDoesNotUseScriptScopeForResolvedTagOrInstallDir()
    {
        var content = File.ReadAllText(LocateScript("install.ps1"));

        Assert.DoesNotContain("$script:", content);
        Assert.Contains("$resolvedTag", content);
        Assert.Contains("$resolvedInstallDir", content);
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

    // After the splat fix, the bootstrap began *succeeding*, but setup.ps1 still
    // threw with the empty error
    //   dotnet-install.ps1 failed (exit ).
    // because the post-call guard read $LASTEXITCODE.  $LASTEXITCODE is only set by
    // native (.exe) commands; .ps1 invocations leave it untouched, so on a fresh
    // shell it stayed $null -- and `$null -ne 0` is $true.  The check fired on every
    // successful bootstrap.  .ps1 calls signal failure via terminating errors, so the
    // bootstrap is now wrapped in try/catch and the spurious LASTEXITCODE guard is
    // gone.  Native commands later in setup.ps1 (claude.exe, dotnet build) DO set
    // LASTEXITCODE legitimately, so the regex below scopes its negative match to a
    // narrow window immediately after the bootstrap invocation.

    [Fact]
    public void SetupScriptDoesNotCheckLastExitCodeAfterDotNetBootstrap()
    {
        var content = File.ReadAllText(LocateScript("setup.ps1"));

        Assert.DoesNotMatch(
            @"&\s*\$bootstrapPath\s*@bootstrapArgs[\s\S]{0,200}\$LASTEXITCODE",
            content);
    }

    // PowerShell sessions running under AppLocker / WDAC / Device Guard policy are
    // forced into Constrained Language Mode, which blocks all .NET method invocations.
    // Microsoft's dotnet-install.ps1 calls .NET methods directly on every code path,
    // so setup.ps1 must avoid that bootstrap in CLM. Native winget still works in
    // managed environments, so setup.ps1 should try that before giving up.

    [Fact]
    public void SetupScriptUsesWingetFallbackInConstrainedLanguageMode()
    {
        var content = File.ReadAllText(LocateScript("setup.ps1"));

        Assert.Contains("function Install-DotNetWithWinget", content);
        Assert.Matches(
            @"(?i)\$languageMode\s*=\s*\$ExecutionContext\.SessionState\.LanguageMode[\s\S]{0,120}if\s*\(\$languageMode\s+-ne\s+'FullLanguage'\)",
            content);
        Assert.Matches(
            @"(?i)if\s*\(\$languageMode\s+-ne\s+'FullLanguage'\)\s*\{[\s\S]{0,800}Install-DotNetWithWinget",
            content);
        Assert.Matches(
            @"(?i)if\s*\(\$languageMode\s+-ne\s+'FullLanguage'\)\s*\{[\s\S]{0,1800}throw",
            content);
    }

    [Fact]
    public void SetupScriptDoesNotInterpolatePlainVariablesBeforeColon()
    {
        var lines = File.ReadAllLines(LocateScript("setup.ps1"));
        var offenders = new List<string>();
        var pattern = new Regex(@"\$(?<name>[A-Za-z_][A-Za-z0-9_]*):");
        var allowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "env", "global", "local", "private", "script", "using"
        };

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match match in pattern.Matches(lines[i]))
            {
                if (allowedScopes.Contains(match.Groups["name"].Value)) continue;
                offenders.Add($"  line {i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "PowerShell parses `$name:` inside a double-quoted string as a scoped " +
            "variable reference, not `$name` followed by a colon. Use `${name}:` " +
            "when interpolation is intended:\n" + string.Join("\n", offenders));
    }

    // Claude Code 2.1.129 documents `claude mcp add -e KEY=value name -- command`,
    // but its variadic -e parser consumes the following server name as another env
    // value and fails with "Invalid environment variable format: wpa-mcp".  Register
    // direct command+args instead: dotnet, DLL path, then server options.

    [Fact]
    public void SetupScriptUsesClaudeAddWithDirectServerArgs()
    {
        var content = File.ReadAllText(LocateScript("setup.ps1"));

        Assert.DoesNotMatch(@"-e\s+_NT_SYMBOL_PATH", content);
        Assert.DoesNotContain("$env:_NT_SYMBOL_PATH", content);
        Assert.Contains("$serverArgs = @($dllPath, '--symbol-path', $SymbolPath, '--cache-size', \"$CacheSize\")", content);
        Assert.Contains("claude mcp add $ServerName --scope user -- $dotnetCommand @serverArgs", content);
        Assert.Contains("args = [$argsToml]", content);
        Assert.DoesNotContain("[mcp_servers.$ServerName.env]", content);
    }

    [Fact]
    public void ReleaseWorkflowPublishesSelfContainedWindowsExecutable()
    {
        var content = File.ReadAllText(LocateRepoFile(".github", "workflows", "release.yml"));

        Assert.Contains("-r win-x64", content);
        Assert.Contains("--self-contained true", content);
        Assert.Contains("PublishSingleFile=true", content);
        Assert.Contains("wpa-mcp-win-x64.exe", content);
        Assert.DoesNotContain("actions/upload-artifact", content);
        Assert.DoesNotContain("Compress-Archive", content);
        Assert.DoesNotContain("wpa-mcp-*.zip", content);
    }

    [Fact]
    public void UninstallScriptRemovesClaudeDesktopEntryWithoutPsObjectRemove()
    {
        var content = File.ReadAllText(LocateScript("uninstall.ps1"));

        Assert.DoesNotContain(".PSObject.Properties.Remove(", content);
        Assert.Contains("New-Object PSObject -Property @{}", content);
        Assert.Contains("$config.mcpServers = $newServers", content);
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
    public void ShellScriptsDoNotShadowTempEnvVars()
    {
        var offenders = new List<string>();
        var pattern = new Regex(@"^(?:export\s+)?(TMP|TEMP|TMPDIR)\s*=");

        foreach (var scriptName in new[] { "install.sh", "uninstall.sh" })
        {
            var path = LocateScript(scriptName);
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("#")) continue;
                if (pattern.IsMatch(trimmed))
                    offenders.Add($"  {scriptName} line {i + 1}: {lines[i]}");
            }
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
