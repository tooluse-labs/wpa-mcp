using System.Text.Json;

namespace WpaMcp.Tests;

public sealed class ObservedContractDefectLedgerTests
{
    [Fact]
    public void WireSchemaDefect_IsTrackedOutsideAnalyzerCorrectnessLedger()
    {
        var repoRoot = LocateRepoRoot();
        var ledgerPath = Path.Combine(
            repoRoot,
            "eng",
            "contract-baselines",
            "observed-contract-defects.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(ledgerPath));
        var root = document.RootElement;

        Assert.Equal("observed-contract-defects.v1", root.GetProperty("formatVersion").GetString());
        var defect = Assert.Single(root.GetProperty("defects").EnumerateArray());
        Assert.Equal("WIRE-SCHEMA-001", defect.GetProperty("id").GetString());
        Assert.Equal("P0", defect.GetProperty("severity").GetString());
        Assert.Equal("known_incorrect_must_change", defect.GetProperty("disposition").GetString());
        Assert.Equal(
            286,
            defect.GetProperty("recursiveProductionStdioAudit")
                .GetProperty("legacyMissingRequiredPathOccurrences")
                .GetInt32());
        Assert.Equal(
            "LEGACY-STDIO-SCHEMA-001",
            defect.GetProperty("legacyEvidence").GetProperty("legacyDefectId").GetString());
        Assert.Equal(
            "tests/WpaMcp.Tests/ContractBaselines/active-structured-stdio.v1.json",
            defect.GetProperty("repair").GetProperty("activeGolden").GetString());
        Assert.Equal(
            "fixed_runtime_and_recursive_tests_verified_pending_active_golden_refresh",
            defect.GetProperty("implementationStatus").GetString());

        var activeGoldenPath = Path.Combine(
            repoRoot,
            defect.GetProperty("repair").GetProperty("activeGolden").GetString()!
                .Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(activeGoldenPath), activeGoldenPath);

        var analyzerLedger = File.ReadAllText(Path.Combine(
            repoRoot,
            "eng",
            "contract-baselines",
            "correctness-disposition.v1.json"));
        Assert.DoesNotContain("WIRE-SCHEMA-001", analyzerLedger, StringComparison.Ordinal);
    }

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
