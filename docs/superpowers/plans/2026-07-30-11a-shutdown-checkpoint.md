# Child 11A shutdown checkpoint — 2026-07-30

This checkpoint records an intentionally incomplete implementation state so work can resume after shutdown without treating unverified WIP as a completed platform decision.

## Scope guard

- Active scope is Child 11A Tasks 1–4 only: platform candidate evidence, candidate selection, exact SDK/NuGet dependency locks, and CI action pinning.
- Do not start Child 11B release/default-protocol/documentation work.
- After Tasks 1–4 pass review, move directly to the approved P0 correctness order: shared time-window and instance contracts, Wait/TID correctness, then slow-startup window correctness.

## Repository state

- Worktree: `D:\wpa-mcp\.worktrees\11a-platform-gate`
- Branch: `feature/11a-platform-gate`
- Last independently approved runtime-evidence checkpoint: `7cc0be6`
- Shutdown WIP checkpoint: `bb3d151` (`wip(platform): checkpoint remaining candidate probes`)
- Main worktree was not modified by this work.

The WIP commit is deliberately not a Task 1 completion commit. It contains the remaining candidate-probe changes in:

- `scripts/Test-PlatformCandidate.ps1`
- `tests/WprMcp.Tests/PlatformDecisionTests.cs`

## Evidence completed before shutdown

- NuGet verified-cache behavior: RED 2/2, then GREEN 2/2.
- Golden TraceEvent reads: semantic regression GREEN 1/1; the real probe opened temporary copies of all six required ETL fixtures.
- Observed event counts were 71, 188983, 117247, 2000741, 236255, and 209672 for the six matrix fixtures in declared order.
- Native layout/load: semantic GREEN 2/2 and real native-load probe GREEN 1/1. Evidence requires exact `amd64/msdia140.dll` and `amd64/KernelTraceControl.dll` paths.
- Windows DIA: semantic GREEN 2/2 and real DIA probe GREEN 1/1 after command-quoting fixes. It enumerated 3243 functions; `PlatformDiaSentinel` RVA and resolved start RVA were both 4096.
- At shutdown, no related `dotnet`, `testhost`, `sdkcandidateprobe`, `VBCSCompiler`, or MSBuild process was visible in the controller's final process check.

These results apply to their focused slices only. No final Task 1 suite was run after the last architecture/aggregate tests were added.

## Known unverified WIP

The final edit before shutdown added these tests but did not run them:

- `CandidateResult_RejectsDisconnectedArchitectureObservationEvenWhenArtifactHashIsUpdated`
- `CandidateResult_RejectsEmptySuccessfulSdkAndSchemaAggregateArtifactMaps`

Before running the architecture RED, remove the two assignments that copy the evidence hash into `probe["stdoutSha256"]` and `probe["cases"][0]["stdoutSha256"]`, or bind the fixture to a real stdout log. As committed, the test may fail at generic hash membership instead of proving the intended architecture semantic gap.

Still required for Task 1:

1. Run and preserve the architecture RED for the intended reason.
2. Implement production architecture evidence and aggregate/result semantic validation.
3. Run and preserve the SDK/schema empty-map RED, then bind aggregate artifacts to the union of their case artifacts.
4. Complete NuGet/restore full-result semantic validation.
5. Run focused tests, a fresh complete `PlatformDecisionTests` run, process enumeration, and `git diff --check`.
6. Update the ignored SDD report/ledger, create the final Task 1 commit, and request a fresh full Task 1 review from base `ea69f60` through the final Task 1 head.

Tasks 2–4 have not started. Task 2 must execute all three immutable candidates before selecting one; no candidate is currently selected.

## Resume procedure

```powershell
Set-Location D:\wpa-mcp\.worktrees\11a-platform-gate
git status --short
git log --oneline -12
git show --stat --oneline bb3d151
```

First correct the architecture mutation fixture noted above. Then run its focused RED with serialized build settings:

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release `
  --filter FullyQualifiedName~CandidateResult_RejectsDisconnectedArchitectureObservationEvenWhenArtifactHashIsUpdated `
  -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:NuGetAudit=false
```

Use the same serialization settings for subsequent tests. A prior unconstrained command on this 192-core host created idle build nodes, so do not omit them. Never broad-kill processes; attribute exact PIDs and start times before terminating a hung child.

After implementing the remaining semantic validation, run at minimum:

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release `
  --filter FullyQualifiedName~PlatformDecisionTests `
  -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:NuGetAudit=false
git diff --check
```

Do not mark Task 1 or the aggregate goal complete until the fresh full review approves it. Do not freeze a platform until Task 2 has produced complete passing evidence for the chosen candidate and recorded explicit rejection reasons for the others.
