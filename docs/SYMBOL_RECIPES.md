# Symbol resolution recipes

`_NT_SYMBOL_PATH` accepts semicolon-separated entries. Each `SRV*<cache>*<url>` entry is a symbol server with a local cache.

## Microsoft system symbols only

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

## + Chromium public symbols (official Chromium / Edge builds)

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com
```

## + Quark internal server (VPN required)

```
…above… ;SRV*C:\Symbols*<your-internal-symsrv-url>
```

## + Local dev build (out\Default PDBs)

```
…above… ;C:\quark\out\Default
```

## Setting at runtime

```
> set_symbol_path SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols false
> add_symbol_server https://chromium-browser-symsrv.commondatastorage.googleapis.com
> diagnose_symbols C:\my\trace.etl
```

## Cache directory

`WprMcp` defaults to `%LocalAppData%\WprMcp\Symbols` (separate from PerfView's `C:\Symbols` to avoid collision). Override per-server in the recipes above.
