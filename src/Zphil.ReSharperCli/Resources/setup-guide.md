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
*not* inherit your shell's `PATH` — a `jb` that works in your terminal can still be invisible here. At
`Debug` each candidate leaves a line with what it answered and how long it took, so one that fails before
a later one succeeds is accounted for rather than reading as an unexplained pause before the first call.
When both candidates fail, the error reports what each one said and how to install the tool. Reference:
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
solution populates that cache and can take minutes; later calls reuse it and finish several times faster,
though rarely in seconds. Both durations are properties of the machine and the solution (warm cost does not
track file count, and a larger solution can be the faster one warm), so time your own rather than
extrapolating from size.

Each `jb` run is capped at **10 minutes**, after which the process tree is killed and the call fails with a
timeout message. A cold run on a large solution can genuinely need longer than the cap, and one killed at
the cap has produced nothing. `RESHARPER_MCP_TIMEOUT_SECS` moves that cap
(default `600`). It is named for this server rather than for `jb` because `jb` never learns of it: it arms a
kill timer here, unlike every `JB_` variable below, each of which becomes something `jb` is actually told.
Seconds rather than minutes so a cap can be pitched just above a run you have actually timed.

**The cap is this server's own, and nothing outside imposes it.** An MCP client's tool-call limit is
typically far longer — Claude Code's default is measured in hours — so a timeout here is always this
number rather than `jb` giving up or the client losing patience. Raise it for a solution that genuinely
needs longer. It stays bounded (a day at most) so that a `jb` which has truly hung cannot occupy an agent
indefinitely.

**Only one `jb` at a time may use a solution's cache**, so calls against one solution queue rather than
run together. This is not politeness: a second concurrent `jb` cannot open the warm cache, and instead of
waiting it silently forks a new, *empty* one — so both runs do cold-cache work, the slower one usually
past the timeout, and the fork is left behind taking up disk. Queueing costs a wait and nothing else. The
queue is shared by every client running this server, since it is the cache that is contended and not the
server process — but it reaches no further than that, and a `jb` you start yourself is outside it. See
*Running `jb` yourself, beside this server* below.

So a call is bounded by its wait plus its run. A call waits up to the same cap for a run already in
flight, and only then starts its own run; if the wait runs out the error says a run against that solution
is already going, and retrying shortly after is the right response. The wait is invisible when nothing
else is running, which is the normal case.

**A run in flight reports itself every ten seconds**, as an MCP `notifications/progress` message against
the token the client sent with the call. The messages track the stages above, in order: the wait for
another run on the same cache, then the cache state `jb` opened (`cold (none on disk)`, `warm`, `stale`,
`part-built`, `seeded from a sibling checkout`), then a running count of files as `jb` analyses them.
Where this solution has already finished a run from the same cache state, that clause names what it cost —
`cold (none on disk; the last cold run took 8 minutes 17 seconds)` — which is the other half of telling a
slow run from a stuck one. The figure is keyed by the cache state, so a warm run never quotes a cold one's
minutes; a part-built cache records and quotes nothing, two resumptions of differently killed runs not
being comparable; and `resharper_reset_cache` clears the figures along with the warm marker.
Every message sent once `jb` is running carries the elapsed time and the cap, so a call can be seen
approaching the cap rather than reported as having hit it. There is no percentage: `jb` announces no file
count up front and its analysis and inspection sweeps report different totals for the same solution, so
any bar would be measuring the cap rather than the work. `resharper_reset_cache` reports the same way
while it queues on that lock and goes quiet once it holds it — the deletes that follow take moments, and
it names no cap, because a reset spends none of the run budget.

Two things this does not do. It reaches only a client that sends a `progressToken` — one that does not
gets the same result and no notifications, with nothing to configure either way. And it does not extend
any timeout: a client that resets its own hang detection on progress will now wait on a `jb` that has
genuinely hung, so the server's cap becomes the thing that ends such a run. That cap names itself and
`RESHARPER_MCP_TIMEOUT_SECS` in the message it fails with.

