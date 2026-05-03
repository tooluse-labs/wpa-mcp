# Symbol resolution recipes

`_NT_SYMBOL_PATH` accepts semicolon-separated entries. Each `SRV*<cache>*<url>` entry is a symbol server with a local cache; bare paths point at folders containing PDBs.

## Microsoft system symbols (always recommended)

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

Resolves `ntoskrnl`, `ntdll`, `kernelbase`, `fltmgr`, `wdfilter`, and the rest of the Windows public surface.

## + Chromium-family browsers (Chrome, Edge, Brave, ...)

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com
```

Public Chromium PDBs cover official builds of any browser using the Chromium symbol server.

## + Private vendor symbol server (corporate / internal)

```
…above… ;SRV*C:\Symbols*https://your-internal-symsrv.example.com/symbols
```

Replace with your team's symbol server URL. May require VPN.

## + Local dev build PDBs

```
…above… ;C:\path\to\out\Default
```

Bare-folder entries (no `SRV*` prefix) are scanned recursively for PDB matches by signature.

## Setting at runtime

```
> set_symbol_path SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols false
> add_symbol_server https://chromium-browser-symsrv.commondatastorage.googleapis.com
> diagnose_symbols C:\my\trace.etl
```

## Cache directory

`WprMcp` defaults to `%LocalAppData%\WprMcp\Symbols` (separate from PerfView's `C:\Symbols` to avoid collision). Override per-server in the recipes above.
