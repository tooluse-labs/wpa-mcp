# WprMcp to WpaMcp Naming Migration Design

**Date:** 2026-07-31
**Status:** Approved scope; ready for implementation planning
**Target:** Make the repository's project-owned name consistently `wpa` while preserving Windows Performance Recorder terminology.

## Goal

The public product is already named `wpa-mcp`, but source, test, build, CI, and runtime internals still use `WprMcp` and `WPRMCP`. Rename those project-owned identifiers to `WpaMcp` and `WPAMCP`, including physical directories and files.

## Scope

The migration changes these project-owned names:

- solution: `WprMcp.sln` to `WpaMcp.sln`;
- project folders/files: `src/WprMcp/WprMcp.csproj` and `tests/WprMcp.Tests/WprMcp.Tests.csproj` to their `WpaMcp` equivalents;
- C# namespaces, `using` directives, test namespaces, assembly names, project references, solution display names, and `InternalsVisibleTo` values;
- MSBuild properties, test assertions, CI action directory names, scripts, fixture paths, installer references, output file names, and documentation paths;
- project-owned environment variables (`WPRMCP_*` to `WPAMCP_*`) and default app-data locations (`WprMcp` to `WpaMcp`);
- human-facing version and CLI strings that identify the server as `WprMcp`.

The migration deliberately does **not** change these external Windows Performance Recorder terms:

- `wpr.exe` commands;
- `.wprp` profile filenames/extensions and their XML content;
- prose where `WPR` means Windows Performance Recorder, such as "WPR capture profile";
- test class names that specifically describe WPR profile recipes, unless they are project-branding identifiers rather than the external tool concept.

## Options considered

1. **Rename paths only.** Low churn but leaves namespaces, assembly names, settings, CI, and documentation inconsistent. Rejected.
2. **Clean project-wide migration (recommended).** Rename every project-owned identifier and physical path in one change, while retaining true WPR tool terminology. This produces one coherent codebase and matches the existing public `wpa-mcp` branding.
3. **Introduce aliases and deprecate WprMcp gradually.** Could preserve environment-variable and app-data compatibility, but adds lasting duplicate names and behavior not requested for an internal source rename. Rejected unless a downstream compatibility requirement is later identified.

## Design

Use option 2. Apply case-preserving mappings:

| Old | New |
|---|---|
| `WprMcp` | `WpaMcp` |
| `wprmcp` | `wpamcp` |
| `WPRMCP` | `WPAMCP` |
| `WprMcp.sln` | `WpaMcp.sln` |
| `src/WprMcp` | `src/WpaMcp` |
| `tests/WprMcp.Tests` | `tests/WpaMcp.Tests` |
| `.github/actions/setup-wprmcp` | `.github/actions/setup-wpamcp` |

On Windows, each directory/file rename is performed through an unambiguous temporary name when needed so case-insensitive filesystem behavior cannot hide a path update. Repository-wide textual replacement is limited to project-owned terms. Searches for standalone `wpr`, `WPR`, and `.wprp` are reviewed separately rather than globally replaced.

## Compatibility and risks

This is intentionally a breaking change for users who rely on `WPRMCP_*` environment variables or the old `%LocalAppData%\\WprMcp` cache directory. Existing normal install paths and the released executable name already use `wpa-mcp`, so they remain stable. The implementation will document the new variable/cache names but will not add aliases without a specific compatibility requirement.

Physical path changes affect contributors' local build commands and CI. All checked-in paths and references will be updated atomically. No external `wpr.exe` invocation or WPR profile format is changed.

## Validation

1. Add/update focused tests for renamed runtime identifiers where assertion coverage exists (version output, environment variables, default cache location, and path-based governance checks).
2. Restore/build/test the renamed solution with locked dependencies.
3. Search tracked files and tracked paths for residual project-owned `WprMcp`, `wprmcp`, and `WPRMCP` terms.
4. Inspect retained `wpr.exe`, `.wprp`, and WPR prose occurrences to confirm they are intentional external-tool references.
5. Verify git detects the intended file and directory moves, with no accidental fixture or generated-artifact churn.