A timeout with nothing else running is almost always the cold run. **Scoping the retry with `files` does
not help**: `jb` analyses the whole solution whatever the report is narrowed to, so a one-file run is no
faster than a solution-wide one — often marginally slower. What a killed run does leave behind is a
partly-built cache that a retry picks up rather than starting over — though not from exactly where it
stopped, because whatever was in flight when it was killed is lost and gets redone. With a warm same-named
checkout in the cache home, the next call replaces that remnant with a copy of that one instead, which is
faster than resuming. Retrying therefore makes real progress, but a series of capped runs costs
appreciably more than one run allowed to finish; where a cold analysis simply takes longer than the cap,
raising `RESHARPER_MCP_TIMEOUT_SECS` is the fix and retrying is not. Your MCP
client may also enforce a tool-call timeout of its own and give up before the server's cap; raise that on
the client side too (for Claude Code, the `MCP_TOOL_TIMEOUT` environment variable, in milliseconds).

### The background pre-warm

To spend that cold run on idle time instead of yours, the server starts one speculative
`jb inspectcode` as soon as a client connects — long before the first tool call, usually. It targets
exactly the solution a call with no `solutionPath` would find, and does nothing at all when there is none,
so a server started somewhere without a solution simply never pre-warms. Nothing about it is visible in a
tool result.

It is never allowed to cost you anything **in this server process**: a tool call arriving mid-pre-warm
*cancels* it and takes the cache for itself, so the call waits for `jb` to be killed and reaped — a second
or two — rather than for the run to finish. That includes `resharper_reset_cache`, the member of the set
easiest to miss: it runs no `jb` of its own, yet it outranks the pass all the same — queueing would put
the call that deletes the cache behind the run busy building it. The precedence is process-wide rather
than per solution, so a call against one solution stands down a pre-warm of another: speculative work
loses to anything you are waiting on, whichever solution each is about. **A pre-warm in another server
process cannot be cancelled**, though, so a call there queues behind it exactly as it queues behind
another session's real call. That is the one case where pre-warming can make a first call slower than it
would have been.

**At most one pass runs at a time**, and a pass is skipped when any `jb` run against that solution's cache
succeeded within the last hour — a tool call counts, so working in a repo does not earn its next session a
redundant analysis. It is a full solution analysis when it does run (`--include` does not make `jb` do less
work), so set `RESHARPER_MCP_PREWARM=off` if you would rather not spend the CPU.

A pass is also **subject to the run cap** like any other `jb` run, and on a large cold solution running the
whole cap and being killed is the ordinary shape rather than a fault. The log says `Capped` for that and
`Cancelled` for a pass a tool call took the cache back from; both left the generation part-built, so neither
is the `Skipped` that means no `jb` ever started. Nothing is reported to you either way.

Exactly one thing arms another pass: **a tool call hitting the run cap**. That is when speculative work is
worth most — the cache is part-built and you are reading the error rather than waiting on `jb` — so the
server spends the pause warming the solution that just timed out. Nothing else re-arms it: not a timer, not
each message, and never a pre-warm reacting to its own timeout. Recurrence only ever advances when you make
another call.

One case it still cannot help, because the decision is made at connect time: a session that connects while
the cache is fresh, sits idle for hours, and only then makes its first call. The pass ran at connect, found
a recent successful run, and correctly skipped; by the time the call arrives the tree has moved on and the
call pays the cold cost anyway. Raising `RESHARPER_MCP_TIMEOUT_SECS` is the answer there.

### Worktrees, clones, and copies of one repository

`jb` keys a cache generation by the solution's **absolute path**, so a fresh `git worktree` — or a clone, or
a copied directory — starts cold no matter how much analysis of the same code already sits in the cache
home. It is also the case least likely to survive: a cold whole-solution run is the one that reaches the
cap, and the pre-warm cannot help, because it warms the solution the *server's* directory resolves while a
session in a worktree passes its own `solutionPath` on every call.

So when a call finds no cache generation for its solution — or only the part-built remnant of runs that
never finished, which no successful run has marked — and a **same-named** solution's last successful run
recorded one, the server copies that generation under the name `jb` will look for before starting the run.
Nothing needs configuring and nothing reports it.

