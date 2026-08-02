# Symbol preparation recipes

The current secure profile uses an explicit, local-only symbol lifecycle. It
does not read `_NT_SYMBOL_PATH`, contact a symbol server, inspect a trace's
directory, or search arbitrary disk locations.

Keep these evidence states separate:

1. **Trace PDB identity** — PDB name, GUID, and age carried by trace metadata.
2. **Local candidate** — a file found at an allowed shape below an approved root.
3. **Verified readiness** — the candidate was opened, matched the complete
   identity, copied into the private verified store, and pinned.
4. **Observed frame resolution** — a stack query actually attempted and resolved
   code-frame names.

`prepare_symbols` can establish state 3. It intentionally reports state 4 as
unmeasured. The current build has no context-bound TraceEvent adapter for state
4, so resolution remains a declared gap. Null/unmeasured is not 0%.

## Configure the startup policy

Configure one or more absolute local candidate roots and a disjoint private
verified store:

```powershell
wpa-mcp.exe --symbol-local-root "C:\Symbols" `
  --symbol-store-root "$env:LOCALAPPDATA\WpaMcp\symbol-store"
```

Repeat `--symbol-local-root` to approve more roots. Equivalent environment
variables are:

```text
WPAMCP_SYMBOL_LOCAL_ROOTS=C:\Symbols;D:\BuildSymbols
WPAMCP_SYMBOL_STORE_ROOT=C:\Users\me\AppData\Local\WpaMcp\symbol-store
```

If any candidate root is configured, the store root is required. Roots and the
store must be absolute, local, and mutually disjoint; UNC, device,
alternate-stream, and reparse traversal are denied. The PowerShell installer
defaults to:

```text
candidate root: %LocalAppData%\WpaMcp\symbol-candidates
verified store: %LocalAppData%\WpaMcp\symbol-store
```

## Place candidates

For each trace identity, preparation probes only these shapes:

```text
<root>\<pdbName>
<root>\<pdbName>\<GUIDAge>\<pdbName>
```

Acquire Microsoft, Chromium, vendor, or private PDBs outside wpa-mcp, then copy
them under an approved root. The server itself performs no remote fetch. A
recognizable filename or symbol-store location is not readiness: the PDB must
be opened and its name/GUID/age must exactly match the trace identity.

For locally built binaries:

- .NET/C#: enable portable or Windows PDB output and keep the PDB from the exact
  deployed build.
- MSVC C++: use `/Zi` and `/DEBUG:FULL`, including Release builds.
- Re-linking changes the identity; an older same-name PDB does not match.

## Run the lifecycle

Ask the client to perform the following sequence:

```text
1. load_trace(path) -> TraceId
2. prepare_symbols(TraceId) -> SymbolContextId
3. inspect verified readiness without calling it frame resolution
```

The SymbolContextId is immutable and bound to the principal, trace generation,
policy, resolver, privacy/contract profile, module identities, and verified
artifacts. A query must supply it explicitly. There is no ambient fallback to
the process environment, trace directory, arbitrary disk, or a remote server.
In the current build, `resolveSymbols=true` fails closed with
`symbol_resolution_unavailable` and detail
`context_bound_frame_resolution_unavailable`; it never invokes the legacy
ambient resolver.

## Interpret the result

- `ModulesWithPdbIdentity` is trace metadata coverage, not symbol resolution.
- `ModulesWithVerifiedSymbolArtifact` / readiness describes exact local artifact
  verification, not function-name lookup.
- Preparation leaves frame counts/rate unmeasured by design.
- `symbols.frame_resolution.measured` remains a declared gap until a
  context-bound lookup implementation and real-trace evidence are admitted.
- An external/offline frame-resolution measurement is not evidence that this
  MCP runtime performed context-bound lookup.
- Candidate failure or absence must remain not-ready/unknown; it cannot be
  converted into a 0% measured frame-resolution claim.

## Historical interface warning

`set_symbol_path`, `add_symbol_server`, and `diagnose_symbols` belong to the old
0.2-era interface and are not in the current 60-tool Active Catalog.
`--symbol-path` is rejected in the secure profile. Do not instruct a current
client to configure `_NT_SYMBOL_PATH` or fetch a remote symbol server through
wpa-mcp.

See [`ARCHITECTURE.md`](ARCHITECTURE.md),
[`CONTRACT_MIGRATION.md`](CONTRACT_MIGRATION.md), and
[`CLIENT_COMPATIBILITY.md`](CLIENT_COMPATIBILITY.md) for lifecycle and evidence
contract details.
