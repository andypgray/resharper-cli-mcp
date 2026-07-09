This server exposes JetBrains' ReSharper command-line tools over MCP. Two tools:

- `resharper_inspect` — run ReSharper InspectCode on the solution and return the issues it finds (file, line, severity, rule ID, message), grouped by file. Read-only. Run it before editing to see existing issues, or after to catch regressions. Scope it with the `files` glob (Ant-style, e.g. `src/**/*.cs`) to what you changed, and raise `severity` (`Suggestion`/`Warning`/`Error`) to cut noise — note `Error` means compilation errors only, not high-priority warnings.
- `resharper_cleanup` — run ReSharper CleanupCode to reformat and normalize files in place. Mutating. Call it **once**, at the end of a task, with every modified `.cs`/`.razor` file batched into a single call. `files` are solution-relative or absolute (a missing non-wildcard path fails fast; wildcards pass to jb unvalidated). `profile` defaults to full cleanup; pass a custom profile from the solution's `.sln.DotSettings` (e.g. `Custom: No Reordering`) to narrow what it touches.

Cleanup is cosmetic: it fixes formatting, unused usings, `var` style, modifier order, redundant qualifiers, brace style, and similar. Do not spend edit effort on those by hand — write correct logic and naming and let cleanup do the polish. After a cleanup there is no need to re-inspect or re-build to check it: it never changes behavior.

Solution discovery: the server auto-detects a single `.sln`/`.slnx` in its working directory. If that directory has zero or several, set the `JB_SOLUTION_PATH` environment variable or pass `solutionPath` on the call (which overrides both auto-detection and `JB_SOLUTION_PATH`).

The first inspection or cleanup on a solution is slow while ReSharper warms its caches; later calls are fast. Each run has a 5-minute timeout.

This is an unofficial wrapper. It is not affiliated with or endorsed by JetBrains.
