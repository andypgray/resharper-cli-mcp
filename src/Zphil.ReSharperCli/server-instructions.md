This server exposes JetBrains' ReSharper command-line tools over MCP. Two tools:

- `resharper_inspect` — run ReSharper InspectCode and return the issues it finds, grouped by file. Read-only. Run it before editing to see existing issues, or after to catch regressions. Scope with the `files` glob (Ant-style, e.g. `src/**/*.cs`) and raise `severity` (`Suggestion`/`Warning`/`Error`) to cut noise — `Error` is compilation errors only, not high-priority warnings.
- `resharper_cleanup` — run ReSharper CleanupCode to reformat and normalize files in place. Mutating. Call it **once**, at the end of a task, with every modified `.cs`/`.razor` file batched into a single call. `files` are solution-relative or absolute (a missing non-wildcard path fails fast). `profile` defaults to full cleanup; pass a custom profile from the solution's `.sln.DotSettings` (e.g. `Custom: No Reordering`) to narrow what it touches.

Cleanup is cosmetic: it fixes formatting, unused usings, `var` style, modifier order, redundant qualifiers, brace style, and similar. Do not spend edit effort on those by hand — write correct logic and naming and let cleanup do the polish. After a cleanup there is no need to re-inspect or re-build to check it: it never changes behavior.

Before changing what ReSharper enforces — choosing a settings file or cleanup profile, suppressing a rule, or stopping cleanup from normalizing a deliberate style — read the `resharper://guides/configuration` resource: inspect and cleanup obey different settings axes.

Solution discovery: the server auto-detects a single `.sln`/`.slnx` in its working directory. If that directory has zero or several, set `JB_SOLUTION_PATH` or pass `solutionPath` on the call (which overrides both).

The first inspection or cleanup on a solution is slow while ReSharper warms its caches; later calls are fast. Each run has a 5-minute timeout.

This is an unofficial wrapper. It is not affiliated with or endorsed by JetBrains.
