using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class MarkerSearchTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void FindMarker_DefaultMode_ReturnsCountsByEvent()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 5);
        Assert.Equal("count_by_event", resp.Mode);
        Assert.Null(resp.Rows);
        Assert.NotNull(resp.Counts);
        Assert.NotEmpty(resp.Counts!);
        Assert.True(resp.TotalMatched >= resp.Counts!.Sum(c => c.Count));
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.Equal(resp.TotalMatched, resp.MatchedEventCount);
        Assert.Null(resp.NoDataReason);
        // Counts must be ordered descending.
        for (var i = 1; i < resp.Counts!.Count; i++)
            Assert.True(resp.Counts[i - 1].Count >= resp.Counts[i].Count);
    }

    [Fact]
    public void FindMarker_RowsMode_ReturnsFullEventDetail()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 3, mode: "rows");
        Assert.Equal("rows", resp.Mode);
        Assert.Null(resp.Counts);
        Assert.NotNull(resp.Rows);
        Assert.True(resp.Rows!.Count <= 3);
        Assert.All(resp.Rows!, r => Assert.Contains("Sample", r.EventName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindMarker_CountByProcess_GroupsByProcess()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 10, mode: "count_by_process");
        Assert.Equal("count_by_process", resp.Mode);
        Assert.NotNull(resp.Counts);
    }

    [Fact]
    public void FindMarker_RowsMode_TruncatesLongFieldValues()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 5, mode: "rows", fieldMaxChars: 4);
        if (resp.Rows is { Count: > 0 })
        {
            foreach (var row in resp.Rows)
                foreach (var f in row.Fields.Values)
                    // 4-char limit + 1-char ellipsis sentinel = max length 5.
                    Assert.True(f.Length <= 5, $"field longer than expected: '{f}'");
        }
    }

    [Fact]
    public void FindMarker_RejectsEmptyQuery()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.FindMarker("nonexistent.etl", ""));
    }

    [Fact]
    public void FindMarker_RejectsUnknownMode()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.FindMarker(FixturePath, "Sample", mode: "bogus"));
    }

    [Fact]
    public void FindMarker_FieldMaxCharsEnforcesSharedBoundary()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));

        var accepted = tools.FindMarker(
            FixturePath,
            "no-such-marker-value",
            top: 1,
            mode: "rows",
            fieldMaxChars: Validation.MaxStringChars);

        Assert.Empty(accepted.Rows!);
        Assert.Equal("ok", accepted.ScopeStatus);
        Assert.Equal("not_observed", accepted.CapabilityStatus);
        Assert.Equal(0, accepted.MatchedEventCount);
        Assert.Equal("no_name_match", accepted.NoDataReason);
        Assert.Contains(accepted.Warnings!, warning =>
            warning.StartsWith("no_name_match:", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FindMarker(
            "missing-before-validation.etl",
            "marker",
            top: 1,
            mode: "rows",
            fieldMaxChars: Validation.MaxStringChars + 1));
    }
}
