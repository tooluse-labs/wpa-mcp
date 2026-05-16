# Recommended WPR capture profile

For best coverage of all 6 PoC tools, capture with the included
`tests/WprMcp.Tests/fixtures/MmapCapture.wprp` (or copy it elsewhere). It enables:

| Keyword | Used by |
|---|---|
| ProcessThread, Loader | All tools |
| HardFaults | hard_fault_by_file / hard_fault_top_stacks (REQUIRED — default profiles omit) |
| MemoryInfo | hard_fault_by_file |
| FileIO, FileIOInit | file_io_top_files |

For memory resource views (`memory_resource_analysis`), use
`tests/WprMcp.Tests/fixtures/MemoryCapture.wprp`. It enables:

| Keyword | Used by |
|---|---|
| Memory, MemoryInfo, MemoryInfoWS | working set, commit, derived private bytes, and system free/modified memory samples |
| Handle | observed handle create/close/duplicate deltas by process |
| Pool | future paged/non-paged pool views; current `memory_resource_analysis` warns that pool counters are not emitted yet |
| VirtualAllocation | pairs resource snapshots with virtual-allocation stack analysis |

For CPU-only focus, `wpr.exe -start CPU -filemode` is sufficient (no hard-fault analysis).

## Capture commands

```powershell
wpr.exe -start MmapCapture.wprp -filemode
# … workload …
wpr.exe -stop my_capture.etl
```

```powershell
wpr.exe -start MemoryCapture.wprp!MemoryMcp -filemode
# … workload …
wpr.exe -stop my_memory_capture.etl
```

## Trace size

Verbose profiles can produce > 1 GB / minute. Recommend < 60s captures unless tracing a slow event.