**It is a trade, not a free win.** A copied cache carries the donor's absolute paths, so `jb` re-keys it on
the run that opens it, and that run also analyses whatever the donor's checkout never saw. The premium over
the warm run that follows is not a fixed cost: it grows with the donor's size and with how far the two
checkouts have drifted apart. What it buys is the difference between a cold analysis and a warm one. On a
solution large enough for cold to run past the cap, that difference is the whole result: a seeded run can
return where the same call was previously killed at the cap having produced nothing. On a small solution,
where a cold run finishes in a few minutes anyway, the premium can cost more than the rebuild it replaced.
It is aimed squarely at the first case.

It gives up at the first doubt, silently, and the call proceeds exactly as it would have: no donor recorded,
a donor a run currently holds, a copy that fails part way. It takes the same queue lock for the donor that
it holds for the target, so it never reads a cache another `jb` is writing. It never replaces a cache a
successful run produced: every such run leaves a marker beside the generation, and a marker means hands
off. What it will replace is a generation with no marker at all — the part-built remnant of a first run
that was killed, which is otherwise the one thing that could block the copy it needs for ever — and even
then the whole copy is made and standing beside the remnant before anything is deleted, so a failure costs
the copy rather than the cache. It never runs after a reset, because a reset is a request for a cold
rebuild and is honoured until a run against that solution succeeds. `jb` remains the judge of what it was
handed: it validates a cache it opens against its own format and rebuilds in place when it does not like
it, so a copy it rejects costs the copy and nothing more.

Upgrading `jb` is the routine case of that rebuild — JetBrains ships roughly thirty stable releases a year —
so a generation the current build did not write is passed over as a donor, and one belonging to this
solution reads as `stale (cache written by jb 2026.2.0.2, this is 2026.2.1, and jb rebuilds it)` rather
than `warm`: the run about to start does cold-shaped work whatever the directory on disk suggests.

### Running `jb` yourself, beside this server

**The queue is a lock file in the cache home that this server's own callers take, and nothing else takes
it.** A `jb` you start yourself — from a terminal, a script, a CI step — never touches that file, and
pointing it at the same `JB_CACHE_HOME` does not enrol it. So your run and a call through here can reach
one solution's cache at the same moment, and from there `jb`'s own lock decides: the second to arrive forks
a new, *empty* generation rather than waiting for the first, which is the outcome the queue exists to
prevent. Both runs then do cold-cache work, and the fork is left behind taking up disk.

**Point a run of your own at its own `--caches-home` and it cannot collide.** There are things these tools
do not expose — an XML or HTML report, a sweep at a severity they do not offer, a probe of which settings
layer `jb` actually mounts — and running `jb` directly for those is reasonable. A separate cache home is a
separate set of generations, so nothing in it is contended and neither run can fork the other's. It costs
that run a cold cache of its own, which is the price of working outside the queue.

The complete itemised findings of a solution-wide run are no longer one of those things:
`resharper_inspect`'s `report=Markdown` writes them to a file from inside the queue. See "Output size"
below.

**A forked generation is a directory whose name ends `.01` or higher, beside the `.00`.**
`resharper_reset_cache` drops it with the rest, because a fork of one solution carries the same path hash
as the generation it forked from — run it with no `jb` live, your own included. Worth knowing too: a run
you start yourself stamps no warm marker, so its work is invisible to the pre-warm's freshness check and
to the seeding above. A checkout that only ever saw a `jb` of your own reads here as one no run has
finished.

### A server killed outright, and the `jb` it was running

The queue is a lock file, and the OS releases a dead holder's handle — but not the `jb` that holder
started. So a client that kills the server rather than shutting it down (replacing the tool binary during
an upgrade is the usual way) used to leave a `jb` running against a generation the next server would read
as free, and that next run forked. A shutdown the client asks for has never had this problem: the server
waits for a speculative `jb` to let go before exiting.

