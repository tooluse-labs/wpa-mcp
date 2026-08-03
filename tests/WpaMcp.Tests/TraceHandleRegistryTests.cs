using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public sealed class TraceHandleRegistryTests
{
    private const string CpuFixture = "fixtures/small_cpu.etl";
    private const string FileIoFixture = "fixtures/small_fileio.etl";

    [Fact]
    public void TraceIdGrammar_IsCanonicalLowerHexAndReservedCaseInsensitively()
    {
        var generator = new CryptographicTraceIdGenerator();
        var tokens = Enumerable.Range(0, 128)
            .Select(_ => generator.CreateTraceId())
            .ToArray();

        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token => Assert.True(TraceId.IsCanonical(token)));
        Assert.True(TraceId.HasReservedPrefix("TRC_bad"));
        Assert.True(TraceId.HasReservedPrefix("TrC_0123.etl"));
        Assert.False(TraceId.IsCanonical("TRC_0123456789abcdef0123456789abcdef"));
        Assert.False(TraceId.IsCanonical("trc_0123456789abcdef0123456789abcdeg"));
        Assert.False(TraceId.HasReservedPrefix("trace.etl"));
    }

    [Fact]
    public async Task ConcurrentCanonicalLoad_UsesOneIdOneBackendAndPrincipalIsolation()
    {
        var openCount = 0;
        using var openerEntered = new ManualResetEventSlim();
        using var allowOpen = new ManualResetEventSlim();
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path =>
            {
                Interlocked.Increment(ref openCount);
                openerEntered.Set();
                Assert.True(allowOpen.Wait(TimeSpan.FromSeconds(30)));
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());
        using var registry = new TraceHandleRegistry(cache);

        var loads = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => registry.Load("alice", CpuFixture)))
            .ToArray();
        Assert.True(openerEntered.Wait(TimeSpan.FromSeconds(30)));
        allowOpen.Set();
        var results = await Task.WhenAll(loads);

        Assert.Single(results.Select(result => result.TraceId).Distinct());
        Assert.Equal(1, Volatile.Read(ref openCount));
        Assert.Single(results.Where(result => !result.ReusedExisting));
        Assert.Equal(1, registry.GetPrincipalStatus("alice").ActivePersistentHandles);

        using var aliceLease = registry.Acquire("alice", results[0].TraceId);
        var crossPrincipal = Assert.Throws<TraceReferenceException>(
            () => registry.Acquire("bob", results[0].TraceId));
        Assert.Equal("trace_not_loaded", crossPrincipal.Code);
        Assert.Equal(TraceHandleLookupStatus.Unknown, crossPrincipal.Status);
        Assert.Equal("unknown", crossPrincipal.DetailCode);
        var randomUnknown = Assert.Throws<TraceReferenceException>(
            () => registry.Acquire("bob", Id('e')));
        Assert.Equal(crossPrincipal.Code, randomUnknown.Code);
        Assert.Equal(crossPrincipal.Status, randomUnknown.Status);
        Assert.Equal(crossPrincipal.DetailCode, randomUnknown.DetailCode);
        Assert.Equal(crossPrincipal.Message, randomUnknown.Message);
        Assert.DoesNotContain(results[0].TraceId, crossPrincipal.Message, StringComparison.Ordinal);

        var bob = registry.Load("bob", CpuFixture);
        Assert.NotEqual(results[0].TraceId, bob.TraceId);
        using var bobLease = registry.Acquire("bob", bob.TraceId);
        Assert.Equal(aliceLease.CacheGenerationSequence, bobLease.CacheGenerationSequence);
        Assert.Equal(1, Volatile.Read(ref openCount));
    }

    [Fact]
    public void ReplacementAndLruRetirement_NeverRebindExistingId()
    {
        using var files = TempTracePair.Create();
        var openCount = 0;
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path =>
            {
                Interlocked.Increment(ref openCount);
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose(),
            refreshStaleSidecars: true);
        using var registry = new TraceHandleRegistry(cache);

        var oldHandle = registry.Load("principal", files.TracePath);
        using var oldLease = registry.Acquire("principal", oldHandle.TraceId);
        var oldGeneration = oldLease.CacheGenerationSequence;
        Assert.False(oldLease.Capabilities.HasFileIo);

        var originalWrite = File.GetLastWriteTimeUtc(files.TracePath);
        File.Move(files.ReplacementPath, files.TracePath, overwrite: true);
        File.SetLastWriteTimeUtc(files.TracePath, originalWrite);

        var newHandle = registry.Load("principal", files.TracePath);
        using var newLease = registry.Acquire("principal", newHandle.TraceId);
        using var replayOldLease = registry.Acquire("principal", oldHandle.TraceId);

        Assert.NotEqual(oldHandle.TraceId, newHandle.TraceId);
        Assert.NotEqual(oldGeneration, newLease.CacheGenerationSequence);
        Assert.Equal(oldGeneration, replayOldLease.CacheGenerationSequence);
        Assert.True(newLease.Capabilities.HasFileIo);
        Assert.False(oldLease.Capabilities.HasFileIo);
        Assert.False(replayOldLease.Capabilities.HasFileIo);
        Assert.Equal(2, Volatile.Read(ref openCount));
    }

    [Fact]
    public void MetadataPreservingRewrite_IsTruthfullyBoundedAndForceRefreshMintsNewGeneration()
    {
        using var files = TempTracePair.Create(copyReplacement: false);
        var openCount = 0;
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path =>
            {
                Interlocked.Increment(ref openCount);
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());
        using var registry = new TraceHandleRegistry(cache);

        var firstStamp = TraceCache.FileStamp.Capture(files.TracePath);
        var first = registry.Load("principal", files.TracePath);
        using var firstLease = registry.Acquire("principal", first.TraceId);

        // Rewrite through the same file object, then restore every metadata field
        // used by the current generation detector. This documents the exact blind
        // spot and proves the explicit freshness escape hatch is effective.
        File.Copy(CpuFixture, files.TracePath, overwrite: true);
        File.SetCreationTimeUtc(files.TracePath, firstStamp.CreationTimeUtc);
        File.SetLastWriteTimeUtc(files.TracePath, firstStamp.LastWriteTimeUtc);
        var preservedStamp = TraceCache.FileStamp.Capture(files.TracePath);
        Assert.Equal(firstStamp, preservedStamp);

        var ordinaryReload = registry.Load("principal", files.TracePath);
        Assert.Equal(first.TraceId, ordinaryReload.TraceId);
        Assert.True(ordinaryReload.ReusedExisting);
        Assert.Equal(
            TraceSourceGenerationAssurance.FileIdentityLengthAndTimestamps,
            ordinaryReload.SourceGenerationAssurance);

        var forced = registry.Load("principal", files.TracePath, forceRefresh: true);
        using var forcedLease = registry.Acquire("principal", forced.TraceId);
        Assert.NotEqual(first.TraceId, forced.TraceId);
        Assert.NotEqual(firstLease.CacheGenerationSequence, forcedLease.CacheGenerationSequence);
        Assert.True(forced.ForceRefreshApplied);
        Assert.Equal(2, Volatile.Read(ref openCount));
        Assert.True(firstLease.Trace.EventCount > 0);
    }

    [Fact]
    public async Task Unload_BlocksNewLeasesAndDrainsExistingLeaseAfterLruRetirement()
    {
        var disposeCount = 0;
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path => TraceLog.OpenOrConvert(path),
            disposeTrace: trace =>
            {
                Interlocked.Increment(ref disposeCount);
                trace.Dispose();
            });
        using var registry = new TraceHandleRegistry(cache);
        var loaded = registry.Load("principal", CpuFixture);
        var query = registry.Acquire("principal", loaded.TraceId);
        _ = query.Trace;

        Assert.True(cache.Unload(CpuFixture));
        var unload = registry.Unload("principal", loaded.TraceId);
        Assert.Equal(TraceHandleUnloadStatus.Unloaded, unload.Status);
        Assert.Equal(1, unload.ActiveLeases);
        Assert.False(unload.DrainTask.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref disposeCount));

        var unavailable = Assert.Throws<TraceReferenceException>(
            () => registry.Acquire("principal", loaded.TraceId));
        Assert.Equal("trace_not_loaded", unavailable.Code);
        Assert.Equal(TraceHandleLookupStatus.Unloaded, unavailable.Status);
        Assert.Equal("unloaded", unavailable.DetailCode);
        var repeated = registry.Unload("principal", loaded.TraceId);
        Assert.Equal(TraceHandleUnloadStatus.AlreadyUnloaded, repeated.Status);
        Assert.Equal(1, repeated.ActiveLeases);
        Assert.Same(unload.DrainTask, repeated.DrainTask);

        query.Dispose();
        await unload.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref disposeCount));
    }

    [Fact]
    public void ExpiredHandlesAreRetiredBeforeAdmissionAndAbsoluteExpiryDoesNotBreakLiveLease()
    {
        using var files = TempTracePair.Create();
        var time = new ManualTimeProvider();
        var options = Options(
            maxHandles: 1,
            idle: TimeSpan.FromSeconds(10),
            absolute: TimeSpan.FromSeconds(30));
        using var cache = new TraceCache(capacity: 2);
        using var registry = new TraceHandleRegistry(cache, options, time);

        var first = registry.Load("principal", files.TracePath);
        time.Advance(TimeSpan.FromSeconds(10));
        var second = registry.Load("principal", files.ReplacementPath);

        Assert.NotEqual(first.TraceId, second.TraceId);
        Assert.Equal(TraceHandleLookupStatus.Expired,
            registry.GetLookupStatus("principal", first.TraceId));
        var expired = Assert.Throws<TraceReferenceException>(
            () => registry.Acquire("principal", first.TraceId));
        Assert.Equal("trace_not_loaded", expired.Code);
        Assert.Equal("expired", expired.DetailCode);

        using var live = registry.Acquire("principal", second.TraceId);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, registry.SweepExpired());
        Assert.True(live.Trace.EventCount > 0);
        Assert.Equal(TraceHandleLookupStatus.Expired,
            registry.GetLookupStatus("principal", second.TraceId));
    }

    [Fact]
    public void IdleLifetimeStartsAtLastLeaseReleaseAndNeverExpiresAnActiveLease()
    {
        var time = new ManualTimeProvider();
        using var cache = new TraceCache(capacity: 1);
        using var registry = new TraceHandleRegistry(
            cache,
            Options(
                idle: TimeSpan.FromSeconds(10),
                absolute: TimeSpan.FromMinutes(1)),
            time);
        var loaded = registry.Load("principal", CpuFixture);
        var lease = registry.Acquire("principal", loaded.TraceId);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, registry.SweepExpired());
        lease.Dispose();
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, registry.SweepExpired());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, registry.SweepExpired());
        Assert.Equal(TraceHandleLookupStatus.Expired,
            registry.GetLookupStatus("principal", loaded.TraceId));
    }

    [Fact]
    public void PerPrincipalCountRateAndTombstoneQuotasAreBounded()
    {
        using var files = TempTracePair.Create();
        var time = new ManualTimeProvider();
        var countOptions = Options(maxHandles: 1, maxCreations: 10, maxTombstones: 1);
        using (var cache = new TraceCache(capacity: 2))
        using (var registry = new TraceHandleRegistry(cache, countOptions, time))
        {
            _ = registry.Load("alice", files.TracePath);
            var quota = Assert.Throws<TraceReferenceException>(
                () => registry.Load("alice", files.ReplacementPath));
            Assert.Equal("budget_exceeded", quota.Code);
            Assert.Equal("trace_handle_quota_exceeded", quota.DetailCode);

            // Quotas and token namespaces are principal-scoped.
            _ = registry.Load("bob", files.ReplacementPath);
        }

        var ids = new SequenceTraceIdGenerator(
            Id('a'), Id('b'), Id('c'));
        var rateOptions = Options(
            maxHandles: 2,
            maxCreations: 1,
            rateWindow: TimeSpan.FromSeconds(10),
            maxTombstones: 1);
        using var rateCache = new TraceCache(capacity: 2);
        using var rateRegistry = new TraceHandleRegistry(rateCache, rateOptions, time, ids);
        var first = rateRegistry.Load("alice", files.TracePath);
        Assert.Equal(TraceHandleUnloadStatus.Unloaded,
            rateRegistry.Unload("alice", first.TraceId).Status);
        var rate = Assert.Throws<TraceReferenceException>(
            () => rateRegistry.Load("alice", files.ReplacementPath));
        Assert.Equal("budget_exceeded", rate.Code);
        Assert.Equal("trace_handle_rate_exceeded", rate.DetailCode);

        time.Advance(TimeSpan.FromSeconds(10));
        var second = rateRegistry.Load("alice", files.ReplacementPath);
        rateRegistry.Unload("alice", second.TraceId);
        Assert.Equal(TraceHandleLookupStatus.Unknown,
            rateRegistry.GetLookupStatus("alice", first.TraceId));
        Assert.Equal(TraceHandleLookupStatus.Unloaded,
            rateRegistry.GetLookupStatus("alice", second.TraceId));
        Assert.Equal(1, rateRegistry.GetPrincipalStatus("alice").Tombstones);
    }

    [Fact]
    public void TokenCollisionRetriesArePrincipalScoped()
    {
        using var files = TempTracePair.Create();
        var collisionIds = new SequenceTraceIdGenerator(Id('a'), Id('a'), Id('b'));
        using (var cache = new TraceCache(capacity: 2))
        using (var registry = new TraceHandleRegistry(
                   cache,
                   Options(maxHandles: 2),
                   traceIds: collisionIds))
        {
            var first = registry.Load("principal", files.TracePath);
            var second = registry.Load("principal", files.ReplacementPath);
            Assert.Equal(Id('a'), first.TraceId);
            Assert.Equal(Id('b'), second.TraceId);
        }

        var isolatedIds = new SequenceTraceIdGenerator(Id('c'), Id('c'));
        using var isolatedCache = new TraceCache(capacity: 1);
        using var isolatedRegistry = new TraceHandleRegistry(
            isolatedCache,
            Options(),
            traceIds: isolatedIds);
        var alice = isolatedRegistry.Load("alice", files.TracePath);
        var bob = isolatedRegistry.Load("bob", files.TracePath);
        Assert.Equal(alice.TraceId, bob.TraceId);
        using var aliceLease = isolatedRegistry.Acquire("alice", alice.TraceId);
        using var bobLease = isolatedRegistry.Acquire("bob", bob.TraceId);
        Assert.Equal(aliceLease.CacheGenerationSequence, bobLease.CacheGenerationSequence);
    }

    [Fact]
    public void Resolver_AcceptsOnlyCanonicalTraceIdsAndNeverOpensRawPaths()
    {
        var openCount = 0;
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path =>
            {
                Interlocked.Increment(ref openCount);
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());
        using var registry = new TraceHandleRegistry(cache);
        var resolver = new TraceReferenceResolver(registry);

        foreach (var malformed in new[]
                 {
                     "trc_",
                     "TRC_bad",
                     "trc_0123.etl",
                     "TrC_0123456789abcdef0123456789abcdeg",
                 })
        {
            var invalid = Assert.Throws<TraceReferenceException>(() =>
                resolver.ResolveQuery("principal", malformed, TraceAccessMode.IdOnly));
            Assert.Equal("invalid_argument", invalid.Code);
            Assert.Equal("malformed_trace_id", invalid.DetailCode);
        }
        Assert.Equal(0, Volatile.Read(ref openCount));

        var unknown = Assert.Throws<TraceReferenceException>(() =>
            resolver.ResolveQuery("principal", Id('d'), TraceAccessMode.IdOnly));
        Assert.Equal("trace_not_loaded", unknown.Code);
        Assert.Equal(TraceHandleLookupStatus.Unknown, unknown.Status);
        Assert.Equal(0, Volatile.Read(ref openCount));

        var idOnly = Assert.Throws<TraceReferenceException>(() =>
            resolver.ResolveQuery("principal", CpuFixture, TraceAccessMode.IdOnly));
        Assert.Equal("invalid_argument", idOnly.Code);
        Assert.Equal("raw_path_not_allowed", idOnly.DetailCode);
        Assert.Equal(0, Volatile.Read(ref openCount));

        Assert.Equal(0, Volatile.Read(ref openCount));
    }

    [Fact]
    public async Task FailureAndCancellationPublishNoRegistryHandle()
    {
        var attempts = 0;
        using (var cache = new TraceCache(
                   capacity: 1,
                   openTrace: path =>
                   {
                       if (Interlocked.Increment(ref attempts) == 1)
                           throw new IOException("injected open failure");
                       return TraceLog.OpenOrConvert(path);
                   },
                   disposeTrace: trace => trace.Dispose()))
        using (var registry = new TraceHandleRegistry(cache))
        {
            Assert.Throws<IOException>(() => registry.Load("principal", CpuFixture));
            Assert.Equal(0, registry.GetPrincipalStatus("principal").ActivePersistentHandles);
            Assert.False(registry.Load("principal", CpuFixture).ReusedExisting);
            Assert.Equal(2, Volatile.Read(ref attempts));
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancelledCache = new TraceCache(
            capacity: 1,
            openTrace: path =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());
        using var cancelledRegistry = new TraceHandleRegistry(cancelledCache);
        using var cancellation = new CancellationTokenSource();
        var load = Task.Run(() => cancelledRegistry.Load(
            "principal",
            CpuFixture,
            cancellationToken: cancellation.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        cancellation.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(0,
            cancelledRegistry.GetPrincipalStatus("principal").ActivePersistentHandles);
    }

    [Fact]
    public async Task ConcurrentLeaseDispose_ReleasesRegistryAndBackendExactlyOnce()
    {
        var disposeCount = 0;
        using var cache = new TraceCache(
            capacity: 1,
            openTrace: path => TraceLog.OpenOrConvert(path),
            disposeTrace: trace =>
            {
                Interlocked.Increment(ref disposeCount);
                trace.Dispose();
            });
        using var registry = new TraceHandleRegistry(cache);
        var loaded = registry.Load("principal", CpuFixture);
        var lease = registry.Acquire("principal", loaded.TraceId);
        _ = lease.Trace;
        Assert.True(cache.Unload(CpuFixture));
        var unload = registry.Unload("principal", loaded.TraceId);

        await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(lease.Dispose)));
        await unload.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Volatile.Read(ref disposeCount));
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Trace);
    }

    [Fact]
    public void RegistryContracts_DoNotExposePathsFileStampsOrGenerationKeys()
    {
        Assert.False(typeof(TraceCache.FileStamp).IsPublic);
        Assert.Null(typeof(TraceLease).GetProperty(
            "GenerationIdentity",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public));

        foreach (var type in new[]
                 {
                     typeof(TraceHandleLoadResult),
                     typeof(TraceHandleUnloadResult),
                     typeof(TracePrincipalRegistryStatus),
                     typeof(TraceReferenceDescriptor),
                 })
        {
            var names = type.GetProperties()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Path", names);
            Assert.DoesNotContain("FileStamp", names);
            Assert.DoesNotContain("GenerationKey", names);
            Assert.DoesNotContain("ArtifactKey", names);
        }
    }

    private static TraceHandleRegistryOptions Options(
        int maxHandles = 8,
        int maxCreations = 32,
        TimeSpan? rateWindow = null,
        TimeSpan? idle = null,
        TimeSpan? absolute = null,
        int maxTombstones = 64) =>
        new(
            maxHandles,
            maxCreations,
            rateWindow ?? TimeSpan.FromMinutes(1),
            idle ?? TimeSpan.FromMinutes(30),
            absolute ?? TimeSpan.FromHours(8),
            maxTombstones,
            TimeSpan.FromHours(8));

    private static string Id(char digit) => $"trc_{new string(digit, 32)}";

    private sealed class SequenceTraceIdGenerator(params string[] ids) : ITraceIdGenerator
    {
        private readonly Queue<string> _ids = new(ids);

        public string CreateTraceId() =>
            _ids.Count != 0 ? _ids.Dequeue() : Id('f');
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _timestamp));

        internal void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class TempTracePair : IDisposable
    {
        private TempTracePair(string root, string tracePath, string replacementPath)
        {
            Root = root;
            TracePath = tracePath;
            ReplacementPath = replacementPath;
        }

        internal string Root { get; }
        internal string TracePath { get; }
        internal string ReplacementPath { get; }

        internal static TempTracePair Create(bool copyReplacement = true)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"wpa-mcp-trace-registry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var trace = Path.Combine(root, "trace.etl");
            var replacement = Path.Combine(root, "replacement.etl");
            File.Copy(CpuFixture, trace);
            if (copyReplacement)
                File.Copy(FileIoFixture, replacement);
            return new TempTracePair(root, trace, replacement);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best effort after an assertion or native trace cleanup failure.
            }
        }
    }
}
