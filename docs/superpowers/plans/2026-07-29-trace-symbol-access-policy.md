# Trace and Symbol Access Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make trace ingestion and symbol lookup fail closed, prevent validated-path replacement and input-directory side effects, and migrate every query to an opaque trace ID without losing an explicitly configured compatibility path.

**Architecture:** A startup-built `TraceAccessPolicy` is the only component allowed to turn an untrusted path into a handle-bound `ValidatedTraceSource`. `TraceArtifactStore` snapshots that handle into a private directory and publishes immutable content-addressed ETLX artifacts under a cross-process single-flight lock. `TraceRegistry` assigns random trace IDs and exposes leases through `ITraceReferenceResolver`; compatibility mode sends raw paths through the same load pipeline, while secure/default ID-only queries never touch the filesystem. Symbol configuration is parsed once into immutable `SymbolContext` values, and all local/remote symbol access passes through a deny-by-default policy.

**Tech Stack:** The TFM, MCP SDK, and TraceEvent package selected by Child 11A; the current implementation baseline is C# 12, .NET 8, Microsoft.Diagnostics.Tracing.TraceEvent 3.2.2, and ModelContextProtocol 1.2.0. Tests use xUnit 2.5.3 plus Windows file-handle APIs, `HttpClient`/`SocketsHttpHandler`, PowerShell, and POSIX installer scripts.

## Global Constraints

- Child 1 and Child 5 are completed prerequisites. This plan directly consumes Child 1's shared argument validation and Child 5's stable `ToolEnvelope`, error codes, startup contract selection, and privacy behavior; it does not create a temporary duplicate or alternate integration branch.
- Startup in every execution profile requires one or more `--trace-root` values. Compatibility means raw-path syntax remains accepted after policy validation; it never means unrestricted path access.
- `TraceAccessPolicy` alone constructs `ValidatedTraceSource`. The source path is diagnostic metadata only after validation; conversion and parsing consume the retained file handle or bytes copied from it.
- Query tools keep their existing public parameter name `path` during the compatibility/secure-default transition; its description says that compatibility accepts an allowed raw path or trace ID and ID-only accepts a trace ID. The next major version may rename it to `traceId`. Internally, use `traceReference`. In `compatibility`, a raw path is routed through `LoadAsync`, returns a structured deprecation warning, and makes the call's read-only annotation false. Any value whose first four characters are `trc_` under `StringComparison.OrdinalIgnoreCase` is in the reserved trace-ID namespace: a well-formed but unknown ID returns `trace_not_loaded`, and a malformed reserved-prefix value returns `invalid_argument`; neither can fall back to path parsing. A valid ID matches `^trc_[0-9a-f]{32}$` after enforcing lower-case canonical form.
- A rejected trace or symbol setting performs no network I/O and creates no file or directory. Validation therefore precedes artifact-root creation, DNS resolution, cache creation, and `TraceLog` construction.
- No runtime code reads or writes `_NT_SYMBOL_PATH`. Startup may import it only when `--import-nt-symbol-path` is set, after the same full validation used for `--symbol-path`.
- Child 5 owns alias syntax, the bounded process-local `ITypedAliasRegistry`, taxonomy, and `IToolArgumentRewriter`. Its exact rewrite seam runs before SDK binding and returns `ToolArgumentRewrite(JsonObject Arguments, IReadOnlyList<ResolvedAliasArgument> ResolvedAliases)`. Child 6 never reimplements alias lookup: rewritten `TracePath`/`SymbolPath` values pass through the same trace-root, reference-mode, local-root, cache, origin, CIDR, and redirect validation as literals before any I/O. Unknown/wrong-kind/overlong/evicted aliases remain Child 5 `invalid_argument`; an alias resolving to a raw trace path remains forbidden in ID-only mode.
- Child 6 creates the stable ownership-facing interfaces `ITraceRegistry`, `TraceLease`, `ITraceBackend`, and `ITraceReferenceResolver`. Its initial registry may retain entries for process lifetime. Child 7 changes only registry/lease state and quota internals; Child 8 changes only the backend to worker RPC in secure-default. Tool callers must already use `await using TraceLease` so neither later child needs another surface migration.
- Child 8 owns hard parser isolation. Until it lands, production packaging must not claim that secure-default is complete; this child only guarantees access policy, immutable artifacts, immutable symbol contexts, and no implicit query writes.
- Keep `tests/WprMcp.Tests/AssemblyInfo.cs` unchanged. Removing the global parallelism guard belongs to Child 9 after cross-process conversion tests exist.

## Configuration and Stable Interfaces

Add the following startup model in `src/WprMcp/McpServerOptions.cs`; every numeric value is validated as positive and at or below the stated hard ceiling before service registration:

```csharp
internal enum TraceReferenceMode { Compatibility, IdOnly }

internal sealed record TraceAccessOptions(
    IReadOnlyList<string> AllowedRoots,
    string ArtifactRoot,
    long MaxInputTraceBytes = 16L * 1024 * 1024 * 1024);

internal readonly record struct SymbolNetworkRange(
    IPAddress Network,
    int PrefixLength);

internal sealed record SymbolPolicyOptions(
    IReadOnlyList<string> AllowedLocalRoots,
    string CacheRoot,
    bool RemoteEnabled,
    IReadOnlyList<Uri> AllowedRemoteOrigins,
    IReadOnlyList<SymbolNetworkRange> AllowedDestinationNetworks,
    string? InitialSymbolPath,
    bool ImportNtSymbolPath,
    // Provisional Child 6 handoff only; Child 7 moves this into TraceRegistryOptions.
    long MaxCacheBytes = 20L * 1024 * 1024 * 1024,
    long MaxDownloadBytes = 512L * 1024 * 1024);

internal sealed record TraceAndSymbolPolicyOptions(
    TraceReferenceMode TraceReferences,
    TraceAccessOptions TraceAccess,
    SymbolPolicyOptions Symbols);

internal sealed record McpServerOptions(
    string[] HostArgs,
    int? CacheSize,
    ToolExecutionOptions ToolExecution,
    TraceAndSymbolPolicyOptions TracePolicy);
```

