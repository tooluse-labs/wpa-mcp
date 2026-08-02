namespace WpaMcp.Tests;

public class ReadmeContractTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpaMcp.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(
            LocateRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void ReadmesKeepTheSameUserJourney()
    {
        AssertInOrder(ReadRepoFile("README.md"), new[]
        {
            "## What it does",
            "## Quick start",
            "## Update",
            "## Analysis workflow",
            "## Capture a useful trace",
            "## Understand results",
            "## Troubleshooting",
            "## Documentation",
            "## Build from source"
        });

        AssertInOrder(ReadRepoFile("README.zh-CN.md"), new[]
        {
            "## 能做什么",
            "## 快速开始",
            "## 更新",
            "## 分析流程",
            "## 采集有效的 trace",
            "## 理解结果",
            "## 故障排查",
            "## 文档",
            "## 从源码构建"
        });
    }

    [Fact]
    public void ReadmesUseStableReleaseReferencesInsteadOfHardCodedVersions()
    {
        foreach (var document in new[]
                 {
                     ReadRepoFile("README.md"),
                     ReadRepoFile("README.zh-CN.md")
                 })
        {
            Assert.DoesNotMatch(@"\bv?\d+\.\d+\.\d+\b", document);
            Assert.Contains(
                "https://github.com/tooluse-labs/wpa-mcp/releases/latest",
                document,
                StringComparison.Ordinal);
            Assert.Contains("wpa-mcp-win-x64.zip", document, StringComparison.Ordinal);
            Assert.Contains("wpa-mcp.exe update", document, StringComparison.Ordinal);
            Assert.Contains("response_too_large", document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadmesLinkToTheMaintainedDocumentationSet()
    {
        var root = LocateRepoRoot();
        var english = ReadRepoFile("README.md");
        var chinese = ReadRepoFile("README.zh-CN.md");

        AssertDocumentLinksExist(root, english, new[]
        {
            "docs/ARCHITECTURE.md",
            "docs/CLIENT_COMPATIBILITY.md",
            "docs/CAPABILITY_GAPS.md",
            "docs/SYMBOL_RECIPES.md",
            "docs/WPR_PROFILE.md",
            "docs/CASE_STUDIES.md",
            "docs/CONTRACT_MIGRATION.md"
        });

        AssertDocumentLinksExist(root, chinese, new[]
        {
            "docs/ARCHITECTURE.md",
            "docs/CLIENT_COMPATIBILITY.zh-CN.md",
            "docs/CAPABILITY_GAPS.zh-CN.md",
            "docs/SYMBOL_RECIPES.zh-CN.md",
            "docs/WPR_PROFILE.md",
            "docs/CASE_STUDIES.md",
            "docs/CONTRACT_MIGRATION.zh-CN.md"
        });
    }

    private static void AssertInOrder(string document, IEnumerable<string> markers)
    {
        var offset = 0;
        foreach (var marker in markers)
        {
            var index = document.IndexOf(marker, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected README marker '{marker}' after offset {offset}.");
            offset = index + marker.Length;
        }
    }

    private static void AssertDocumentLinksExist(
        string root,
        string document,
        IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            Assert.Contains($"({relativePath})", document, StringComparison.Ordinal);
            Assert.True(
                File.Exists(Path.Combine(
                    root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar))),
                $"README link target does not exist: {relativePath}");
        }
    }
}
