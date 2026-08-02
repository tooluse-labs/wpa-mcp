using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class TraceLifecycleProductionTests
{
    [Fact]
    public async Task LoadQueryUnloadReload_UsesOpaqueLifecycleAndRetainsArtifact()
    {
        using var runtime = TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");

        var loaded = runtime.Lifecycle.Load(runtime.Principal, source);
        Assert.Matches("^trc_[0-9a-f]{32}$", loaded.Handle.TraceId);
        Assert.StartsWith(runtime.ArtifactRoot, loaded.Artifact.TracePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.ChangeExtension(source, ".etlx")));

        long firstGeneration;
        using (var query = runtime.Registry.Acquire(runtime.Principal, loaded.Handle.TraceId))
        {
            Assert.True(query.Trace.EventCount > 0);
            firstGeneration = query.GetFacts(CancellationToken.None).GenerationSequence;
        }

        var unload = runtime.Registry.Unload(runtime.Principal, loaded.Handle.TraceId);
        await unload.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(loaded.Artifact.TracePath));

        var reloaded = runtime.Lifecycle.Load(runtime.Principal, source);
        Assert.NotEqual(loaded.Handle.TraceId, reloaded.Handle.TraceId);
        using (var query = runtime.Registry.Acquire(runtime.Principal, reloaded.Handle.TraceId))
        {
            Assert.NotEqual(
                firstGeneration,
                query.GetFacts(CancellationToken.None).GenerationSequence);
        }
        Assert.Equal(1, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);
        runtime.Registry.Unload(runtime.Principal, reloaded.Handle.TraceId);
    }

    [Fact]
    public async Task ConcurrentLoadAndForceRefresh_SingleFlightSnapshotConversionAndBackend()
    {
        var backendOpenCount = 0;
        using var runtime = TestRuntime.Create(
            openTrace: path =>
            {
                Interlocked.Increment(ref backendOpenCount);
                return Microsoft.Diagnostics.Tracing.Etlx.TraceLog.OpenOrConvert(path);
            });
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");

        var firstWave = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => runtime.Lifecycle.Load(runtime.Principal, source))));
        Assert.Single(firstWave.Select(result => result.Handle.TraceId).Distinct());
        Assert.Equal(1, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);
        Assert.Equal(1, Volatile.Read(ref backendOpenCount));

        using var barrier = new Barrier(8);
        var refreshWave = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                return runtime.Lifecycle.Load(
                    runtime.Principal,
                    source,
                    forceRefresh: true);
            })));
        Assert.Single(refreshWave.Select(result => result.Handle.TraceId).Distinct());
        Assert.Equal(2, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);
        Assert.Equal(1, Volatile.Read(ref backendOpenCount));
        Assert.Equal(0, runtime.Loader.InFlightCount);
        runtime.Registry.Unload(runtime.Principal, refreshWave[0].Handle.TraceId);
    }

    [Fact]
    public async Task SharedMaterialization_LeaderCancellationDoesNotCancelFollower()
    {
        using var conversionStarted = new ManualResetEventSlim();
        using var releaseConversion = new ManualResetEventSlim();
        using var runtime = TestRuntime.Create(
            convertTrace: path =>
            {
                conversionStarted.Set();
                Assert.True(releaseConversion.Wait(TimeSpan.FromSeconds(30)));
                return Microsoft.Diagnostics.Tracing.Etlx.TraceLog.OpenOrConvert(path);
            });
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        using var leaderCancellation = new CancellationTokenSource();

        var leader = runtime.Loader.LoadAsync(
            source,
            forceRefresh: false,
            leaderCancellation.Token);
        Assert.True(conversionStarted.Wait(TimeSpan.FromSeconds(30)));
        var follower = runtime.Loader.LoadAsync(source, forceRefresh: false);
        Assert.True(SpinWait.SpinUntil(
            () => runtime.Loader.InFlightWaiterCount == 2,
            TimeSpan.FromSeconds(30)));
        leaderCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        releaseConversion.Set();
        var loaded = await follower.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(loaded.Artifact.TracePath));
        Assert.Equal(
            TraceSourceValidationEvidence.OpenedHandleSnapshotContentHash,
            loaded.SourceValidation);
        Assert.Equal(1, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);
        Assert.Equal(0, runtime.Loader.InFlightCount);
    }

    [Fact]
    public async Task SharedMaterialization_AllWaitersCancel_CancelsOperationAndCleansFlight()
    {
        var snapshotStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = TestRuntime.Create(
            beforeSnapshot: cancellationToken =>
            {
                snapshotStarted.TrySetResult();
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            });
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = runtime.Loader.LoadAsync(
            source,
            forceRefresh: false,
            firstCancellation.Token);
        var second = runtime.Loader.LoadAsync(
            source,
            forceRefresh: false,
            secondCancellation.Token);
        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(SpinWait.SpinUntil(
            () => runtime.Loader.InFlightWaiterCount == 2,
            TimeSpan.FromSeconds(30)));

        firstCancellation.Cancel();
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(SpinWait.SpinUntil(
            () => runtime.Loader.InFlightCount == 0,
            TimeSpan.FromSeconds(30)));
        Assert.Equal(0, runtime.Store.SnapshotCopyCount);
        Assert.Equal(0, runtime.Store.ConversionCount);
    }

    [Fact]
    public void MaterializationStart_ScavengesStaleTemporaryOperations()
    {
        using var runtime = TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var first = runtime.Lifecycle.Load(runtime.Principal, source);
        var stale = Path.Combine(runtime.ArtifactRoot, "tmp", "stale-crashed-operation");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "partial.etlx"), "partial");

        _ = runtime.Lifecycle.Load(
            runtime.Principal,
            source,
            forceRefresh: true);

        Assert.False(Directory.Exists(stale));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(runtime.ArtifactRoot, "tmp")));
        runtime.Registry.Unload(runtime.Principal, first.Handle.TraceId);
    }

    [Fact]
    public void TemporaryQuota_CountsInputAndDerivedArtifactAndFailsBeforePublication()
    {
        var fixture = Path.GetFullPath(Path.Combine("fixtures", "small_cpu.etl"));
        var inputBytes = new FileInfo(fixture).Length;
        using var runtime = TestRuntime.Create(maxStoreBytes: inputBytes);
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");

        var quota = Assert.Throws<TraceReferenceException>(() =>
            runtime.Lifecycle.Load(runtime.Principal, source));

        Assert.Equal("budget_exceeded", quota.Code);
        Assert.Equal("trace_artifact_temporary_quota_exceeded", quota.DetailCode);
        Assert.Equal(1, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(runtime.ArtifactRoot, "tmp")));
        Assert.False(Directory.Exists(Path.Combine(runtime.ArtifactRoot, "objects")));
        Assert.Equal(
            "accepted_residual_risk:retained_quota_enforced;single_materialization_checkpoint_budget;opaque_converter_transient_peak_not_hard_limited",
            OwnedTraceArtifactStore.TemporarySpaceAssurance);
        Assert.Equal(
            0,
            runtime.Registry.GetPrincipalStatus(runtime.Principal).ActivePersistentHandles);
    }

    [Fact]
    public async Task DifferentMaterializations_AreSerializedAcrossTemporarySpace()
    {
        using var firstConversionStarted = new ManualResetEventSlim();
        using var releaseFirstConversion = new ManualResetEventSlim();
        var activeConversions = 0;
        var maximumConcurrentConversions = 0;
        var invocations = 0;
        using var runtime = TestRuntime.Create(
            convertTrace: path =>
            {
                var active = Interlocked.Increment(ref activeConversions);
                var observed = Volatile.Read(ref maximumConcurrentConversions);
                while (active > observed)
                {
                    var prior = Interlocked.CompareExchange(
                        ref maximumConcurrentConversions,
                        active,
                        observed);
                    if (prior == observed)
                        break;
                    observed = prior;
                }

                var invocation = Interlocked.Increment(ref invocations);
                try
                {
                    if (invocation == 1)
                    {
                        firstConversionStarted.Set();
                        Assert.True(releaseFirstConversion.Wait(TimeSpan.FromSeconds(30)));
                    }
                    return Microsoft.Diagnostics.Tracing.Etlx.TraceLog.OpenOrConvert(path);
                }
                finally
                {
                    Interlocked.Decrement(ref activeConversions);
                }
            });
        var firstSource = runtime.CopyFixture("small_cpu.etl", "first.etl");
        var secondSource = runtime.CopyFixture("small_fileio.etl", "second.etl");

        var first = Task.Run(() => runtime.Loader.LoadAsync(firstSource, forceRefresh: false));
        Assert.True(firstConversionStarted.Wait(TimeSpan.FromSeconds(30)));
        var second = Task.Run(() => runtime.Loader.LoadAsync(secondSource, forceRefresh: false));
        Assert.True(SpinWait.SpinUntil(
            () => runtime.Loader.InFlightCount == 2,
            TimeSpan.FromSeconds(30)));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentConversions));
        releaseFirstConversion.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, Volatile.Read(ref invocations));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentConversions));
        Assert.Equal(0, runtime.Loader.InFlightCount);
    }

    [Fact]
    public async Task ArtifactQuota_SkipsPinnedGeneration_ThenEvictsAfterDrain()
    {
        using var runtime = TestRuntime.Create(maxObjects: 1);
        var firstSource = runtime.CopyFixture("small_cpu.etl", "first.etl");
        var secondSource = runtime.CopyFixture("small_fileio.etl", "second.etl");
        var first = runtime.Lifecycle.Load(runtime.Principal, firstSource);

        var quota = Assert.Throws<TraceReferenceException>(() =>
            runtime.Lifecycle.Load(runtime.Principal, secondSource));
        Assert.Equal("budget_exceeded", quota.Code);
        Assert.True(File.Exists(first.Artifact.TracePath));
        using (var query = runtime.Registry.Acquire(runtime.Principal, first.Handle.TraceId))
            Assert.True(query.Trace.EventCount > 0);

        var unloaded = runtime.Registry.Unload(runtime.Principal, first.Handle.TraceId);
        await unloaded.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
        var second = runtime.Lifecycle.Load(runtime.Principal, secondSource, forceRefresh: true);
        Assert.False(File.Exists(first.Artifact.TracePath));
        Assert.True(File.Exists(second.Artifact.TracePath));
        runtime.Registry.Unload(runtime.Principal, second.Handle.TraceId);
    }

    [Fact]
    public async Task ArtifactRetentionTtl_ExpiresOnlyUnpinnedObjectsAndRematerializes()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        using var runtime = TestRuntime.Create(
            retentionTtl: TimeSpan.FromHours(1),
            utcNow: () => now);
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");

        var first = runtime.Lifecycle.Load(runtime.Principal, source);
        now = now.AddHours(2);

        // A live trace handle pins the retained object, so TTL cannot invalidate it.
        var reused = runtime.Lifecycle.Load(runtime.Principal, source);
        Assert.Equal(first.Handle.TraceId, reused.Handle.TraceId);
        Assert.True(reused.Handle.ReusedExisting);
        Assert.Equal(1, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);

        var unloaded = runtime.Registry.Unload(runtime.Principal, first.Handle.TraceId);
        await unloaded.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
        now = now.AddHours(2);

        // The loader's bounded generation cache cannot resurrect an expired,
        // independently retained object after its final pin drains.
        var rematerialized = runtime.Lifecycle.Load(runtime.Principal, source);
        Assert.NotEqual(first.Handle.TraceId, rematerialized.Handle.TraceId);
        Assert.False(rematerialized.Handle.ReusedExisting);
        Assert.Equal(2, runtime.Store.SnapshotCopyCount);
        Assert.Equal(2, runtime.Store.ConversionCount);
        runtime.Registry.Unload(runtime.Principal, rematerialized.Handle.TraceId);
    }

    [Fact]
    public void IdOnlyRejectsRawOrReservedMalformedReference_BeforeArtifactCreation()
    {
        using var runtime = TestRuntime.Create();
        var raw = Path.Combine(runtime.SourceRoot, "does-not-exist.etl");
        var resolver = new TraceReferenceResolver(runtime.Registry, runtime.Lifecycle);

        var rawError = Assert.Throws<TraceReferenceException>(() =>
            resolver.ResolveQuery(runtime.Principal, raw, TraceAccessMode.IdOnly));
        Assert.Equal("raw_path_not_allowed", rawError.DetailCode);
        var malformed = Assert.Throws<TraceReferenceException>(() =>
            resolver.ResolveQuery(runtime.Principal, "TRC_bad", TraceAccessMode.Compatibility));
        Assert.Equal("malformed_trace_id", malformed.DetailCode);

        Assert.False(runtime.Store.ArtifactRootCreated);
        Assert.False(Directory.Exists(runtime.ArtifactRoot));
        Assert.Equal(0, runtime.Store.SnapshotCopyCount);
    }

    [Fact]
    public async Task ActiveArtifactPin_BlocksObjectAndRootReplacement()
    {
        using var runtime = TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var loaded = runtime.Lifecycle.Load(runtime.Principal, source);
        var replacement = Path.Combine(Path.GetDirectoryName(loaded.Artifact.TracePath)!, "replacement.etlx");
        File.Copy(loaded.Artifact.TracePath, replacement);

        AssertSharingViolation(() =>
            File.Move(replacement, loaded.Artifact.TracePath, overwrite: true));
        AssertSharingViolation(() =>
            Directory.Move(runtime.ArtifactRoot, runtime.ArtifactRoot + "-moved"));
        using (var query = runtime.Registry.Acquire(runtime.Principal, loaded.Handle.TraceId))
            Assert.True(query.Trace.EventCount > 0);

        var unload = runtime.Registry.Unload(runtime.Principal, loaded.Handle.TraceId);
        await unload.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PinnedArtifact_HashesHeldFileAndRejectsPathReplacement()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-pin-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var trusted = new TrustedTraceArtifactRoot(root);
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var secured = new DirectoryInfo(root).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            Assert.Equal(identity.User, secured.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(secured.AreAccessRulesProtected);
            var artifact = Path.Combine(root, "trace.etlx");
            var replacement = Path.Combine(root, "replacement.etlx");
            await File.WriteAllBytesAsync(artifact, [1, 2, 3, 4, 5]);
            await File.WriteAllBytesAsync(replacement, [5, 4, 3, 2, 1]);
            using var pin = trusted.Pin(artifact);

            AssertSharingViolation(() =>
                File.Move(replacement, artifact, overwrite: true));
            var actualHash = await pin.ComputeSha256Async(CancellationToken.None);
            var expectedHash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4, 5]));
            Assert.Equal(expectedHash, actualHash);
            pin.VerifyUnchanged();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductionTools_ReturnTraceIdAndNeverOwnedOrSourcePath()
    {
        var factsBuildCount = 0;
        using var runtime = TestRuntime.Create(
            factsBuilder: (trace, generationSequence, cancellationToken) =>
            {
                Interlocked.Increment(ref factsBuildCount);
                return TraceFactsSnapshotBuilder.Build(
                    trace,
                    generationSequence,
                    cancellationToken,
                    TraceFactsBuildBudget.Default);
            });
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var toolRuntime = new TraceToolRuntime(
            runtime.Lifecycle,
            runtime.Registry,
            runtime.SessionPrincipal);
        var tools = new MetaTools(runtime.Cache, toolRuntime);

        var response = tools.LoadTrace(source);
        Assert.Equal(response.TraceId, response.Trace.Path);
        Assert.DoesNotContain(runtime.SourceRoot, response.Trace.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runtime.ArtifactRoot, response.Trace.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "opened_handle_snapshot_content_hash_verified",
            response.SourceGenerationAssurance);
        Assert.Equal(1, Volatile.Read(ref factsBuildCount));
        using (var factsLease = runtime.Registry.Acquire(runtime.Principal, response.TraceId!))
        {
            Assert.Equal("ready", factsLease.FactsTelemetry.State);
            Assert.Equal(1, factsLease.FactsTelemetry.PhysicalPassCount);
        }

        var cached = tools.LoadTrace(source);
        Assert.Equal(
            "cached_file_identity_length_timestamps",
            cached.SourceGenerationAssurance);
        Assert.Equal(1, Volatile.Read(ref factsBuildCount));
        var refreshed = tools.LoadTrace(source, forceRefresh: true);
        Assert.Equal(
            "opened_handle_snapshot_content_hash_verified",
            refreshed.SourceGenerationAssurance);
        Assert.True(refreshed.ForceRefreshApplied);
        // The source refresh produced identical content, so the immutable artifact
        // generation and its facts snapshot are intentionally reused.
        Assert.Equal(1, Volatile.Read(ref factsBuildCount));
        Assert.Equal(2, runtime.Store.SnapshotCopyCount);
        Assert.Equal(1, runtime.Store.ConversionCount);

        var first = tools.UnloadTrace(response.TraceId!);
        var second = tools.UnloadTrace(response.TraceId!);
        Assert.Equal("unloaded", first.LifecycleStatus);
        Assert.Equal("already_unloaded", second.LifecycleStatus);
        Assert.Equal("drained", second.DrainStatus);
        Assert.Equal("retained_by_independent_policy", first.ArtifactDisposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadTrace_FactsBudgetFailureLeavesNoHandleOrBackgroundScan(
        bool elapsedBudget)
    {
        var started = 0;
        var completed = 0;
        using var runtime = TestRuntime.Create(
            factsBuilder: (trace, generationSequence, cancellationToken) =>
            {
                Interlocked.Increment(ref started);
                try
                {
                    return TraceFactsSnapshotBuilder.Build(
                        trace,
                        generationSequence,
                        cancellationToken,
                        elapsedBudget
                            ? new TraceFactsBuildBudget(long.MaxValue, TimeSpan.Zero)
                            : new TraceFactsBuildBudget(1, TimeSpan.FromMinutes(1)));
                }
                finally
                {
                    Interlocked.Increment(ref completed);
                }
            });
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var tools = new MetaTools(
            runtime.Cache,
            new TraceToolRuntime(
                runtime.Lifecycle,
                runtime.Registry,
                runtime.SessionPrincipal));

        var failure = Assert.Throws<TraceFactsSnapshotException>(() =>
            tools.LoadTrace(source));

        Assert.Equal("budget_exceeded", failure.Code);
        Assert.Equal("trace_facts_budget_exceeded", failure.DetailCode);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Equal(1, Volatile.Read(ref completed));
        var status = runtime.Registry.GetPrincipalStatus(runtime.Principal);
        Assert.Equal(0, status.ActivePersistentHandles);
        Thread.Yield();
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Equal(1, Volatile.Read(ref completed));
    }

    private static void AssertSharingViolation(Action action)
    {
        var exception = Record.Exception(action);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected a sharing/access violation, got: {exception}");
    }

    internal sealed class TestRuntime : IDisposable
    {
        private readonly string _root;
        private int _disposed;

        private TestRuntime(
            string root,
            string sourceRoot,
            string artifactRoot,
            TraceAccessPolicy policy,
            OwnedTraceArtifactStore store,
            TraceArtifactLoader loader,
            TraceCache cache,
            TraceHandleRegistry registry,
            TraceLifecycleService lifecycle,
            StdioSessionPrincipal sessionPrincipal)
        {
            _root = root;
            SourceRoot = sourceRoot;
            ArtifactRoot = artifactRoot;
            Policy = policy;
            Store = store;
            Loader = loader;
            Cache = cache;
            Registry = registry;
            Lifecycle = lifecycle;
            SessionPrincipal = sessionPrincipal;
        }

        internal string SourceRoot { get; }
        internal string ArtifactRoot { get; }
        internal TraceAccessPolicy Policy { get; }
        internal OwnedTraceArtifactStore Store { get; }
        internal TraceArtifactLoader Loader { get; }
        internal TraceCache Cache { get; }
        internal TraceHandleRegistry Registry { get; }
        internal TraceLifecycleService Lifecycle { get; }
        internal StdioSessionPrincipal SessionPrincipal { get; }
        internal string Principal => SessionPrincipal.RegistryKey;

        internal static TestRuntime Create(
            int maxObjects = 128,
            long maxStoreBytes = TraceRuntimeOptions.DefaultMaxArtifactStoreBytes,
            Func<string, Microsoft.Diagnostics.Tracing.Etlx.TraceLog>? openTrace = null,
            Func<string, Microsoft.Diagnostics.Tracing.Etlx.TraceLog>? convertTrace = null,
            Func<CancellationToken, ValueTask>? beforeSnapshot = null,
            Func<Microsoft.Diagnostics.Tracing.Etlx.TraceLog, long, CancellationToken,
                TraceFactsSnapshot>? factsBuilder = null,
            TimeSpan? retentionTtl = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "wpa-mcp-trace-runtime", Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(root, "sources");
            var artifactRoot = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(sourceRoot);
            var options = new TraceRuntimeOptions(
                TraceAccessMode.IdOnly,
                [sourceRoot],
                artifactRoot,
                TraceRuntimeOptions.DefaultMaxInputTraceBytes,
                maxStoreBytes,
                maxObjects,
                TraceRuntimeOptions.DefaultArtifactRetentionTtl);
            var policy = new TraceAccessPolicy(options);
            var store = new OwnedTraceArtifactStore(
                artifactRoot,
                options.MaxInputTraceBytes,
                options.MaxArtifactStoreBytes,
                options.MaxArtifactObjects,
                convertTrace,
                beforeSnapshot,
                retentionTtl: retentionTtl ?? options.ArtifactRetentionTtl,
                utcNow: utcNow);
            var loader = new TraceArtifactLoader(policy, store);
            var cache = new TraceCache(
                capacity: 8,
                openTrace: openTrace ??
                    (static path => Microsoft.Diagnostics.Tracing.Etlx.TraceLog.OpenOrConvert(path)),
                disposeTrace: trace => trace.Dispose(),
                factsBuilder: factsBuilder);
            var registry = new TraceHandleRegistry(cache);
            var lifecycle = new TraceLifecycleService(loader, registry);
            return new TestRuntime(
                root,
                sourceRoot,
                artifactRoot,
                policy,
                store,
                loader,
                cache,
                registry,
                lifecycle,
                new StdioSessionPrincipal());
        }

        internal string CopyFixture(string fixtureName, string targetName)
        {
            var source = Path.GetFullPath(Path.Combine("fixtures", fixtureName));
            var target = Path.Combine(SourceRoot, targetName);
            File.Copy(source, target);
            return target;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Registry.Dispose();
            Cache.Dispose();
            Store.Dispose();
            Policy.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
