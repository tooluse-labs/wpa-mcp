using System.Xml.Linq;

namespace WpaMcp.Tests;

public class WprProfileRecipeTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpaMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string LocateRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { LocateRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Expected file at {path}");
        return path;
    }

    [Fact]
    public void JitOnlyProfileCapturesClrJitWithoutBroadClrRuntimeKeywords()
    {
        var path = LocateRepoFile("tests", "WpaMcp.Tests", "fixtures", "JitOnlyCapture.wprp");
        var doc = XDocument.Load(path);

        var profile = Assert.Single(doc.Descendants("Profile"));
        Assert.Equal("ClrJitOnly.Verbose.File", profile.Attribute("Id")?.Value);
        Assert.Equal("ClrJitOnly", profile.Attribute("Name")?.Value);

        var runtimeProvider = Assert.Single(doc.Descendants("EventProvider"));
        Assert.Equal("ClrRuntimeJitProvider", runtimeProvider.Attribute("Id")?.Value);
        Assert.Equal("Microsoft-Windows-DotNETRuntime", runtimeProvider.Attribute("Name")?.Value);
        Assert.Equal("5", runtimeProvider.Attribute("Level")?.Value);
        Assert.Equal("true", runtimeProvider.Attribute("NonPagedMemory")?.Value);
        Assert.Null(runtimeProvider.Attribute("Keywords"));
        Assert.Equal("0x18", Assert.Single(runtimeProvider
            .Element("Keywords")!
            .Elements("Keyword"))
            .Attribute("Value")?.Value);

        var profileElementOrder = doc.Root!
            .Element("Profiles")!
            .Elements()
            .Select(e => e.Name.LocalName)
            .ToArray();
        Assert.Equal(new[]
        {
            "SystemCollector",
            "EventCollector",
            "SystemProvider",
            "EventProvider",
            "Profile"
        }, profileElementOrder);

        var systemKeywords = doc.Descendants("SystemProvider")
            .Descendants("Keyword")
            .Select(k => k.Attribute("Value")?.Value)
            .ToArray();
        Assert.Equal(new[] { "ProcessThread", "Loader" }, systemKeywords);

        Assert.Contains(doc.Descendants("EventProviderId"),
            p => p.Attribute("Value")?.Value == "ClrRuntimeJitProvider");
    }

    [Fact]
    public void DocsReferenceTheJitOnlyCaptureRecipe()
    {
        var wprProfile = File.ReadAllText(LocateRepoFile("docs", "WPR_PROFILE.md"));
        var capture = File.ReadAllText(LocateRepoFile("tests", "WpaMcp.Tests", "fixtures", "CAPTURE.md"));
        var readme = File.ReadAllText(LocateRepoFile("README.md"));
        var readmeZh = File.ReadAllText(LocateRepoFile("README.zh-CN.md"));

        Assert.Contains("JitOnlyCapture.wprp", wprProfile);
        Assert.Contains("Capture-JitOnly.ps1", wprProfile);
        Assert.Contains("ClrJitOnly", wprProfile);
        Assert.Contains("JitOnlyCapture.wprp", capture);
        Assert.Contains("Capture-JitOnly.ps1", capture);
        Assert.Contains("JitOnlyCapture.wprp", readme);
        Assert.Contains("Capture-JitOnly.ps1", readme);
        Assert.Contains("JitOnlyCapture.wprp", readmeZh);
        Assert.Contains("Capture-JitOnly.ps1", readmeZh);
    }

    [Fact]
    public void JitOnlyCaptureScriptUsesTheJitProfileAndMarkerValidation()
    {
        var script = File.ReadAllText(LocateRepoFile(
            "tests", "WpaMcp.Tests", "fixtures", "Capture-JitOnly.ps1"));

        Assert.Contains("JitOnlyCapture.wprp", script);
        Assert.Contains("ClrJitOnly", script);
        Assert.Contains("JittingStarted", script);
        Assert.Contains("LoadVerbose", script);
        Assert.Contains("dotnet build", script);
        Assert.Contains("src\\WpaMcp\\bin\\Release\\net10.0\\WpaMcp.dll", script);
        Assert.DoesNotContain("Add-Type", script);
        Assert.DoesNotContain("New-Object System.Text.StringBuilder", script);
        Assert.Contains("Microsoft-Windows-DotNETRuntime", File.ReadAllText(LocateRepoFile(
            "tests", "WpaMcp.Tests", "fixtures", "JitOnlyCapture.wprp")));
    }

    [Fact]
    public void FixtureRefreshScriptUsesTheCurrentNet10ServerDll()
    {
        var script = File.ReadAllText(LocateRepoFile(
            "tests", "WpaMcp.Tests", "fixtures", "Refresh-SmallMemoryFixture.ps1"));

        Assert.Contains("src\\WpaMcp\\bin\\Release\\net10.0\\WpaMcp.dll", script);
    }
}
