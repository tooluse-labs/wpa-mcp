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
| Pool | observed paged/non-paged pool allocation/free deltas; `memory_resource_analysis` reports captured-window deltas, not absolute current counters |

`MemoryCapture.wprp` intentionally avoids stackwalks and virtual-allocation
stack capture so the fixture can stay small. Use a stack-enabled profile when
`virtual_alloc_top_stacks` call-chain evidence is required.

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
