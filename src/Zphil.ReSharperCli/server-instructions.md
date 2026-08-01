This server runs JetBrains' ReSharper command-line tools. `resharper_inspect` is read-only — run it before editing to see existing issues, or after to catch regressions. `resharper_cleanup` rewrites files in place: call it **once**, at the end of a task, with every modified `.cs`/`.razor` file batched into that single call.

Cleanup is cosmetic: it fixes formatting, unused usings, `var` style, modifier order, redundant qualifiers, brace style, and similar. Do not spend edit effort on those by hand — write correct logic and naming and let cleanup do the polish. After a cleanup there is no need to re-inspect or re-build to check it: it never changes behavior.

Before changing what ReSharper enforces — a settings file, a cleanup profile, suppressing a rule, or stopping cleanup from normalizing a deliberate style — read the `resharper://guides/configuration` resource. When a call cannot find the solution, times out, or comes back shortened, read `resharper://guides/setup`.