This is the staged Child 6 extension of Child 5's final `McpServerOptions`: `HostArgs`, nullable `CacheSize`, nested `ToolExecutionOptions ToolExecution`, and all Child 5 derived contract/privacy/budget accessors retain their meanings and tests. Child 6 consumes Child 5's temporary `SymbolPath` scalar while adding nested `TraceAndSymbolPolicyOptions TracePolicy`; the existing `--symbol-path` CLI flag remains accepted but its validated value is stored exactly once as `TracePolicy.Symbols.InitialSymbolPath`, and the final Child 6 record has no `SymbolPath` property. Child 6 does not flatten or reconstruct contract/privacy/budget fields. It does not re-parse, default, validate, or own `--cache-size`; the nullable `CacheSize` handoff remains only until Child 7 consumes it into `TraceRegistryOptions.MaxEntries`.

`SymbolPolicyOptions.MaxCacheBytes` is likewise a provisional Child 6 value so this child can bound its initial symbol-cache implementation; it defaults to 20 GiB and is never an independent hard ceiling. During the required Child 7 rebase, the same default and `--symbol-cache-bytes` configuration move exactly once to `TraceRegistryOptions.MaxSymbolCacheBytes`, and the final `SymbolPolicyOptions` type loses `MaxCacheBytes`. `RuntimeHardLimits.MaxSymbolCacheBytes` remains the immutable 100 GiB validation ceiling, not a second configured runtime quota.

The new CLI flags are `--trace-root <absolute-local-directory>` (repeatable and required), `--artifact-root <absolute-local-directory>`, `--trace-reference-mode compatibility|id-only`, `--symbol-local-root <absolute-local-directory>` (repeatable), `--symbol-cache-root <absolute-local-directory>`, `--symbol-cache-bytes <1..107374182400>`, `--enable-remote-symbols`, `--allow-symbol-origin <https-origin>` (repeatable), `--allow-symbol-network <canonical-ip-cidr>` (repeatable), and `--import-nt-symbol-path`. In Child 6, `--symbol-cache-bytes` populates the provisional `Symbols.MaxCacheBytes`; Child 7 preserves the identical flag/default while moving its value to `Registry.MaxSymbolCacheBytes`. Remote symbols require both `--enable-remote-symbols` and at least one origin. `--allow-symbol-origin` accepts an HTTPS origin with no user-info, query, fragment, or path other than `/`; the default origin and private-network allowlists are empty. Public unicast destinations need no CIDR entry; loopback/private/link-local destinations require containment in an explicit startup CIDR as well as an approved origin. Unspecified, multicast, broadcast, IPv4-mapped ambiguity, and zone-indexed addresses remain denied even if a CIDR would contain them.

Create these contracts and keep their signatures stable for Child 7 and Child 8:

```csharp
internal sealed record TraceSourceHandleIdentity(
    string CanonicalPath,
    uint VolumeSerialNumber,
    Guid FileId128,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record TraceSourceIdentity(
    TraceSourceHandleIdentity Handle,
    long SnapshotLength,
    string Sha256);

internal sealed record TraceArtifactKey(
    string Sha256,
    long Length,
    string TraceEventVersion,
    string ConversionOptionsVersion);

internal sealed record TraceDescriptor(
    string TraceId,
    TraceSourceIdentity Source,
    TraceArtifactKey ArtifactKey,
    string ArtifactPath,
    long ArtifactBytes,
    long DurationUs);

internal interface ITraceBackend : IAsyncDisposable
{
    TraceLog Trace { get; }
}

internal sealed class TraceLease : IAsyncDisposable
{
    public TraceDescriptor Descriptor { get; }
    public ITraceBackend Backend { get; }
    public ValueTask DisposeAsync();
}

internal interface ITraceRegistry
{
    ValueTask<TraceDescriptor> RegisterAsync(
        ValidatedTraceSource source,
        CancellationToken cancellationToken);
    ValueTask<TraceLease> AcquireAsync(string traceId, CancellationToken cancellationToken);
    ValueTask<UnloadTraceResult> UnloadAsync(
        string traceId,
        bool waitForDrain,
        CancellationToken cancellationToken);
}

internal sealed record ResolvedTraceReference(
    TraceLease Lease,
    bool LoadedFromRawPath,
    IReadOnlyList<string> Warnings);

internal interface ITraceReferenceResolver
{
    ValueTask<ResolvedTraceReference> ResolveQueryAsync(
        string traceReference,
        CancellationToken cancellationToken);
    ValueTask<TraceDescriptor> LoadAsync(string rawPath, CancellationToken cancellationToken);
}
```

`UnloadTraceResult` is introduced here with the four dispositions that Child 7 implements fully: `Drained`, `Pending`, `NotFound`, and `TimedOut`. The Child 6 implementation may return `Pending` for a known entry, but it must never dispose beneath an outstanding lease.

The artifact layout is fixed and contains no user-controlled path segment:

```text
<artifact-root>/objects/<first-two-hash-bytes>/<artifact-key>/trace.etlx
<artifact-root>/objects/<first-two-hash-bytes>/<artifact-key>/manifest.json
<artifact-root>/tmp/<server-instance>-<random-guid>/input.etl
<artifact-root>/tmp/<server-instance>-<random-guid>/trace.etlx
<artifact-root>/locks/<artifact-key>.lock
```