The server now binds each `jb` it starts to its own lifetime, with the strongest primitive the platform
offers — and they are not equivalent. On **Windows** a job object with `KILL_ON_JOB_CLOSE` covers `jb` and
everything `jb` starts, and it holds through a kill no code inside the process can intercept. On **Linux**
`setpriv --pdeathsig SIGKILL` covers `jb` itself but not a worker `jb` forks afterwards, and it needs
util-linux 2.33 or later. On **macOS** there is no equivalent, and the behaviour is unchanged. The startup
line names the one in force as `orphan guard` — `kill-on-job-close`, `parent-death-signal` or `none` — and
where it reads `none`, runs behave exactly as they did before. For a fork already on disk, the remedy is
the same as for any other: `resharper_reset_cache`.

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
a run that starts meanwhile waits for the reset. A background pre-warm is the one thing it does not wait
for: the reset cancels it and takes the generation once the killed `jb` has been reaped — a second or two,
not the minutes the pass had left. That cold rebuild costs minutes and can hit the run cap on a large
solution, so the call after a reset is the one most worth expecting to be slow — and the one most worth
raising `RESHARPER_MCP_TIMEOUT_SECS` for ahead of time.

It deletes only what provably belongs to this solution. `jb` names a generation directory with a hash of the
solution's **full path**, which this server reproduces, so a cache home shared by two checkouts of one
repository — or by two unrelated solutions with the same file name — is ordinary rather than an obstacle: the
generations carrying another path's hash are named in the report and left where they are, and a hash matching
nothing deletes nothing at all.

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
   where a solution-wide run usually lands. The count is exact and every line number is there; what it
   gives up is the individual message of each repeat, with one example standing for the group.
3. **Medium** — only the eight most-affected files are listed; the rest are counted.
4. **Low** — the per-file listing is replaced by a rollup of the top rules and the top files.
5. **Minimal** — one line: totals, severity counts, and the top rules.

**To land on one of those levels deliberately, pass `detail`.** It names the most detailed level the
response may use, and `Full` — the default — leaves the ladder to decide exactly as it always has. It caps
rather than pins: rendering starts at the level you name and still steps below it when the result does not
fit, so the mid-line chop the ladder exists to prevent stays prevented. The note says which of the two
happened — `Rendered at the requested detail level Low` for a level you asked for, `Output exceeded the
25,000 character limit. Reduced to Low` for one the budget forced — and on the first it drops the "narrow
the scan" remedy, because a caller that asked for less has already done that. The `jb` run is unaffected:
the same analysis runs and the same issues are parsed, and `detail` decides only how many of them the
response spells out. It will not make a call finish sooner.

**When you need every finding with its own message, pass `report=Markdown`.** It is off by default. Set it
and `resharper_inspect` writes the whole listing at Full detail to a file and names that file at the top of
its response, which still carries the summary the ladder produced. The two answer different questions: the
ladder is for a caller reading a verdict in the response, and a reduced verdict is safe to conclude from;
the report is for working through findings one at a time, which the response cannot serve on a
solution-wide run because such a run lands on High by construction. Markdown is the only format — `jb`
writes one report per run and this server needs the SARIF to build the summary, so XML, HTML or raw SARIF
would each cost a second full run of `jb`. The file is written under the system temp directory, in a
directory this server owns, and is deleted once it is 7 days old.

The two compose, and neither subsumes the other: `detail` decides how much lands in the response, `report`
whether all of it also lands in a file. `detail=Minimal report=Markdown` surveys a legacy solution in a
single call — a one-line verdict in the response, every finding with its own message in the file — and the
response offers neither remedy, having already done both.

`resharper_cleanup` steps down the same ladder over its per-file statuses, collapsing them toward counts.
The cleanup itself always ran in full; only the report shrank. It has neither parameter: `jb cleanupcode`
writes no report of its own, and what the ladder reduces there is a status list of the files you supplied,
which you can already shorten by supplying fewer.

A `RESPONSE TRUNCATED` footer survives only as a last-resort backstop, for when even the smallest
rendering overflows the budget — a very small `MAX_MCP_OUTPUT_TOKENS`, essentially. It cuts at the last
line boundary before the cap and states how much was dropped. If you see it, the results above it are
incomplete: re-run scoped with `files`, or raised to a higher `severity`, before concluding anything about
the remainder.

## Environment variables

Set these in the MCP client config's `env` block. They are read from the server process's own environment,
so a change takes effect once the client restarts the server. All are optional. The `JB_` ones each become
something `jb` itself is told; the `RESHARPER_MCP_` ones govern this server's own behaviour and never reach
`jb` at all.

