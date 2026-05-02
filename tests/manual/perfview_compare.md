# PerfView CLI sanity check

Ground truth: PerfView v3.x `CPUStacks` command on the same fixture.

| Trace | Tool | PerfView top-10 | WprMcp top-10 | Match |
|---|---|---|---|---|
| small_cpu.etl | cpu_top_functions | (paste 10 names + sample counts) | (paste 10 names + sample counts) | ✅ ≥7 overlap / ❌ |

Re-run after every change to `CpuAnalysis`. Pass criterion: ≥ 7/10 overlap and per-row sample count within ±10%.

## How to run

1. Install PerfView from <https://github.com/microsoft/perfview/releases> (single .exe, drop in PATH).
2. Run on the same fixture:
   ```powershell
   PerfView.exe -nogui -OutputFile=small_cpu_topcpu.txt CPUStacks tests\WprMcp.Tests\fixtures\small_cpu.etl
   ```
3. Open `small_cpu_topcpu.txt`, copy the top-10 inclusive list.
4. Run our tool against the same fixture (ad-hoc MCP smoke test):
   ```powershell
   dotnet run --project src/WprMcp -- --version  # sanity build
   $req = '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0"}}}'
   $req2 = '{"jsonrpc":"2.0","method":"notifications/initialized"}'
   $req3 = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cpu_top_functions","arguments":{"path":"tests/WprMcp.Tests/fixtures/small_cpu.etl","top":10}}}'
   "$req`n$req2`n$req3" | dotnet run --project src/WprMcp --no-build
   ```
5. Capture both lists in the table above.

If overlap < 7/10 or counts > ±10%: the analyzer is wrong — return to Task 10.