`artifact-key` is lower-case hex SHA-256 over the UTF-8 sequence `sha256:length:traceEventVersion:conversionOptionsVersion`. `ConversionOptionsVersion` starts at literal `wprmcp-etlx-v1`. Publication is a same-volume atomic `File.Move(temp, final)` after opening and validating the generated ETLX. A lock file is acquired with `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, and `FileShare.None`.

---

### Task 1: Parse and validate fail-closed startup policy

**Files:**

- Modify: `src/WprMcp/McpServerOptions.cs`
- Modify: `src/WprMcp/Program.cs`
- Create: `src/WprMcp/Core/RuntimeHardLimits.cs`
- Create: `tests/WprMcp.Tests/TraceAccessOptionsTests.cs`
- Modify: `tests/WprMcp.Tests/McpServerOptionsTests.cs`

**Interfaces:**

- Consumes: `string[] args` before MCP host construction.
- Produces: the staged Child 6 `McpServerOptions` shape (`HostArgs`, nullable `CacheSize`, `ToolExecution`, `TracePolicy`), nested `TraceAndSymbolPolicyOptions`/`TraceAccessOptions`/`SymbolPolicyOptions`, and a `RuntimeHardLimits` constants class shared by Children 7 and 8.

- [ ] **Step 1: Write failing option, compatibility, and side-effect-order tests.** Cover missing `--trace-root`, relative/UNC/device roots, duplicate canonical roots, artifact root inside an allowed trace root, remote enablement without an origin, non-HTTPS origins, credentials/query/fragment/path in an origin, malformed/non-canonical IPv4 and IPv6 CIDRs, IPv4-mapped/zone-indexed network entries, and input/symbol limits at ceiling and ceiling plus one. Parameterize Child 5's contract/privacy/budget cases and assert byte-for-byte equivalent `ToolExecution` and derived accessors after adding trace flags. Prove the existing `--symbol-path` syntax/default/error behavior now populates only `TracePolicy.Symbols.InitialSymbolPath` and reflection finds no scalar `McpServerOptions.SymbolPath`; prove nullable `CacheSize` still passes through unchanged because its range/consumption belongs to Child 7. Assert absent/explicit `--symbol-cache-bytes` produces the provisional 20 GiB/configured `Symbols.MaxCacheBytes`, with 100 GiB accepted and plus one rejected, so Child 7 can migrate the identical value without changing CLI behavior. In a missing-root test, set artifact/cache paths below a fresh test directory and assert that directory does not exist after parse failure.

```csharp
[Fact]
public void Parse_WithoutTraceRoot_FailsBeforeCreatingConfiguredDirectories()
{
    string scratch = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var error = Assert.Throws<OptionsValidationException>(() =>
        McpServerOptions.Parse([
            "--artifact-root", Path.Combine(scratch, "artifacts"),
            "--symbol-cache-root", Path.Combine(scratch, "symbols")
        ]));

    Assert.Equal("trace_root_required", error.Code);
    Assert.False(Directory.Exists(scratch));
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~McpServerOptionsTests|FullyQualifiedName~TraceAccessOptionsTests"` and verify failures name the absent policy records and validation errors.**

- [ ] **Step 3: Implement staged pure parsing and validation.** `Parse` must not touch the filesystem or environment. Begin from Child 5's parsed values, consume/remove its temporary `SymbolPath` scalar into validated `TracePolicy.Symbols.InitialSymbolPath`, and preserve `HostArgs`, nullable `CacheSize`, `ToolExecution`, output contract, privacy, budgets, and derived compatibility accessors. Keep accepting the `--symbol-path` CLI flag; do not retain a second scalar property or compatibility copy after construction. Resolve default artifact/cache locations only as strings. Put hard ceilings in constants: input 64 GiB, artifact 256 GiB, symbol cache 100 GiB, symbol download 2 GiB, queued loads 32, conversion workers 4, analysis workers 8, operation/worker CPU 60 minutes, worker committed memory 16 GiB, IPC frame 4 MiB, progress 10/s, event visits 1 billion, stack visits 100 million, and symbol attempts 1 million. Remove `ApplyToEnvironment`; register both `options.ToolExecution` and validated `options.TracePolicy` directly in `Program.cs`.

```csharp
internal static class RuntimeHardLimits
{
    internal const long MaxInputTraceBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaxArtifactStoreBytes = 256L * 1024 * 1024 * 1024;
    internal const long MaxSymbolCacheBytes = 100L * 1024 * 1024 * 1024;
    internal const long MaxSymbolDownloadBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaxQueuedTraceLoads = 32;
    internal const int MaxConversionWorkers = 4;
    internal const int MaxAnalysisWorkers = 8;
    internal static readonly TimeSpan MaxOperationWallTime = TimeSpan.FromMinutes(60);
}
```

- [ ] **Step 4: Run the focused command from Step 2, then `dotnet build WprMcp.sln -c Release`, and require zero failures.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/McpServerOptions.cs src/WprMcp/Program.cs src/WprMcp/Core/RuntimeHardLimits.cs tests/WprMcp.Tests/TraceAccessOptionsTests.cs tests/WprMcp.Tests/McpServerOptionsTests.cs
git commit -m "feat: validate trace and symbol startup policy"
```

### Task 2: Validate a trace through an open file object

**Files:**

- Create: `src/WprMcp/Core/TraceAccessPolicy.cs`
- Create: `src/WprMcp/Core/ValidatedTraceSource.cs`
- Create: `src/WprMcp/Core/WindowsFileIdentity.cs`
- Create: `tests/WprMcp.Tests/TraceAccessPolicyTests.cs`

**Interfaces:**

- Consumes: a raw path and immutable `TraceAccessOptions`.
- Produces: `ValueTask<ValidatedTraceSource> OpenAsync(string rawPath, CancellationToken)` where `ValidatedTraceSource` owns a `SafeFileHandle`, preliminary `TraceSourceHandleIdentity`, and an async `CopyToAsync(Stream destination, IncrementalHash hash, long maxBytes, CancellationToken)` method. Task 3 creates the authoritative `TraceSourceIdentity` only from that copy.

- [ ] **Step 1: Write failing hostile-path tests.** Cover relative names; `.txt`; case-insensitive `.ETL`/`.ETLX`; path-component escape (`C:\root2` must not match `C:\root`); `..`; ADS (`trace.etl:stream`); UNC; `\\?\`, `\\.\`, and `\??\` namespaces; directories; symlinks/junctions at every component; source larger than the configured maximum; source replacement after validation; and a pre-existing shared writer attempting mutation while the policy opens the file. The shared-writer case must either fail handle acquisition before artifact creation or later yield one internally consistent private snapshot; it may not publish a key for different bytes. Also inject a `RecordingFileSystemSideEffects` and assert zero create/open-for-write calls for every rejection.

```csharp
[Fact]
public async Task OpenAsync_ReplacementAfterValidation_DoesNotChangeCopiedBytes()
{
    await using ValidatedTraceSource source = await policy.OpenAsync(tracePath, default);
    File.Move(replacementPath, tracePath, overwrite: true);
    await using var destination = new MemoryStream();
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    await source.CopyToAsync(destination, hash, maxBytes: 1024, default);

    Assert.Equal(originalBytes, destination.ToArray());
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TraceAccessPolicyTests` and verify that the tests fail before `TraceAccessPolicy` exists.**

- [ ] **Step 3: Implement handle-first validation.** Canonicalize with `Path.GetFullPath`, reject namespace/UNC/ADS syntax before opening, verify component containment with `Path.GetRelativePath` using Windows ordinal-ignore-case component comparison, walk each root-to-file component with `File.GetAttributes` and reject `ReparsePoint`, then open using `File.OpenHandle` with `FileMode.Open`, `FileAccess.Read`, `FileShare.Read | FileShare.Delete`, `FileOptions.Asynchronous | FileOptions.SequentialScan`. Query final path and `FILE_ID_INFO` from the handle, repeat root containment on the final path, require a regular disk file, and read length from the handle only for an early maximum-size rejection. Do not compute or publish the authoritative content hash here; Task 3 computes it during the one snapshot copy that conversion consumes.

```csharp
internal sealed class ValidatedTraceSource : IAsyncDisposable
{
    private readonly SafeFileHandle _handle;
    internal TraceSourceHandleIdentity HandleIdentity { get; }
    internal string Extension { get; }

    internal async ValueTask<long> CopyToAsync(
        Stream destination,
        IncrementalHash hash,
        long maxBytes,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long offset = 0;
            int read;
            while ((read = await RandomAccess.ReadAsync(
                       _handle,
                       buffer.AsMemory(0, 1 << 20),
                       offset,
                       ct).ConfigureAwait(false)) != 0)
            {
                if (checked(offset + read) > maxBytes)
                    throw new TraceAccessException("trace_too_large");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                offset = checked(offset + read);
            }
            return offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run the focused test command. On Windows, additionally run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceAccessPolicyTests&FullyQualifiedName~Reparse"`; require all cases to pass without administrator privileges.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceAccessPolicy.cs src/WprMcp/Core/ValidatedTraceSource.cs src/WprMcp/Core/WindowsFileIdentity.cs tests/WprMcp.Tests/TraceAccessPolicyTests.cs
git commit -m "feat: bind trace validation to an open file"
```

### Task 3: Build and atomically publish immutable ETLX artifacts

**Files:**

- Create: `src/WprMcp/Core/TraceArtifactStore.cs`
- Create: `src/WprMcp/Core/TraceArtifactManifest.cs`
- Create: `src/WprMcp/Core/TraceEventConverter.cs`
- Create: `tests/WprMcp.Tests/TraceArtifactStoreTests.cs`
- Modify: `tests/WprMcp.Tests/TraceEventSmokeTests.cs`

**Interfaces:**

- Consumes: `ValidatedTraceSource` and `TraceAccessOptions`.
- Produces: `ITraceArtifactStore.GetOrCreateAsync(ValidatedTraceSource, CancellationToken) -> TraceArtifact`, with `TraceArtifact(TraceSourceIdentity Source, TraceArtifactKey Key, string EtlxPath, long Bytes, long DurationUs)`.

- [ ] **Step 1: Write failing tests for controlled output and snapshot integrity.** With a copied real fixture, assert `.etl` input creates no sibling `.etlx`, `.etlx.new`, or directory; a successful artifact has the fixed layout and manifest; an invalid generated ETLX is never published; publication is atomic; stale temporary directories older than 24 hours are scavenged only below `<artifact-root>/tmp`; and cancellation removes its private temporary directory. Verify source replacement after `OpenAsync` still produces an artifact whose hash matches the opened source bytes. Inject a handle reader that mutates/chunks between the preliminary length check and copy: the store must either fail closed on length/limit inconsistency or publish a manifest/key whose SHA-256 and length exactly match the fsynced private input actually passed to conversion. No pre-copy hash may enter the key.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceArtifactStoreTests|FullyQualifiedName~TraceEventSmokeTests"` and capture the expected sibling-sidecar or missing-store failures.**

- [ ] **Step 3: Implement one authoritative snapshot-copy stream.** Create the root only after a source has passed policy. In one loop, read the retained handle into a unique private input while incrementally computing SHA-256, counting with checked arithmetic, and enforcing `MaxInputTraceBytes`; fsync the input, require the final count to equal the handle's preliminary length (otherwise fail closed), and only then create `TraceSourceIdentity`/`TraceArtifactKey`. Conversion and parsing consume only this frozen snapshot. For `.etl`, call a `TraceEventConverter` overload that takes explicit private input and private output names; for `.etlx`, copy/rename the frozen snapshot to `trace.etlx`. Open the result with `TraceLog.OpenOrConvert(privateEtlxPath)`, read duration, dispose it, fsync the ETLX and manifest, then atomically move the complete object directory to the final same-volume location. The manifest records snapshot SHA/length and the published ETLX SHA/length separately. Treat an already-published object as reusable only when both hashes/lengths and the conversion-version fields validate; delete only this operation's private temp directory.

```csharp
internal interface ITraceArtifactStore
{
    ValueTask<TraceArtifact> GetOrCreateAsync(
        ValidatedTraceSource source,
        CancellationToken cancellationToken);
}

internal sealed record TraceArtifact(
    TraceSourceIdentity Source,
    TraceArtifactKey Key,
    string EtlxPath,
    long Bytes,
    long DurationUs);
```

- [ ] **Step 4: Run the focused tests twice to exercise reuse: `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceArtifactStoreTests|FullyQualifiedName~TraceEventSmokeTests"`; require the second run to leave no `<artifact-root>/tmp` children owned by the test.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceArtifactStore.cs src/WprMcp/Core/TraceArtifactManifest.cs src/WprMcp/Core/TraceEventConverter.cs tests/WprMcp.Tests/TraceArtifactStoreTests.cs tests/WprMcp.Tests/TraceEventSmokeTests.cs
git commit -m "feat: publish controlled immutable trace artifacts"
```

### Task 4: Add in-process and cross-process conversion single-flight

**Files:**

- Create: `src/WprMcp/Core/ArtifactSingleFlight.cs`
- Create: `tests/WprMcp.Tests/ArtifactSingleFlightTests.cs`
- Create: `tests/WprMcp.Tests/ArtifactStoreProcessHost.cs`
- Modify: `tests/WprMcp.Tests/WprMcp.Tests.csproj`
- Modify: `src/WprMcp/Core/TraceArtifactStore.cs`

**Interfaces:**

- Consumes: immutable `TraceArtifactKey` derived from Task 3's fsynced private snapshot and a conversion factory that consumes that snapshot.
- Produces: `ArtifactSingleFlight.RunAsync(TraceArtifactKey, Func<CancellationToken, Task<TraceArtifact>>, CancellationToken)` and a lock file held with `FileShare.None` through validation/publication.

- [ ] **Step 1: Write failing concurrency tests.** Start 16 same-process calls and assert one conversion. Start two helper processes against the same artifact root and fixture, wait on a barrier file, then assert both succeed, one final object exists, and no `.etlx.new` appears beside the input. Inject a conversion failure and cancellation; the next call must run the factory again and succeed. Prove the dictionary is in-flight-only: after a successful shared task completes, `InFlightCount` is zero; a later call invokes the artifact-store factory again, and that factory revalidates the durable manifest/ETLX under the object lock before reusing it. Delete the ETLX after its final Child 7 reference is released, call again with the same bytes, and assert revalidation detects the missing object and rebuilds it rather than returning a cached `TraceArtifact` that points at a deleted file. For the stale race, gate an old factory that deliberately ignores cancellation, cancel its only waiter so `TryRemoveExact` removes that lazy, start and publish a second lazy for the same key, then start a third gated durable-revalidation flight. Release the old factory to fault and assert its late cleanup cannot remove the third flight: a concurrent fourth call joins the third factory, which is invoked exactly once.

```csharp
[Fact]
public async Task FaultedFlight_RemovesOnlyItsOwnLazyInstance()
{
    using var firstCancellation = new CancellationTokenSource();
    Task<TraceArtifact> first = flight.RunAsync(
        key, cancellationIgnoringOldFactory, firstCancellation.Token);
    await oldFactoryEntered.Task;
    firstCancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

    Task<TraceArtifact> second = flight.RunAsync(key, succeedingFactory, default);
    TraceArtifact published = await second;
    Assert.Equal(0, flight.InFlightCount);

    Task<TraceArtifact> current = flight.RunAsync(key, gatedRevalidatingFactory, default);
    await currentFactoryEntered.Task;
    releaseOldFault.SetResult();
    await oldFactoryObservedFault.Task;
    Task<TraceArtifact> joined = flight.RunAsync(key, mustNotRunFactory, default);
    releaseCurrentFactory.SetResult();
    Assert.Equal((await current).Key, (await joined).Key);
    Assert.Equal(1, currentFactoryInvocations);
    Assert.Equal(0, flight.InFlightCount);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~ArtifactSingleFlightTests` and verify duplicate factory invocations or shared conversion races.**

- [ ] **Step 3: Implement compare-by-identity cleanup, in-flight-only retention, and the cross-process lock.** Each caller first completes its own handle-to-private-snapshot copy and derives the key from those exact bytes; single-flight begins only then, and a losing caller deletes only its redundant private snapshot. Use `ConcurrentDictionary<TraceArtifactKey, Lazy<Task<TraceArtifact>>>` plus a small `TryRemoveExact` helper implemented as `((ICollection<KeyValuePair<TraceArtifactKey, Lazy<Task<TraceArtifact>>>>)map).Remove(new(key, expectedLazy))`, which is an atomic key-and-value match on the current baseline and avoids assuming a newer public overload. Await the shared task with per-waiter cancellation; when the cancelled waiter removes its exact lazy, still attach an observing continuation to the cancellation-ignoring factory so a late fault is consumed and can only attempt identity-matched cleanup. Attach identity-matched completion cleanup when the shared task is created and remove that exact lazy after success, failure, or cancellation; already-attached awaiters retain their task result, but no completed `TraceArtifact` remains cached in this dictionary. Every later acquisition therefore re-enters `TraceArtifactStore`, acquires the cross-process object lock asynchronously with bounded retry/cancellation, and revalidates the manifest, ETLX presence, length, and hash before durable reuse; if final-reference cleanup deleted the object, rebuild from the caller's immutable snapshot. Never delete another process's lock or temp directory.

- [ ] **Step 4: Run the focused tests, then run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TraceArtifactStoreTests`; require retry and two-process cases to pass.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/ArtifactSingleFlight.cs src/WprMcp/Core/TraceArtifactStore.cs tests/WprMcp.Tests/ArtifactSingleFlightTests.cs tests/WprMcp.Tests/ArtifactStoreProcessHost.cs tests/WprMcp.Tests/WprMcp.Tests.csproj
git commit -m "feat: single-flight trace conversion across processes"
```

### Task 5: Enforce local and remote symbol policy at every hop

**Files:**

- Create: `src/WprMcp/Core/SymbolPolicy.cs`
- Create: `src/WprMcp/Core/SymbolPolicyHttpHandler.cs`
- Create: `src/WprMcp/Core/PolicySymbolReaderFactory.cs`
- Create: `tests/WprMcp.Tests/SymbolPolicyTests.cs`
- Create: `tests/WprMcp.Tests/SymbolPolicyHttpHandlerTests.cs`
- Modify: `tests/WprMcp.Tests/SymbolServiceTests.cs`

**Interfaces:**

- Consumes: `SymbolPolicyOptions`, requested local paths/origins, DNS results, redirects, and response lengths.
- Produces: `SymbolPolicy.ValidatePath`, `ValidateOrigin`, `ValidateResolvedAddress`; a redirect-disabled `SymbolPolicyHttpHandler`; and `PolicySymbolReaderFactory.Create(SymbolContext, TextWriter)`.

- [ ] **Step 1: Write a failing package-integration spike and policy tests.** The spike constructs a `SymbolReader` with the supported hook in the Child 11A-selected TraceEvent package, requests from a loopback `HttpListener`, and proves the injected handler observes the request only when both the loopback origin and `127.0.0.1/32` are explicitly allowed. Fail the test if the selected package bypasses it; record the exact supported hook in an assertion message rather than silently using global environment state. Set hostile `HTTP_PROXY`/`HTTPS_PROXY` environment variables, `WebRequest.DefaultWebProxy`, default proxy credentials, and a cookie container pointing to a recording endpoint; assert no request/credential/cookie reaches any proxy and compressed responses are not automatically expanded. Policy cases cover remote disabled, empty allowlist, HTTP, credentials, non-default/unlisted ports, redirect to another host, redirect to a private IP without its CIDR, explicitly allowed private CIDR, public DNS followed by private DNS, cancellation while resolving each redirect hop, IPv4/IPv6 loopback/link-local/private/multicast/unspecified addresses, redirect loops, more than five hops, oversized `Content-Length`, oversized chunked bodies, UNC cache paths, delimiters in path entries, and unapproved local roots.

```csharp
[Theory]
[InlineData("127.0.0.1")]
[InlineData("169.254.1.2")]
[InlineData("10.0.0.8")]
[InlineData("::1")]
[InlineData("fe80::1")]
public void ValidateResolvedAddress_DefaultPolicyRejectsNonPublicRanges(string value)
{
    var error = Assert.Throws<SymbolPolicyException>(() =>
        policy.ValidateResolvedAddress(IPAddress.Parse(value)));
    Assert.Equal("symbol_policy_denied", error.Code);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~SymbolPolicyTests|FullyQualifiedName~SymbolPolicyHttpHandlerTests|FullyQualifiedName~SymbolServiceTests"` and require the handler spike to fail before integration exists.**

- [ ] **Step 3: Implement strict parsing, pinned connection, and bounded download.** Normalize IDN hosts, compare scheme/host/effective port against immutable allowed origins, disable automatic redirects, and manually validate each `Location`. Configure the dedicated `SocketsHttpHandler` with `UseProxy=false`, `Proxy=null`, `DefaultProxyCredentials=null`, `Credentials=null`, `UseCookies=false`, and `AutomaticDecompression=DecompressionMethods.None`; never assign a cookie container or use `HttpClient.DefaultProxy`/ambient handlers. Parse CIDRs into canonical masked `SymbolNetworkRange` values at startup. In `ConnectCallback`, call cancellable DNS with the request token, reject ambiguous/unspecified/multicast/broadcast addresses, require every non-public address to be contained by an allowed range, and connect to one validated address while retaining the original host for TLS SNI/certificate checks; on every redirect, revalidate the URI, perform a fresh cancellable resolution, and pin again. Limit to five redirects and `MaxDownloadBytes`; abort before reading when `Content-Length` exceeds it and while copying when a chunked body crosses it. Validate local symbol/cache paths with component containment and no reparse component. If TraceEvent cannot use the handler, make the spike a hard implementation blocker and route symbol fetches through an explicit parent-owned downloader that stores only validated PDB files; never fall back to ambient networking.

- [ ] **Step 4: Run the focused test command. Then run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~Symbol`; require zero direct network calls from tests whose policy rejects before resolution.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/SymbolPolicy.cs src/WprMcp/Core/SymbolPolicyHttpHandler.cs src/WprMcp/Core/PolicySymbolReaderFactory.cs tests/WprMcp.Tests/SymbolPolicyTests.cs tests/WprMcp.Tests/SymbolPolicyHttpHandlerTests.cs tests/WprMcp.Tests/SymbolServiceTests.cs
git commit -m "feat: enforce deny-by-default symbol access"
```

### Task 6: Replace process-wide symbol mutation with immutable contexts

**Files:**

- Create: `src/WprMcp/Core/SymbolContext.cs`
- Create: `src/WprMcp/Core/SymbolContextProvider.cs`
- Modify: `src/WprMcp/Core/SymbolPathDefaults.cs`
- Modify: `src/WprMcp/Core/SymbolService.cs`
- Modify: `src/WprMcp/Analyzers/StackSourceTopN.cs`
- Modify: `src/WprMcp/Tools/SymbolTools.cs`
- Modify: `src/WprMcp/Program.cs`
- Create: `tests/WprMcp.Tests/SymbolContextProviderTests.cs`
- Modify: `tests/WprMcp.Tests/SymbolServiceTests.cs`

**Interfaces:**

- Consumes: validated startup symbol policy and authorized runtime changes.
- Produces: immutable `SymbolContext` snapshots and `ISymbolContextProvider.Capture`, `ReplaceAuthorized`, and `AppendAuthorizedServer`.

- [ ] **Step 1: Write failing isolation tests.** Run two concurrent analyses with disjoint local roots, caches, and allowed hosts; assert each factory receives only its captured values and `_NT_SYMBOL_PATH` remains byte-for-byte unchanged. Assert startup import is rejected atomically if one segment is invalid. Assert `set_symbol_path`/`add_symbol_server` cannot add a local root or origin absent from startup policy and that a rejected call does not change the next snapshot.

```csharp
internal sealed record SymbolContext(
    string SerializedSymbolPath,
    string CacheRoot,
    bool RemoteEnabled,
    IReadOnlySet<SymbolOrigin> AllowedOrigins,
    IReadOnlyList<SymbolNetworkRange> AllowedDestinationNetworks,
    HttpMessageHandler PolicyHandler);

internal interface ISymbolContextProvider
{
    SymbolContext Capture();
    SymbolContext ReplaceAuthorized(string symbolPath);
    SymbolContext AppendAuthorizedServer(Uri origin, string cacheRoot);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~SymbolContextProviderTests|FullyQualifiedName~SymbolServiceTests"` and verify environment/context cross-talk failures.**

- [ ] **Step 3: Implement immutable compare/exchange snapshots.** Parse `_NT_SYMBOL_PATH` exactly once in `Program.cs` only when import is enabled, pass it into the provider, and never call `Environment.SetEnvironmentVariable`. Build a fresh `SymbolReader` from the captured context for each operation or trace backend. Change `StackSourceTopN` and `SymbolService` to accept `SymbolContext`/`PolicySymbolReaderFactory`; remove environment and default-path reads from analyzer code. Runtime symbol tools may narrow or select among startup-authorized entries but cannot expand policy.

- [ ] **Step 4: Run the focused tests, then `rg -n "SetEnvironmentVariable|GetEnvironmentVariable\(\"_NT_SYMBOL_PATH\"" src/WprMcp` and require no runtime match other than the single startup import read. Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~Symbol`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/SymbolContext.cs src/WprMcp/Core/SymbolContextProvider.cs src/WprMcp/Core/SymbolPathDefaults.cs src/WprMcp/Core/SymbolService.cs src/WprMcp/Analyzers/StackSourceTopN.cs src/WprMcp/Tools/SymbolTools.cs src/WprMcp/Program.cs tests/WprMcp.Tests/SymbolContextProviderTests.cs tests/WprMcp.Tests/SymbolServiceTests.cs
git commit -m "refactor: scope symbol state to immutable contexts"
```

### Task 7: Register opaque trace IDs and resolve compatibility references

**Files:**

- Create: `src/WprMcp/Core/TraceRegistry.cs`
- Create: `src/WprMcp/Core/TraceLease.cs`
- Create: `src/WprMcp/Core/TraceReferenceResolver.cs`
- Create: `src/WprMcp/Core/InProcessTraceBackend.cs`
- Modify: `src/WprMcp/Core/TraceCache.cs`
- Modify: `src/WprMcp/Program.cs`
- Create: `tests/WprMcp.Tests/TraceRegistryTests.cs`
- Create: `tests/WprMcp.Tests/TraceReferenceResolverTests.cs`
- Create: `tests/WprMcp.Tests/PolicyAliasIntegrationTests.cs`

**Interfaces:**

- Consumes: `TraceAccessPolicy`, `ITraceArtifactStore`, `TraceReferenceMode`, `ISymbolContextProvider`, and values already rewritten by Child 5 `IToolArgumentRewriter`/`ITypedAliasRegistry`.
- Produces: the stable `ITraceRegistry`, `TraceLease`, `ITraceBackend`, and `ITraceReferenceResolver` contracts declared above; trace IDs matching `^trc_[0-9a-f]{32}$` from 16 cryptographically random bytes encoded as lower-case hex.

- [ ] **Step 1: Write failing registry/resolver and alias-integration tests.** Cover ID format and collision retry; two explicit registrations of the same immutable artifact returning distinct trace IDs backed by one artifact key; acquisition by each ID; unknown valid ID; a path-shaped token in ID-only mode; malformed reserved-prefix values `trc_`, `TRC_bad`, `trc_0123.etl`, and `TrC_0123456789abcdef0123456789abcdeg` in compatibility mode; compatibility raw path warning; source replacement after registration; and disposing every returned lease. Drive Child 5's real `IToolArgumentRewriter` with allowed `TracePath`/`SymbolPath` aliases and assert their rewritten values are revalidated. Cover an alias resolving outside the trace root, an ID-only alias resolving to a raw path, a symbol alias adding an unapproved root/origin/cache, and Child 5 unknown/wrong-kind/evicted failures; every rejection causes zero filesystem/network side effects. Add a filesystem spy proving ID-only resolution and every case-insensitive `trc_`-prefixed value perform no path lookup, file open, artifact creation, or symbol/network action.

```csharp
[Fact]
public async Task ResolveQueryAsync_UnknownTraceToken_NeverFallsBackToPath()
{
    var error = await Assert.ThrowsAsync<TraceNotLoadedException>(() =>
        resolver.ResolveQueryAsync("trc_0123456789abcdef0123456789abcdef", default).AsTask());

    Assert.Equal(0, traceAccessPolicy.OpenCount);
    Assert.Equal("trace_not_loaded", error.Code);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceRegistryTests|FullyQualifiedName~TraceReferenceResolverTests"` and verify missing registry behavior.**

- [ ] **Step 3: Implement the provisional registry and resolver.** Generate IDs with `RandomNumberGenerator.Fill`; every explicit `LoadAsync` registration gets a new ID even when the immutable artifact object is reused. Register only a validated/published artifact. The provisional `InProcessTraceBackend` opens only `TraceDescriptor.ArtifactPath`. Its lease release is idempotent via `Interlocked.Exchange`; no query receives a naked `TraceLog`. `ResolveQueryAsync` first reserves `StartsWith("trc_", StringComparison.OrdinalIgnoreCase)`, then validates canonical lower-case grammar and looks up the ID; only a non-reserved value in compatibility mode can reach `LoadAsync`. Use stable warning code `raw_trace_path_deprecated`. Do not expose canonical source or artifact paths in trace IDs or user-facing errors.

- [ ] **Step 4: Run the focused tests and `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~PolicyAliasIntegrationTests|FullyQualifiedName~TraceCacheTests|FullyQualifiedName~TraceEventSmokeTests|FullyQualifiedName~PrivacyRedactorTests"`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/TraceLease.cs src/WprMcp/Core/TraceReferenceResolver.cs src/WprMcp/Core/InProcessTraceBackend.cs src/WprMcp/Core/TraceCache.cs src/WprMcp/Program.cs tests/WprMcp.Tests/TraceRegistryTests.cs tests/WprMcp.Tests/TraceReferenceResolverTests.cs tests/WprMcp.Tests/PolicyAliasIntegrationTests.cs
git commit -m "feat: register and resolve opaque trace ids"
```

### Task 8: Migrate every tool, CLI path, installer, and annotation

**Files:**

- Modify: `src/WprMcp/Tools/AlpcTools.cs`
- Modify: `src/WprMcp/Tools/ClrTools.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Tools/DiagnoseTools.cs`
- Modify: `src/WprMcp/Tools/GenericProviderTools.cs`
- Modify: `src/WprMcp/Tools/HardFaultTools.cs`
- Modify: `src/WprMcp/Tools/HeapTools.cs`
- Modify: `src/WprMcp/Tools/ImageLoadTools.cs`
- Modify: `src/WprMcp/Tools/InterruptTools.cs`
- Modify: `src/WprMcp/Tools/IoTools.cs`
- Modify: `src/WprMcp/Tools/MarkerTools.cs`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Modify: `src/WprMcp/Tools/NetIoTools.cs`
- Modify: `src/WprMcp/Tools/ReadyThreadTools.cs`
- Modify: `src/WprMcp/Tools/RegistryTools.cs`
- Modify: `src/WprMcp/Tools/SecurityTools.cs`
- Modify: `src/WprMcp/Tools/SymbolTools.cs`
- Modify: `src/WprMcp/Tools/VirtualMemoryTools.cs`
- Modify: `src/WprMcp/Tools/WaitTools.cs`
- Modify: `src/WprMcp/Cli/CliRunner.cs`
- Modify: `scripts/install.ps1`
- Modify: `scripts/install.sh`
- Modify: `scripts/setup.ps1`
- Modify: `scripts/setup.sh`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Modify: `tests/WprMcp.Tests/McpSdkSurfaceTests.cs`
- Modify: `tests/WprMcp.Tests/MetaToolsTests.cs`
- Modify: `tests/WprMcp.Tests/CliRunnerTests.cs`
- Modify: `tests/WprMcp.Tests/InstallerScriptTests.cs`
- Create: `tests/WprMcp.Tests/TraceReferenceSurfaceTests.cs`

**Interfaces:**

- Consumes: `ITraceReferenceResolver`; `load_trace(rawPath)` is the only public raw-path load entry in ID-only mode.
- Produces: query methods whose existing public trace parameter remains `string path`, whose internal variable is `traceReference`, whose body uses `await using ResolvedTraceReference.Lease`, and whose output/provenance carries `TraceId` instead of a canonical path.

- [ ] **Step 1: Write failing reflection and behavior tests.** Reflect every `[McpServerTool]` method and fail when a query injects `TraceCache`, calls `TraceCache.Get`, or changes its existing public trace parameter from `path`; assert the parameter description documents compatibility versus ID-only interpretation. Call representative metadata, CPU, wait, symbol, and CLI query flows in both modes. Assert the existing raw-path CLI/MCP call still succeeds through policy in compatibility mode and carries `raw_trace_path_deprecated`, while the same call is rejected before filesystem access in ID-only. Assert valid unknown IDs and malformed case-insensitive `trc_` prefixes never reach the filesystem. Assert ID-only annotations are `ReadOnlyHint=true`; compatibility queries conservatively advertise `ReadOnlyHint=false` because an allowed raw path can trigger load/conversion. Assert the warning appears only on path fallback. Installer tests require a default `%USERPROFILE%\Documents\WprTraces`/`$HOME/Documents/WprTraces` trace root, no default public symbol server, and an explicit opt-in switch that adds Microsoft symbols to both the enable flag and allowlist.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceReferenceSurfaceTests|FullyQualifiedName~McpSdkSurfaceTests|FullyQualifiedName~MetaToolsTests|FullyQualifiedName~CliRunnerTests|FullyQualifiedName~InstallerScriptTests"` and verify it lists every unmigrated tool.**

- [ ] **Step 3: Migrate the 19 tool files and CLI.** Use this exact lifetime in every query body and pass `lease.Backend.Trace` only to in-process analyzers; this expression is replaced by Child 8's operation backend and must not spread into new helpers.

```csharp
string traceReference = path;
ResolvedTraceReference resolved = await _traceReferences
    .ResolveQueryAsync(traceReference, cancellationToken)
    .ConfigureAwait(false);
await using TraceLease lease = resolved.Lease;
ToolResult result = Analyze(lease.Backend.Trace, arguments);
return result.WithTraceId(lease.Descriptor.TraceId)
             .WithWarnings(resolved.Warnings);
```

`load_trace` calls `LoadAsync`, returns the new `TraceId`, and never returns source/artifact paths under `paths` or `strict` privacy. Preserve existing raw-path CLI and MCP query syntax in compatibility mode, but route it through the same `TraceAccessPolicy -> TraceArtifactStore -> TraceRegistry` pipeline and emit the same migration warning. ID-only rejects raw query paths before filesystem access. Removing raw-query syntax or renaming `path` is a next-major change, not part of this task.

- [ ] **Step 4: Change installer defaults and documentation.** Add `-TraceRoot`/`--trace-root` and `-EnableMicrosoftSymbols`/`--enable-microsoft-symbols`; construct `--enable-remote-symbols --allow-symbol-origin https://msdl.microsoft.com` only when explicitly selected. Keep custom symbol origins explicit. Document that compatibility is transitional and that a query by ID has no implicit filesystem write.

- [ ] **Step 5: Run the focused command, then `rg -n "_cache\.Get\(|TraceCache" src/WprMcp/Tools src/WprMcp/Cli` and require no matches. Run `dotnet test WprMcp.sln -c Release`, then `dotnet run --project src/WprMcp/WprMcp.csproj -- --trace-root tests/WprMcp.Tests/fixtures --artifact-root artifacts/policy-smoke --trace-reference-mode id-only --version`; require all tests to pass and the version command to exit 0 without creating a source-directory sidecar.**

- [ ] **Step 6: Commit.**

```powershell
git add src/WprMcp/Tools/AlpcTools.cs src/WprMcp/Tools/ClrTools.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Tools/GenericProviderTools.cs src/WprMcp/Tools/HardFaultTools.cs src/WprMcp/Tools/HeapTools.cs src/WprMcp/Tools/ImageLoadTools.cs src/WprMcp/Tools/InterruptTools.cs src/WprMcp/Tools/IoTools.cs src/WprMcp/Tools/MarkerTools.cs src/WprMcp/Tools/MetaTools.cs src/WprMcp/Tools/NetIoTools.cs src/WprMcp/Tools/ReadyThreadTools.cs src/WprMcp/Tools/RegistryTools.cs src/WprMcp/Tools/SecurityTools.cs src/WprMcp/Tools/SymbolTools.cs src/WprMcp/Tools/VirtualMemoryTools.cs src/WprMcp/Tools/WaitTools.cs src/WprMcp/Cli/CliRunner.cs scripts/install.ps1 scripts/install.sh scripts/setup.ps1 scripts/setup.sh README.md README.zh-CN.md tests/WprMcp.Tests/McpSdkSurfaceTests.cs tests/WprMcp.Tests/MetaToolsTests.cs tests/WprMcp.Tests/CliRunnerTests.cs tests/WprMcp.Tests/InstallerScriptTests.cs tests/WprMcp.Tests/TraceReferenceSurfaceTests.cs
git commit -m "feat: migrate tools to policy-bound trace ids"
```

## Acceptance Gate and Handoff

Run from repository root on Windows:

```powershell
dotnet restore WprMcp.sln
dotnet build WprMcp.sln -c Release --no-restore
dotnet test WprMcp.sln -c Release --no-build
rg -n "_cache\.Get\(|SetEnvironmentVariable|\.etlx\.new" src/WprMcp
git status --short
```

The gate passes only when rejected paths/settings caused zero file/network side effects; source replacement cannot affect an existing trace ID; two processes share one immutable artifact without a source sibling; failed/cancelled conversion retries; symbol contexts do not cross-talk; redirects remain inside approved public HTTPS origins; all query tools use a lease; ID-only queries never interpret paths; compatibility calls disclose their side effect; and installers start with a narrow trace root and remote symbols disabled.

Child 7 may modify `TraceRegistry.cs`, `TraceLease.cs`, `InProcessTraceBackend.cs`, and `Program.cs`, but must preserve `TraceDescriptor`, `ITraceRegistry`, `ITraceReferenceResolver`, the trace-ID grammar, artifact identity, and caller-side `await using`. Child 8 may change `ITraceBackend` from direct `TraceLog` exposure to typed `ExecuteAsync`; it must keep the registry/lease/reference contracts and make secure-default parsing, analysis, and symbol resolution worker-only. Child 9 owns removal of the global test parallelism guard and the final hostile stdio matrix.

## Mandatory Spikes and Stop Conditions

- Before Task 3 is considered green, prove the pinned TraceEvent API can convert from a private snapshot to an explicitly named output. If it cannot, wrap the supported conversion API in a private working directory and verify every generated path stays under that directory.
- Before Task 5 is considered green, prove the pinned TraceEvent symbol stack honors the policy transport. If it bypasses the injected handler, do not ship remote symbols; implement the explicit policy downloader or block remote enablement with a startup error.
- Before enabling compatibility by default, verify actual MCP annotations from `tools/list` on the Child 11-selected SDK. If annotation selection cannot vary by startup profile, advertise the conservative `ReadOnlyHint=false` for query tools until compatibility is removed.
- Do not weaken reparse, namespace, resolved-address, redirect, origin, or path-root checks to make platform tests pass. A platform inability to prove same-object consumption or policy-bound network access is a production blocker for the affected mode.