| Variable | Effect |
|---|---|
| `JB_SOLUTION_PATH` | Solution to use when the working directory holds zero or several. The `solutionPath` argument overrides it per call. |
| `JB_SETTINGS_PATH` | Explicit `.DotSettings` file passed to `jb` as `--settings`, which mounts it as a Custom layer overriding the solution's and every project's own settings — use it only when that is the intent. Set but missing logs a warning and falls through to the next settings source rather than failing the call. |
| `JB_CACHE_HOME` | ReSharper cache directory (default `~/.jb-cache`). |
| `JB_EXTENSIONS` | Semicolon-separated ReSharper plugin IDs to load. |
| `JB_EXTENSION_SOURCE` | Custom NuGet source for those plugins. |
| `RESHARPER_MCP_TIMEOUT_SECS` | Cap in seconds on one `jb` run, and on the wait for one already in flight (default `600`). Clamped to 60…86,400; anything unreadable falls back to `600`. |
| `RESHARPER_MCP_PREWARM` | Set to `off` to stop the background pre-warm above. Anything else, including unset, leaves it on. |
| `RESHARPER_MCP_LOG_LEVEL` | Minimum level for the file log (default `Warning`). Accepts Serilog or Microsoft level names; anything unrecognised falls back to `Warning`. Set it to `Information` to record what each call did to the cache — see "Logs" below. |
| `MAX_MCP_OUTPUT_TOKENS` | Set by the MCP client, not by you — the output budget above. |

## Logs

One daily-rolling file under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, or the platform
equivalent elsewhere, keeping the last 7 days. `RESHARPER_MCP_LOG_LEVEL` chooses the level; the default is
`Warning`, which records unexpected failures only. An expected error — a missing `jb`, an undiscoverable
solution, a bad path, a non-zero `jb` exit — is returned to you in the tool result and is never written to
the log at any level.

Each line reads
`[timestamp] [level] [session] [run] [source] message`. **Session** is `CLAUDE_CODE_SESSION_ID` when the
client sets it, else a short random id, and it separates concurrent server processes sharing the daily file.
**Run** is a four-digit counter over one process: every tool call and every pre-warm pass gets one, and every
line that call or pass causes carries it, so interleaved work can be read apart. A line belonging to no run —
startup, shutdown — reads `----`.

`Information` is what to raise the level to when a call was slower than expected. It carries what this server
did and nothing else: the MCP SDK's and .NET Hosting's own categories are held at `Warning`, and this server
emits its own startup, shutdown, and per-run lines in their place.

| Level | Carries |
|---|---|
| `Warning` (default) | Unexpected failures, plus the degradations that leave a promise unkept — a settings file named but missing, a run lock that could not be taken, a cache reset that could not be recorded. |
| `Information` | The startup fingerprint (version, pid, cache home, run cap, pre-warm on/off, orphan guard) · the config each call resolved and how it found its solution · one line as each `jb` run starts, naming its cache state and how long it queued · one as it ends, with its exit code and duration · every transplant decision, seeded or declined, with its reason · a notable lock queue wait · a pre-warm's start and outcome · a speculative pass stood down for a call · what a cache reset dropped. |
| `Debug` | The full `jb` command line · one line per `jb` candidate probed, with what it answered and how long it took, including a candidate that failed before a later one succeeded · the tool-call envelope and argument shape · the detail level a response settled at, and whether it was truncated · cache generation sizes · warm-marker stamps · the declines and skips that cost nothing. |

A typical inspect costs about four `Information` lines, so a day at that level stays readable. Two of them
are the pair around the `jb` run, and the first of the pair is written *before* the run, because a `jb` run
is minutes of silence: a run with an opening line and no closing one is still going, or was killed. That is
the log's answer after the fact. The progress notifications described above are the answer during, for a
client that asks for them; the log is what remains for a client that did not, and for a session that has
ended. The line closing a run killed at the cap also names how many files `jb` had reached, which is the
only record of how much a capped run left behind in the cache.

Nothing is written to stdout, because that channel carries the MCP JSON-RPC stream and any stray write
would corrupt the protocol; diagnostics go to stderr and the file. Nothing leaves the machine.
