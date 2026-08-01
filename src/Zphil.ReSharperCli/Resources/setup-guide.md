# Running this server: setup, discovery, timeouts, and output limits

> Unofficial wrapper: not affiliated with or endorsed by JetBrains.

Read this when a call cannot find `jb` or the solution, times out, or comes back shortened. For what
ReSharper *enforces* — inspection severities, cleanup profiles, `.editorconfig` — read the
`resharper://guides/configuration` resource instead; the two guides do not overlap.

## Prerequisite: the ReSharper CLI

This server bundles no JetBrains software. It shells out to a `jb` you install yourself:

```bash
dotnet tool install -g JetBrains.ReSharper.GlobalTools
```

`jb` is located once per server process by running `jb inspectcode --version` against two candidates, in
order:

1. `jb` on `PATH`,
2. `~/.dotnet/tools/jb` (`jb.exe` on Windows).

The second candidate matters because an MCP client starts this server as a child process that often does
*not* inherit your shell's `PATH` — a `jb` that works in your terminal can still be invisible here. When
both candidates fail, the error reports what each one said and how to install the tool. Reference:
<https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html>

## Which solution a call runs against

Resolved in this order:

1. the `solutionPath` argument (a relative path resolves against the server's working directory),
2. the `JB_SOLUTION_PATH` environment variable,
3. a single `.sln` or `.slnx` in the working directory — **top level only, no parent walk**.

Levels 1 and 2 are assertions, not hints: a path that does not exist is an error, never a fall-through to
the next level. Level 3 fails when the directory holds zero or several solution files, and the error names
`JB_SOLUTION_PATH`. So a solution one directory below the server's working directory, or a directory
holding two `.sln` files, needs one of the first two levers — discovery will not find it on its own.

`jb` is located once per server process. Everything else — the solution, its settings file, and the cleanup
profile that file declares — is resolved fresh on every call, so adding or editing a `.sln.DotSettings`
takes effect on the next call rather than after a client restart.

## Why the first call is slow, and the timeout

Both tools run `jb` with `--no-build` against a ReSharper cache directory — `~/.jb-cache` unless
`JB_CACHE_HOME` overrides it. The first inspection or cleanup on a solution populates that cache and can
take minutes; later calls reuse it and finish in seconds. Each `jb` run is capped at **5 minutes**, after
which the process tree is killed and the call fails with a timeout message.

A timeout is almost always the cold run. Retry once — the second attempt starts from a warm cache — and if
it still times out, narrow the work with `files` so `jb` analyses less. Your MCP client may also enforce
its own, shorter tool-call timeout and give up before the server's cap; raise that on the client side (for
Claude Code, the `MCP_TOOL_TIMEOUT` environment variable, in milliseconds).

## Output size: reduction, and truncation as a last resort

Responses are capped at the client's `MAX_MCP_OUTPUT_TOKENS` × 2.5 characters, or 25,000 characters when
that variable is unset or non-positive. Both tools *degrade* rather than being cut: each re-renders its
result at progressively lower detail until it fits, then appends a `DETAIL REDUCED` note naming the level
it landed on and what that level gave up.

**The distinction that carries the meaning: a truncated result is an incomplete list of issues; a reduced
result is complete but less detailed.** Every issue is still counted and every file still named at every
reduction level, so a `DETAIL REDUCED` response is safe to conclude from — unlike a truncated one.

`resharper_inspect` steps down five levels:

1. **Full** — every issue on its own line with file, line, severity, rule, and message. What a scoped scan
   returns.
2. **High** — issues repeating a rule within a file collapse to one line carrying their line numbers and
   one example message (`` `NotAccessedPositionalProperty.Global` [WARNING] x30, lines 13-42 ``). This is
   where a solution-wide run usually lands, and it drops nothing: the count is exact and every line number
   is there.
3. **Medium** — only the eight most-affected files are listed; the rest are counted.
4. **Low** — the per-file listing is replaced by a rollup of the top rules and the top files.
5. **Minimal** — one line: totals, severity counts, and the top rules.

`resharper_cleanup` steps down the same ladder over its per-file statuses, collapsing them toward counts.
The cleanup itself always ran in full; only the report shrank.

A `RESPONSE TRUNCATED` footer survives only as a last-resort backstop, for when even the smallest
rendering overflows the budget — a very small `MAX_MCP_OUTPUT_TOKENS`, essentially. It cuts at the last
line boundary before the cap and states how much was dropped. If you see it, the results above it are
incomplete: re-run scoped with `files`, or raised to a higher `severity`, before concluding anything about
the remainder.

## Environment variables

Set these in the MCP client config's `env` block. They are read from the server process's own environment,
so a change takes effect once the client restarts the server. All are optional.

| Variable | Effect |
|---|---|
| `JB_SOLUTION_PATH` | Solution to use when the working directory holds zero or several. The `solutionPath` argument overrides it per call. |
| `JB_SETTINGS_PATH` | Explicit `.DotSettings` file to pass to `jb`. Set but missing logs a warning and falls through to the next settings source rather than failing the call. |
| `JB_CACHE_HOME` | ReSharper cache directory (default `~/.jb-cache`). |
| `JB_EXTENSIONS` | Semicolon-separated ReSharper plugin IDs to load. |
| `JB_EXTENSION_SOURCE` | Custom NuGet source for those plugins. |
| `RESHARPER_MCP_LOG_LEVEL` | Minimum level for the file log (default `Warning`). Accepts Serilog or Microsoft level names; anything unrecognised falls back to `Warning`. |
| `MAX_MCP_OUTPUT_TOKENS` | Set by the MCP client, not by you — the output budget above. |

## Logs

One daily-rolling file under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, or the platform
equivalent elsewhere, keeping the last 7 days. It records unexpected failures only: an expected error —
a missing `jb`, an undiscoverable solution, a bad path, a non-zero `jb` exit — is returned to you in the
tool result and never written to the log.

Nothing is written to stdout, because that channel carries the MCP JSON-RPC stream and any stray write
would corrupt the protocol; diagnostics go to stderr and the file. Nothing leaves the machine.
