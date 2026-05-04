# Recommended WPR capture profile

For best coverage of all 6 PoC tools, capture with the included
`tests/WprMcp.Tests/fixtures/MmapCapture.wprp` (or copy it elsewhere). It enables:

| Keyword | Used by |
|---|---|
| ProcessThread, Loader | All tools |
| HardFaults | hard_fault_by_file / hard_fault_top_stacks (REQUIRED — default profiles omit) |
| MemoryInfo | hard_fault_by_file |
| FileIO, FileIOInit | file_io_top_files |

For CPU-only focus, `wpr.exe -start CPU -filemode` is sufficient (no hard-fault analysis).

## Capture commands

```powershell
wpr.exe -start MmapCapture.wprp -filemode
# … workload …
wpr.exe -stop my_capture.etl
```

## Trace size

Verbose profiles can produce > 1 GB / minute. Recommend < 60s captures unless tracing a slow event.
