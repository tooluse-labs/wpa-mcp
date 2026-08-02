using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class SymbolContextCoreTests
{
    [Fact]
    public async Task Tokens_AreCanonicalPrincipalScopedAndPubliclyNonEnumerating()
    {
        var random = new SequenceRandom();
        await using var registry = Registry(randomBytes: random.Next);
        var alice = new SymbolPrincipal("session:alice");
        var bob = new SymbolPrincipal("session:bob");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(alice, Prepared(alice, trace));

        Assert.True(SymbolContextRegistry.HasCanonicalShape(descriptor.SymbolContextId));
        Assert.Matches("^sym_[0-9a-f]{32}$", descriptor.SymbolContextId);
        Assert.Null(typeof(SymbolContextDescriptor).GetProperty(
            "TraceGenerationIdentity",
            BindingFlags.Instance | BindingFlags.Public));

        var crossPrincipal = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(bob, descriptor.SymbolContextId, trace.GenerationIdentity));
        var randomUnknown = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(
                alice,
                "sym_ffffffffffffffffffffffffffffffff",
                trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Unknown, crossPrincipal.Failure);
        Assert.Equal(SymbolContextFailure.Unknown, randomUnknown.Failure);
        Assert.Equal(
            SymbolContextPublicErrorProjection.Project(randomUnknown),
            SymbolContextPublicErrorProjection.Project(crossPrincipal));
        Assert.Equal("symbol_context_expired", SymbolContextPublicErrorProjection.Project(crossPrincipal).Code);
        Assert.Null(SymbolContextPublicErrorProjection.Project(crossPrincipal).DetailCode);

        foreach (var malformed in new[]
                 {
                     "SYM_00000000000000000000000000000000",
                     "sym_0000000000000000000000000000000",
                     "sym_0000000000000000000000000000000g",
                     "not-a-context",
                 })
        {
            var exception = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await registry.AcquireAsync(alice, malformed, trace.GenerationIdentity));
            Assert.Equal(SymbolContextFailure.Malformed, exception.Failure);
            Assert.Equal("invalid_argument", SymbolContextPublicErrorProjection.Project(exception).Code);
        }
    }

    [Fact]
    public async Task Lease_RetireAndDispose_DrainsBeforeReleasingPinnedArtifacts()
    {
        var pin = Pin();
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(principal, Prepared(principal, trace, pin: pin));
        await using var lease = await registry.AcquireAsync(
            principal,
            descriptor.SymbolContextId,
            trace.GenerationIdentity);

        var retiring = await registry.RetireAsync(
            principal,
            descriptor.SymbolContextId,
            waitForDrain: false);

        Assert.Equal(SymbolContextRetireDisposition.Draining, retiring.Disposition);
        Assert.Equal(1, retiring.ActiveLeases);
        Assert.Equal(0, pin.DisposeCount);
        var retiredLookup = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(principal, descriptor.SymbolContextId, trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Retired, retiredLookup.Failure);
        Assert.Equal("symbol_context_expired", SymbolContextPublicErrorProjection.Project(retiredLookup).Code);

        await lease.DisposeAsync();
        await EventuallyAsync(() => pin.DisposeCount == 1);
        Assert.Equal(0, registry.OwnedCount(principal));
        var repeated = await registry.RetireAsync(
            principal,
            descriptor.SymbolContextId,
            waitForDrain: true);
        Assert.Equal(SymbolContextRetireDisposition.AlreadyRetired, repeated.Disposition);
    }

    [Fact]
    public async Task IdleAndAbsoluteTtl_ExpireWithoutSilentReplacement()
    {
        var clock = new MutableClock();
        var options = Options() with
        {
            IdleTtl = TimeSpan.FromMinutes(2),
            AbsoluteTtl = TimeSpan.FromMinutes(5),
        };
        await using var registry = new SymbolContextRegistry(options, clock.UtcNow, new SequenceRandom().Next);
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");

        var idlePin = Pin(hash: Hash('1'));
        var idle = await registry.PublishAsync(principal, Prepared(principal, trace, pin: idlePin));
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1));
        var idleError = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(principal, idle.SymbolContextId, trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Expired, idleError.Failure);
        await EventuallyAsync(() => idlePin.DisposeCount == 1);

        var absolutePin = Pin(hash: Hash('2'));
        var absolute = await registry.PublishAsync(principal, Prepared(principal, trace, pin: absolutePin));
        for (var index = 0; index < 2; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var keepAlive = await registry.AcquireAsync(
                principal,
                absolute.SymbolContextId,
                trace.GenerationIdentity);
        }
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromTicks(1));
        var absoluteError = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(principal, absolute.SymbolContextId, trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Expired, absoluteError.Failure);
        await EventuallyAsync(() => absolutePin.DisposeCount == 1);
    }

    [Fact]
    public async Task CountRateAndTombstoneQuotas_ArePerPrincipalAndBounded()
    {
        var clock = new MutableClock();
        var options = Options() with
        {
            MaxContextsPerPrincipal = 1,
            MaxPrepareAttemptsPerWindow = 2,
            PrepareRateWindow = TimeSpan.FromMinutes(1),
            MaxTombstonesPerPrincipal = 2,
        };
        await using var registry = new SymbolContextRegistry(options, clock.UtcNow, new SequenceRandom().Next);
        var principal = new SymbolPrincipal("session:a");
        var other = new SymbolPrincipal("session:b");
        var trace = Trace("generation-a");

        registry.RecordPrepareAttempt(principal);
        registry.RecordPrepareAttempt(principal);
        var rate = Assert.Throws<SymbolContextException>(() => registry.RecordPrepareAttempt(principal));
        Assert.Equal(SymbolContextFailure.RateLimited, rate.Failure);
        Assert.Equal("budget_exceeded", SymbolContextPublicErrorProjection.Project(rate).Code);
        registry.RecordPrepareAttempt(other);
        clock.Advance(TimeSpan.FromMinutes(1));
        registry.RecordPrepareAttempt(principal);

        var first = await registry.PublishAsync(principal, Prepared(principal, trace, hash: Hash('1')));
        var rejectedPin = Pin(hash: Hash('2'));
        var quota = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.PublishAsync(
                principal,
                Prepared(principal, trace, pin: rejectedPin, hash: Hash('2'))));
        Assert.Equal(SymbolContextFailure.QuotaExceeded, quota.Failure);
        Assert.Equal(1, rejectedPin.DisposeCount);
        Assert.Equal("budget_exceeded", SymbolContextPublicErrorProjection.Project(quota).Code);

        await registry.RetireAsync(principal, first.SymbolContextId, waitForDrain: true);
        for (var index = 0; index < 3; index++)
        {
            var descriptor = await registry.PublishAsync(
                principal,
                Prepared(principal, trace, hash: Hash((char)('3' + index))));
            await registry.RetireAsync(principal, descriptor.SymbolContextId, waitForDrain: true);
        }
        Assert.Equal(2, registry.TombstoneCount(principal));
    }

    [Fact]
    public async Task EquivalentDefinition_ReusesActiveId_AndEveryImmutableInputChangesRevision()
    {
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var activePin = Pin(hash: Hash('1'));
        var first = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: activePin, hash: Hash('1')));
        var duplicatePin = Pin(hash: Hash('1'));
        var duplicate = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: duplicatePin, hash: Hash('1')));

        Assert.Equal(first.SymbolContextId, duplicate.SymbolContextId);
        Assert.Equal(first.ContextRevision, duplicate.ContextRevision);
        Assert.Equal(1, duplicatePin.DisposeCount);
        Assert.Equal(0, activePin.DisposeCount);

        var revisions = new HashSet<string>(StringComparer.Ordinal)
        {
            first.ContextRevision,
            Definition(new SymbolPrincipal("session:b"), trace, hash: Hash('1')).Revision,
            Definition(principal, Trace("generation-b"), hash: Hash('1')).Revision,
            Definition(principal, trace, policy: Policy(revision: "policy-v2"), hash: Hash('1')).Revision,
            Definition(principal, trace, resolverVersion: "resolver-v2", hash: Hash('1')).Revision,
            Definition(principal, Trace("generation-a", signature: Guid.Parse("22222222-2222-2222-2222-222222222222")), hash: Hash('1')).Revision,
            Definition(principal, trace, hash: Hash('2')).Revision,
            Definition(principal, trace, privacy: "restricted", hash: Hash('1')).Revision,
            Definition(principal, trace, contract: "3.0", hash: Hash('1')).Revision,
        };
        Assert.Equal(9, revisions.Count);
    }

    [Fact]
    public async Task ConcurrentEquivalentPrepare_IsSingleFlightAndReturnsOneCanonicalId()
    {
        var resolver = new GatePreparationResolver();
        await using var registry = Registry(Options() with { MaxPrepareAttemptsPerWindow = 100 });
        var service = Service(registry, resolver);
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");

        var calls = Enumerable.Range(0, 20)
            .Select(_ => service.PrepareAsync(
                    principal,
                    trace,
                    "local",
                    "standard",
                    "2.0")
                .AsTask())
            .ToArray();
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resolver.CallCount);
        resolver.Release.TrySetResult();

        var descriptors = await Task.WhenAll(calls);
        Assert.Single(descriptors.Select(static descriptor => descriptor.SymbolContextId).Distinct());
        Assert.Single(descriptors.Select(static descriptor => descriptor.ContextRevision).Distinct());
        Assert.Equal(1, registry.ActiveCount(principal));
    }

    [Fact]
    public async Task FailedOrCancelledPrepare_DoesNotPublishOrLeakPins()
    {
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");

        await using (var failedRegistry = Registry())
        {
            var failure = new ThrowingPreparationResolver();
            var failed = Service(failedRegistry, failure);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await failed.PrepareAsync(principal, trace, "local", "standard", "2.0"));
            Assert.Equal(0, failedRegistry.ActiveCount(principal));
        }

        await using (var cancelledRegistry = Registry())
        {
            var blocking = new CancellationPreparationResolver();
            var cancelled = Service(cancelledRegistry, blocking);
            using var cancellation = new CancellationTokenSource();
            var call = cancelled.PrepareAsync(
                principal,
                trace,
                "local",
                "standard",
                "2.0",
                cancellation.Token).AsTask();
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            await EventuallyAsync(() => blocking.Cancelled);
            Assert.Equal(0, cancelledRegistry.ActiveCount(principal));
        }
    }

    [Fact]
    public async Task CancelledLastWaiter_PreventsPublicationEvenWhenResolverIgnoresCancellation()
    {
        var resolver = new CancellationIgnoringPreparationResolver();
        await using var registry = Registry();
        var service = Service(registry, resolver);
        var principal = new SymbolPrincipal("session:a");
        using var cancellation = new CancellationTokenSource();
        var call = service.PrepareAsync(
            principal,
            Trace("generation-a"),
            "local",
            "standard",
            "2.0",
            cancellation.Token).AsTask();
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        resolver.Release.TrySetResult();

        await EventuallyAsync(() => resolver.Pin.DisposeCount == 1);
        Assert.Equal(0, registry.ActiveCount(principal));
        Assert.Equal(0, registry.OwnedCount(principal));
    }

    [Fact]
    public async Task PolicyDenialAndRemoteUnimplemented_DoNotProbeLocalStore()
    {
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var store = new RecordingArtifactStore();
        var resolver = new LocalOnlySymbolPreparationResolver(store);

        await using (var deniedRegistry = Registry())
        {
            var deniedCatalog = new ApprovedSymbolPolicyCatalog([Policy()], static (_, _) => false);
            var deniedService = new SymbolPreparationService(deniedRegistry, deniedCatalog, resolver);
            var denied = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await deniedService.PrepareAsync(principal, trace, "local", "standard", "2.0"));
            Assert.Equal(SymbolContextFailure.PolicyDenied, denied.Failure);
            Assert.Equal("symbol_policy_denied", SymbolContextPublicErrorProjection.Project(denied).Code);
            Assert.Equal(0, store.ProbeCount);
        }

        await using (var remoteRegistry = Registry())
        {
            var remote = Policy(
                network: SymbolNetworkPolicy.ApprovedOrigins,
                origins: [new Uri("https://symbols.example.test")]);
            var remoteService = new SymbolPreparationService(
                remoteRegistry,
                new ApprovedSymbolPolicyCatalog([remote]),
                resolver);
            var unsupported = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await remoteService.PrepareAsync(principal, trace, "local", "standard", "2.0"));
            Assert.Equal(SymbolContextFailure.RemoteResolutionUnimplemented, unsupported.Failure);
            Assert.Equal("analysis_failed", SymbolContextPublicErrorProjection.Project(unsupported).Code);
            Assert.Equal(0, store.ProbeCount);
        }
    }

    [Fact]
    public async Task ConcreteLocalStore_ProbesOnlyApprovedRootsAndPinsVerifiedContent()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-context-{Guid.NewGuid():N}");
        var approvedRoot = Path.Combine(scratch, "approved");
        var outsideRoot = Path.Combine(scratch, "outside");
        var storeRoot = Path.Combine(scratch, "private-store");
        Directory.CreateDirectory(approvedRoot);
        Directory.CreateDirectory(outsideRoot);
        var expected = Module();
        var bytes = "verified test pdb content"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, expected.PdbName), bytes);
        await File.WriteAllBytesAsync(Path.Combine(outsideRoot, expected.PdbName), "outside"u8.ToArray());
        var verifier = new RecordingPdbVerifier(bytes);
        using var store = new LocalVerifiedSymbolArtifactStore(
            storeRoot,
            1024 * 1024,
            verifier,
            trustedRoot: new TestTrustedSymbolArtifactRoot(storeRoot));
        var resolver = new LocalOnlySymbolPreparationResolver(store);
        var policy = Policy(roots: [approvedRoot]);
        var request = new SymbolPreparationRequest(
            new SymbolPrincipal("session:a"),
            Trace("generation-a"),
            policy,
            "standard",
            "2.0");
        var originalEnvironment = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");

        try
        {
            await using var resolved = await resolver.PrepareAsync(request, default);
            var pin = Assert.Single(resolved.Pins);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), pin.Identity.ContentSha256);
            Assert.Equal(bytes.Length, pin.Identity.ByteLength);
            Assert.Equal(expected.PdbSignature, pin.Identity.PdbSignature);
            var readable = Assert.IsAssignableFrom<IReadableVerifiedSymbolArtifactPin>(pin);
            await using var stream = await readable.OpenReadAsync(default);
            using var copied = new MemoryStream();
            await stream.CopyToAsync(copied);
            Assert.Equal(bytes, copied.ToArray());
            Assert.All(verifier.Paths, path => Assert.StartsWith(storeRoot, path, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(verifier.Paths, path => path.StartsWith(outsideRoot, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(originalEnvironment, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        }
        finally
        {
            if (originalEnvironment is null)
                Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            else
                Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", originalEnvironment);
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task LocalStore_RejectsReparseRootDeviceRootAndAlternateDataStreamNames()
    {
        Assert.Throws<ArgumentException>(() => new TraceModulePdbIdentity(
            "image",
            "example.dll",
            "x64",
            "example.pdb:secret",
            Guid.NewGuid(),
            1));
        Assert.Throws<ArgumentException>(() => Policy(roots: [@"\\?\C:\symbols"]));

        var scratch = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-reparse-{Guid.NewGuid():N}");
        var outside = Path.Combine(scratch, "outside");
        var junction = Path.Combine(scratch, "approved-link");
        var storeRoot = Path.Combine(scratch, "private-store");
        Directory.CreateDirectory(outside);
        var expected = Module();
        var bytes = "outside pdb"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(outside, expected.PdbName), bytes);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = scratch,
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(junction);
            start.ArgumentList.Add(outside);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            Assert.True(process.ExitCode == 0, standardError);

            var verifier = new RecordingPdbVerifier(bytes);
            using var store = new LocalVerifiedSymbolArtifactStore(
                storeRoot,
                1024,
                verifier,
                trustedRoot: new TestTrustedSymbolArtifactRoot(storeRoot));
            var resolver = new LocalOnlySymbolPreparationResolver(store);
            await using var resolved = await resolver.PrepareAsync(
                new SymbolPreparationRequest(
                    new SymbolPrincipal("session:a"),
                    Trace("generation-a"),
                    Policy(roots: [junction]),
                    "standard",
                    "2.0"),
                default);

            Assert.Empty(resolved.Pins);
            Assert.Empty(verifier.Paths);
            Assert.Empty(verifier.Paths);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingContentAddressObject_IsRehashedFromPinnedHandleBeforeReuse()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-collision-{Guid.NewGuid():N}");
        var approvedRoot = Path.Combine(scratch, "approved");
        var storeRoot = Path.Combine(scratch, "private-store");
        Directory.CreateDirectory(approvedRoot);
        var expected = Module();
        var bytes = "same length content!"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, expected.PdbName), bytes);
        using var store = new LocalVerifiedSymbolArtifactStore(
            storeRoot,
            1024,
            new PermissivePdbVerifier(),
            trustedRoot: new TestTrustedSymbolArtifactRoot(storeRoot));
        var resolver = new LocalOnlySymbolPreparationResolver(store);
        var request = new SymbolPreparationRequest(
            new SymbolPrincipal("session:a"),
            Trace("generation-a"),
            Policy(roots: [approvedRoot]),
            "standard",
            "2.0");

        try
        {
            await using (var first = await resolver.PrepareAsync(request, default))
                Assert.Single(first.Pins);
            var objectPath = Assert.Single(Directory.GetFiles(
                Path.Combine(storeRoot, "objects"),
                "*.pdb",
                SearchOption.AllDirectories));
            await File.WriteAllBytesAsync(objectPath, Enumerable.Repeat((byte)'x', bytes.Length).ToArray());

            var error = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await resolver.PrepareAsync(request, default));
            Assert.Equal(SymbolContextFailure.ArtifactVerificationFailed, error.Failure);
            Assert.Equal("analysis_failed", SymbolContextPublicErrorProjection.Project(error).Code);
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateStoreQuota_UsesLruButNeverEvictsAnActivePin()
    {
        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"wpa-mcp-symbol-quota-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(scratch, "source");
        var storeRoot = Path.Combine(scratch, "store");
        Directory.CreateDirectory(sourceRoot);
        var firstIdentity = new TraceModulePdbIdentity(
            "image:first", "first.dll", "x64", "first.pdb", Guid.NewGuid(), 1);
        var secondIdentity = new TraceModulePdbIdentity(
            "image:second", "second.dll", "x64", "second.pdb", Guid.NewGuid(), 1);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, firstIdentity.PdbName),
            "first-pdb"u8.ToArray());
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, secondIdentity.PdbName),
            "secondpdb"u8.ToArray());
        using var store = new LocalVerifiedSymbolArtifactStore(
            storeRoot,
            maxArtifactBytes: 16,
            new PermissivePdbVerifier(),
            maxStoreBytes: 16,
            maxArtifactCount: 1,
            trustedRoot: new TestTrustedSymbolArtifactRoot(storeRoot));

        try
        {
            var firstPin = await store.TryVerifyAndPinLocalAsync(
                new ApprovedLocalSymbolCandidate(sourceRoot, [firstIdentity.PdbName]),
                firstIdentity,
                default);
            Assert.NotNull(firstPin);

            var quota = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await store.TryVerifyAndPinLocalAsync(
                    new ApprovedLocalSymbolCandidate(sourceRoot, [secondIdentity.PdbName]),
                    secondIdentity,
                    default));
            Assert.Equal(SymbolContextFailure.QuotaExceeded, quota.Failure);

            await firstPin!.DisposeAsync();
            await using var secondPin = await store.TryVerifyAndPinLocalAsync(
                new ApprovedLocalSymbolCandidate(sourceRoot, [secondIdentity.PdbName]),
                secondIdentity,
                default);
            Assert.NotNull(secondPin);
            Assert.Single(Directory.GetFiles(
                Path.Combine(storeRoot, "objects"),
                "*.pdb",
                SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void WithoutContext_ExposesOnlyTraceNativePdbMetadataAsUnmeasured()
    {
        var originalEnvironment = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        var boundary = SymbolEvidenceBoundary.WithoutContext(Trace("generation-a"));

        Assert.Equal(1, boundary.ModulesWithPdbIdentity);
        Assert.Equal("unmeasured", boundary.LocalReadinessState);
        Assert.Equal("unmeasured", boundary.LocalReadinessMeasurementBasis);
        Assert.Equal("unmeasured", boundary.FrameResolutionState);
        Assert.Null(boundary.FramesAttempted);
        Assert.Null(boundary.FramesResolved);
        Assert.Null(boundary.FrameResolutionRate);
        Assert.Equal(originalEnvironment, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
    }

    [Fact]
    public async Task ArtifactDisappearance_ExpiresContextInsteadOfUpgradingOrDowngrading()
    {
        var pin = Pin();
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(principal, Prepared(principal, trace, pin: pin));
        pin.Available = false;

        var missing = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(principal, descriptor.SymbolContextId, trace.GenerationIdentity));

        Assert.Equal(SymbolContextFailure.Expired, missing.Failure);
        Assert.Equal("symbol_context_expired", SymbolContextPublicErrorProjection.Project(missing).Code);
        await EventuallyAsync(() => pin.DisposeCount == 1);
        Assert.Equal(0, registry.ActiveCount(principal));
    }

    [Fact]
    public async Task PrepareDoesNotReuseCanonicalIdWhoseOldPinCanNoLongerSatisfyPromise()
    {
        var options = Options() with { MaxContextsPerPrincipal = 1 };
        await using var registry = Registry(options);
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var oldPin = Pin();
        var oldContext = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: oldPin));
        oldPin.Available = false;
        var replacementPin = Pin();

        var replacement = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: replacementPin));

        Assert.NotEqual(oldContext.SymbolContextId, replacement.SymbolContextId);
        Assert.Equal(oldContext.ContextRevision, replacement.ContextRevision);
        Assert.Equal(1, oldPin.DisposeCount);
        Assert.Equal(0, replacementPin.DisposeCount);
        Assert.Equal(1, registry.ActiveCount(principal));
        Assert.Equal(1, registry.OwnedCount(principal));
        var oldLookup = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(principal, oldContext.SymbolContextId, trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Expired, oldLookup.Failure);
    }

    [Fact]
    public async Task ArtifactAvailability_IsValidatedOnceAtLeaseAcquisition_NotPerFrame()
    {
        var pin = Pin();
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: pin));
        await using var lease = await registry.AcquireAsync(
            principal,
            descriptor.SymbolContextId,
            trace.GenerationIdentity);
        var resolver = new RecordingFrameResolver("resolver-v1", "resolved");
        var query = new ContextBoundSymbolQueryService(resolver);

        var first = await query.ResolveAsync(lease, Lookup(trace));
        var second = await query.ResolveAsync(
            lease,
            Lookup(trace) with { NormalizedAddress = 0x4321 });

        Assert.Equal("resolved", first.LookupState);
        Assert.Equal("resolved", second.LookupState);
        Assert.Equal(1, pin.AvailabilityCheckCount);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task CancelledLeaseAcquisition_DoesNotProbeArtifactsOrLeakALease()
    {
        var pin = Pin();
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, pin: pin));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await registry.AcquireAsync(
                principal,
                descriptor.SymbolContextId,
                trace.GenerationIdentity,
                cancellation.Token));

        Assert.Equal(0, pin.AvailabilityCheckCount);
        var retired = await registry.RetireAsync(
            principal,
            descriptor.SymbolContextId,
            waitForDrain: false);
        Assert.Equal(SymbolContextRetireDisposition.Retired, retired.Disposition);
        Assert.Equal(0, retired.ActiveLeases);
    }

    [Fact]
    public async Task ContextBoundQuery_MeasuresFramesAndKeepsOldAndNewNegativeCachesIsolated()
    {
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var oldDescriptor = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, hash: Hash('1')));
        var newDescriptor = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, hash: Hash('2')));
        var resolver = new RecordingFrameResolver("resolver-v1", result: null);
        var cache = new SymbolNegativeResultCache();
        var query = new ContextBoundSymbolQueryService(resolver, cache);
        var request = Lookup(trace);

        await using var oldLease = await registry.AcquireAsync(
            principal,
            oldDescriptor.SymbolContextId,
            trace.GenerationIdentity);
        Assert.Equal("unmeasured", oldDescriptor.Evidence.FrameResolutionState);
        Assert.Null(oldDescriptor.Evidence.FramesAttempted);
        var oldFirst = await query.ResolveAsync(oldLease, request);
        var oldSecond = await query.ResolveAsync(oldLease, request);
        Assert.Equal("measured", oldFirst.Measurement.MeasurementState);
        Assert.Equal(1, oldFirst.Measurement.FramesAttempted);
        Assert.Equal(0, oldFirst.Measurement.FramesResolved);
        Assert.False(oldFirst.FromNegativeCache);
        Assert.True(oldSecond.FromNegativeCache);
        Assert.Equal(1, resolver.CallCount);

        await using var newLease = await registry.AcquireAsync(
            principal,
            newDescriptor.SymbolContextId,
            trace.GenerationIdentity);
        var newFirst = await query.ResolveAsync(newLease, request);
        Assert.False(newFirst.FromNegativeCache);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(2, cache.Count);

        Assert.Equal(1, cache.InvalidateContext(newDescriptor.ContextRevision));
        Assert.Equal(1, cache.Count);
        var oldStillCached = await query.ResolveAsync(oldLease, request);
        Assert.True(oldStillCached.FromNegativeCache);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task QueryRequiresExactContextResolverTracePrivacyAndContractBinding()
    {
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(principal, Prepared(principal, trace));
        await using var lease = await registry.AcquireAsync(
            principal,
            descriptor.SymbolContextId,
            trace.GenerationIdentity);

        var wrongResolver = new ContextBoundSymbolQueryService(
            new RecordingFrameResolver("resolver-v2", "function"));
        var resolverFailure = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await wrongResolver.ResolveAsync(lease, Lookup(trace)));
        Assert.Equal(SymbolContextFailure.Expired, resolverFailure.Failure);

        var query = new ContextBoundSymbolQueryService(
            new RecordingFrameResolver("resolver-v1", "function"));
        foreach (var request in new[]
                 {
                     Lookup(Trace("generation-b")),
                     Lookup(trace) with { PrivacyProfile = "restricted" },
                     Lookup(trace) with { ContractVersion = "3.0" },
                 })
        {
            var mismatch = await Assert.ThrowsAsync<SymbolContextException>(async () =>
                await query.ResolveAsync(lease, request));
            Assert.Equal(SymbolContextFailure.TraceBindingMismatch, mismatch.Failure);
            Assert.Equal("symbol_context_expired", SymbolContextPublicErrorProjection.Project(mismatch).Code);
        }
    }

    [Fact]
    public async Task ContextWithNoVerifiedArtifact_DoesNotPretendReadyOrAttemptFrameLookup()
    {
        await using var registry = Registry();
        var principal = new SymbolPrincipal("session:a");
        var trace = Trace("generation-a");
        var descriptor = await registry.PublishAsync(
            principal,
            Prepared(principal, trace, includeArtifact: false));
        await using var lease = await registry.AcquireAsync(
            principal,
            descriptor.SymbolContextId,
            trace.GenerationIdentity);
        var resolver = new RecordingFrameResolver("resolver-v1", "should-not-run");
        var query = new ContextBoundSymbolQueryService(resolver);

        var result = await query.ResolveAsync(lease, Lookup(trace));

        Assert.Equal("not_ready", descriptor.Evidence.LocalReadinessState);
        Assert.Equal(0, descriptor.Evidence.ModulesWithVerifiedSymbolArtifact);
        Assert.Equal("unmeasured", descriptor.Evidence.FrameResolutionState);
        Assert.Equal("not_available_in_context", result.LookupState);
        Assert.Equal("not_attempted", result.Measurement.MeasurementState);
        Assert.Null(result.Measurement.FrameResolutionRate);
        Assert.Equal(0, resolver.CallCount);
    }

    private static SymbolContextRegistry Registry(
        SymbolContextRegistryOptions? options = null,
        Func<byte[]>? randomBytes = null)
        => new(
            options ?? Options(),
            randomBytes: randomBytes ?? new SequenceRandom().Next);

    private static SymbolContextRegistryOptions Options()
        => new(
            MaxContextsPerPrincipal: 32,
            MaxPrepareAttemptsPerWindow: 32,
            PrepareRateWindow: TimeSpan.FromMinutes(1),
            IdleTtl: TimeSpan.FromMinutes(5),
            AbsoluteTtl: TimeSpan.FromHours(1),
            MaxTombstonesPerPrincipal: 16);

    private static ApprovedSymbolPolicySnapshot Policy(
        string reference = "local",
        string revision = "policy-v1",
        IEnumerable<string>? roots = null,
        SymbolNetworkPolicy network = SymbolNetworkPolicy.Denied,
        IEnumerable<Uri>? origins = null)
        => new(
            reference,
            revision,
            roots ?? [Path.GetTempPath()],
            network,
            origins,
            "private-cache-v1");

    private static TraceModulePdbIdentity Module(
        Guid? signature = null)
        => new(
            imageIdentity: "image-sha256:abc",
            imageName: "example.dll",
            architecture: "x64",
            pdbName: "example.pdb",
            pdbSignature: signature ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            pdbAge: 1);

    private static OpaqueSymbolTraceGenerationReference Trace(
        string generation,
        Guid? signature = null)
        => new(generation, [Module(signature)]);

    private static VerifiedSymbolArtifactIdentity Artifact(
        string? hash = null,
        TraceModulePdbIdentity? module = null)
    {
        module ??= Module();
        return new VerifiedSymbolArtifactIdentity(
            hash ?? Hash('1'),
            128,
            module.PdbName,
            module.PdbSignature,
            module.PdbAge,
            "test-pdb");
    }

    private static TestPin Pin(string? hash = null)
        => new(Artifact(hash));

    private static SymbolContextDefinition Definition(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        ApprovedSymbolPolicySnapshot? policy = null,
        string resolverVersion = "resolver-v1",
        string privacy = "standard",
        string contract = "2.0",
        string? hash = null,
        bool includeArtifact = true)
        => SymbolContextDefinition.Create(
            principal,
            trace,
            policy ?? Policy(),
            resolverVersion,
            includeArtifact ? [Artifact(hash, trace.ModulePdbIdentities[0])] : [],
            privacy,
            contract);

    private static PreparedSymbolContext Prepared(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        ApprovedSymbolPolicySnapshot? policy = null,
        string resolverVersion = "resolver-v1",
        string privacy = "standard",
        string contract = "2.0",
        TestPin? pin = null,
        string? hash = null,
        bool includeArtifact = true)
    {
        if (!includeArtifact)
        {
            var emptyDefinition = Definition(
                principal,
                trace,
                policy,
                resolverVersion,
                privacy,
                contract,
                hash,
                includeArtifact: false);
            return new PreparedSymbolContext(
                emptyDefinition,
                SymbolPreparationEvidence.Create(trace.ModulePdbIdentities, []),
                []);
        }

        pin ??= Pin(hash);
        var definition = SymbolContextDefinition.Create(
            principal,
            trace,
            policy ?? Policy(),
            resolverVersion,
            [pin.Identity],
            privacy,
            contract);
        return new PreparedSymbolContext(
            definition,
            SymbolPreparationEvidence.Create(trace.ModulePdbIdentities, [pin.Identity]),
            [pin]);
    }

    private static SymbolPreparationService Service(
        SymbolContextRegistry registry,
        ISymbolPreparationResolver resolver)
        => new(
            registry,
            new ApprovedSymbolPolicyCatalog([Policy()]),
            resolver);

    private static SymbolFrameLookupRequest Lookup(ISymbolTraceGenerationReference trace)
    {
        var module = trace.ModulePdbIdentities[0];
        return new SymbolFrameLookupRequest(
            trace.GenerationIdentity,
            module.ImageIdentity,
            module.Architecture,
            module.PdbName,
            module.PdbSignature,
            module.PdbAge,
            NormalizedAddress: 0x1234,
            PrivacyProfile: "standard",
            ContractVersion: "2.0");
    }

    private static string Hash(char value) => new(value, 64);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected asynchronous condition was not reached.");
            await Task.Yield();
        }
    }

    private sealed class MutableClock
    {
        private DateTimeOffset _now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class SequenceRandom
    {
        private int _next;

        public byte[] Next()
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(Interlocked.Increment(ref _next)).CopyTo(bytes, 0);
            return bytes;
        }
    }

    private sealed class TestPin : IVerifiedSymbolArtifactPin
    {
        public TestPin(VerifiedSymbolArtifactIdentity identity) => Identity = identity;

        public VerifiedSymbolArtifactIdentity Identity { get; }

        public bool Available { get; set; } = true;

        public int DisposeCount { get; private set; }

        public int AvailabilityCheckCount { get; private set; }

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AvailabilityCheckCount++;
            return ValueTask.FromResult(Available && DisposeCount == 0);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Available = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Logic-only seam for managed-sandbox tests that forbid SetAccessControl. It
    /// cannot be selected by production DI, which always constructs the ACL/owner/
    /// retained-handle ProductionTrustedSymbolArtifactRoot and fails closed.
    /// </summary>
    private sealed class TestTrustedSymbolArtifactRoot : ITrustedSymbolArtifactRoot
    {
        public TestTrustedSymbolArtifactRoot(string path)
        {
            Path = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(path));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public bool ContainsFinalPath(string candidate)
        {
            if (candidate.StartsWith("\\\\?\\", StringComparison.Ordinal))
                candidate = candidate[4..];
            return candidate.StartsWith(
                Path + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingArtifactStore : IVerifiedSymbolArtifactStore
    {
        public int ProbeCount { get; private set; }

        public ValueTask<IVerifiedSymbolArtifactPin?> TryVerifyAndPinLocalAsync(
            ApprovedLocalSymbolCandidate candidate,
            TraceModulePdbIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return ValueTask.FromResult<IVerifiedSymbolArtifactPin?>(null);
        }
    }

    private sealed class RecordingPdbVerifier(byte[] expectedBytes) : ILocalPdbIdentityVerifier
    {
        private readonly byte[] _expectedBytes = expectedBytes;

        public ConcurrentBag<string> Paths { get; } = [];

        public async ValueTask<string?> VerifyFormatAsync(
            string immutableSnapshotPath,
            TraceModulePdbIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            Paths.Add(immutableSnapshotPath);
            var bytes = await File.ReadAllBytesAsync(immutableSnapshotPath, cancellationToken);
            return bytes.SequenceEqual(_expectedBytes) ? "test-pdb" : null;
        }
    }

    private sealed class PermissivePdbVerifier : ILocalPdbIdentityVerifier
    {
        public ValueTask<string?> VerifyFormatAsync(
            string immutableSnapshotPath,
            TraceModulePdbIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>("test-pdb");
        }
    }

    private sealed class GatePreparationResolver : ISymbolPreparationResolver
    {
        public string ResolverVersion => "resolver-v1";

        public int CallCount { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
            SymbolPreparationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ResolvedSymbolArtifacts([Pin()]);
        }
    }

    private sealed class ThrowingPreparationResolver : ISymbolPreparationResolver
    {
        public string ResolverVersion => "resolver-v1";

        public ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
            SymbolPreparationRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromException<ResolvedSymbolArtifacts>(new InvalidOperationException("expected failure"));
    }

    private sealed class CancellationPreparationResolver : ISymbolPreparationResolver
    {
        public string ResolverVersion => "resolver-v1";

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        public async ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
            SymbolPreparationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    private sealed class CancellationIgnoringPreparationResolver : ISymbolPreparationResolver
    {
        public string ResolverVersion => "resolver-v1";

        public TestPin Pin { get; } = Pin();

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
            SymbolPreparationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            return new ResolvedSymbolArtifacts([Pin]);
        }
    }

    private sealed class RecordingFrameResolver(string resolverVersion, string? result)
        : IContextBoundSymbolFrameResolver
    {
        public string ResolverVersion { get; } = resolverVersion;

        public int CallCount { get; private set; }

        public ValueTask<string?> ResolveFrameAsync(
            IVerifiedSymbolArtifactPin artifact,
            SymbolFrameLookupRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }
}
