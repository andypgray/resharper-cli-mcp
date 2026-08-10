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

## Why the first call is slow, the timeout, and the queue

`resharper_inspect` and `resharper_cleanup` both run `jb` with `--no-build` against a ReSharper cache
directory — `~/.jb-cache` unless `JB_CACHE_HOME` overrides it. The first inspection or cleanup on a
solution populates that cache and can take minutes; later calls reuse it and finish in seconds. Each `jb`
run is capped at **5 minutes**, after which the process tree is killed and the call fails with a timeout
message.

**Only one `jb` at a time may use a solution's cache**, so calls against one solution queue rather than
run together. This is not politeness: a second concurrent `jb` cannot open the warm cache, and instead of
waiting it silently forks a new, *empty* one — so both runs do cold-cache work, the slower one usually
past the timeout, and the fork is left behind taking up disk. Queueing costs a wait and nothing else. The
queue is shared by every client on the machine, since it is the cache that is contended, not the server.

So a call is bounded by its wait plus its run. A call waits up to **5 minutes** for a run already in
flight, and only then starts its own 5-minute run; if the wait runs out the error says a run against that
solution is already going, and retrying shortly after is the right response. The wait is invisible when
nothing else is running, which is the normal case.

A timeout with nothing else running is almost always the cold run: retry once, and if it still times out,
narrow the work with `files` so `jb` analyses less. Your MCP client may also enforce its own, shorter
tool-call timeout and give up before the server's caps; raise that on the client side (for Claude Code,
the `MCP_TOOL_TIMEOUT` environment variable, in milliseconds).

### The background pre-warm

To spend that cold run on idle time instead of yours, the server starts one speculative
`jb inspectcode` as soon as a client connects — long before the first tool call, usually. It targets
exactly the solution a call with no `solutionPath` would find, and does nothing at all when there is none,
so a server started somewhere without a solution simply never pre-warms. Nothing about it is visible in a
tool result.

It is never allowed to cost you anything **in this server process**: a tool call arriving mid-pre-warm
*cancels* it and takes the cache for itself, so the call waits for `jb` to be killed and reaped — a second
or two — rather than for the run to finish. **A pre-warm in another server process cannot be cancelled**,
though, so a call there queues behind it exactly as it queues behind another session's real call. That is
the one case where pre-warming can make a first call slower than it would have been.

It runs **at most once per session**, and is skipped when any `jb` run against that solution's cache
succeeded within the last hour — a tool call counts, so working in a repo does not earn its next session a
redundant analysis. It is a full solution analysis when it does run (`--include` does not make `jb` do less
work), so set `RESHARPER_MCP_PREWARM=off` if you would rather not spend the CPU.

## Phantom compilation errors, and resetting the cache

**The signature.** `resharper_inspect` reports `.CSharpErrors` issues — `Cannot resolve symbol 'Foo'`, and
knock-on "Ambiguous invocation" where an unresolved symbol was an argument — in files you did not edit,
naming symbols declared in a file you *did*. Often a whole cluster of them at once.

**The discriminator, and it is decisive: build the solution.** If the compiler and your tests are green,
those errors do not exist. ReSharper's solution-wide analysis keeps its own index in the cache, and that
index can miss invalidating the consumers of a declaration you reshaped. Once it is wrong it stays wrong:
re-running the inspection returns the identical set, scoping it with `files` returns the same, and nothing
you do in the editor clears it. Only a real compilation error survives the build test — fix that instead.

**The cure.** Call `resharper_reset_cache`. It deletes the solution's cache generation directories under
the cache home, and the next inspect or cleanup rebuilds the index from cold. It takes the same queue lock
the analysis tools take, so it waits for a run in flight rather than deleting the cache underneath it, and
a run that starts meanwhile waits for the reset. That cold rebuild costs minutes and can hit the 5-minute
run cap on a large solution, so the retry after a reset is the one call most worth expecting to be slow.

It refuses in one case rather than guessing: when the cache home holds generations for **two solutions with
the same file name**, since `jb` names those directories with a hash of the solution path and not the path
itself. The error names the candidates so you can delete the right one yourself.

The stale index originates in the ReSharper CLI's incremental invalidation, not in this wrapper, so
nothing here can fix it — and `jb` exposes no cache-invalidation option of its own (`--caches-home` only
chooses *where* caches live), which is why the operation lives here at all. A `--no-build` solution model
is *not* the cause: `jb` builds its model from source for a solution's own projects, and resolves
declarations added since the last build without one.

## Output size: reduction, and truncation as a last resort

Responses are capped at the client's `MAX_MCP_OUTPUT_TOKENS` × 2.5 characters, or 25,000 characters when
that variable is unset or non-positive. The two analysis tools *degrade* rather than being cut: each
re-renders its result at progressively lower detail until it fits, then appends a `DETAIL REDUCED` note
naming the level it landed on and what that level gave up. (`resharper_reset_cache` has nothing to
degrade — its report is a line per cache generation.)

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
| `RESHARPER_MCP_PREWARM` | Set to `off` to stop the background pre-warm above. Anything else, including unset, leaves it on. |
| `RESHARPER_MCP_LOG_LEVEL` | Minimum level for the file log (default `Warning`). Accepts Serilog or Microsoft level names; anything unrecognised falls back to `Warning`. |
| `MAX_MCP_OUTPUT_TOKENS` | Set by the MCP client, not by you — the output budget above. |

## Logs

One daily-rolling file under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, or the platform
equivalent elsewhere, keeping the last 7 days. It records unexpected failures only: an expected error —
a missing `jb`, an undiscoverable solution, a bad path, a non-zero `jb` exit — is returned to you in the
tool result and never written to the log.

A pre-warm that was skipped, cancelled, or failed is not log-worthy either — it costs the session nothing
beyond what it would have cost anyway — so it appears only at `Debug`.

Nothing is written to stdout, because that channel carries the MCP JSON-RPC stream and any stray write
would corrupt the protocol; diagnostics go to stderr and the file. Nothing leaves the machine.
