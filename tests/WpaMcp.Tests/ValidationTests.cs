using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void RequirePidTid_RejectsTidWithoutPid()
    {
        Assert.Throws<ArgumentException>(() => Validation.RequirePidTid(pid: null, tid: 42));
        Validation.RequirePidTid(pid: 123, tid: 42);
    }

    [Theory]
    [InlineData(null, null, 10L, null, null)]
    [InlineData(7, null, null, 10L, null)]
    [InlineData(7, 8, -1L, null, null)]
    [InlineData(7, 8, null, -1L, null)]
    [InlineData(null, null, null, null, 1L)]
    [InlineData(7, null, null, null, 1L)]
    [InlineData(7, 8, null, null, 0L)]
    public void RequireThreadSelector_RejectsInvalidShapeBeforeTraceAccess(
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        long? threadGeneration) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            Validation.RequireThreadSelector(
                pid, tid, processStartUs, threadStartUs, threadGeneration));

    [Fact]
    public void RequireThreadSelector_AcceptsPositiveGenerationWithPidAndTid()
    {
        Validation.RequireThreadSelector(
            pid: 7,
            tid: 8,
            processStartUs: null,
            threadStartUs: null,
            threadGeneration: 2);
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void RequireText_EnforcesCharacterCeiling(int length, bool accepted)
    {
        var value = new string('x', length);
        var action = () => Validation.RequireText(value, allowEmpty: true);
        if (accepted)
        {
            action();
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(action);
        }
    }

    [Fact]
    public void RequireCollectionCount_RejectsMoreThan128()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Validation.RequireCollectionCount(129));
    }
}
