# Interrupt Missing-Stack Time Validation

Manual end-to-end validation for the `interrupt_top_stacks` warning gate that
uses missing-stack interrupt time instead of missing-stack event count.

The committed repository must not include private WPR traces. The local ETL used
for this run was generated under `tests/manual/` from real DPC/ISR events relogged
out of a private WPR trace, and remains ignored by git:

```powershell
dotnet run --project tools/interruptfixture -c Release --no-build -- make-mixed `
  "C:\Users\admin3\Documents\WPR Files\LAPTOP-NL4LGTQH.08-11-2025.15-40-35.etl" `
  "tests\manual\wpa-mcp-interrupt-mixed.etl" 10 1 5000
```

Generation output:

```text
selected count=11 stackCount=10 noStackCount=1 totalUs=1969 stackUs=11 noStackUs=1958
kept=38271 dropped=15754713 outBytes=939942
```

After relogging, TraceEvent preserved 11 DPC/ISR events in the local ETL. Seven
still have stacks and four do not, so the old count gate would not warn:

```powershell
dotnet run --project tools/interruptfixture -c Release --no-build -- scan `
  "tests\manual\wpa-mcp-interrupt-mixed.etl" 100
```

```text
total count=11 stackCount=7 noStackCount=4 totalUs=1969 stackUs=11 noStackUs=1958
```

The analyzer now warns because missing-stack interrupt time dominates the metric
being ranked:

```powershell
dotnet run --project src\WprMcp -c Release --no-restore -- --interrupt-top-stacks `
  "tests\manual\wpa-mcp-interrupt-mixed.etl" 5
```

Relevant response fields:

```text
TotalUs: 1969
TotalCount: 11
Warnings[0]: 1958 of 1969 us across 4 of 11 DPC/ISR events did not carry call stacks; ...
```

This is the intended regression shape: missing-stack event count is below 50%,
but missing-stack interrupt time is above 50%.
