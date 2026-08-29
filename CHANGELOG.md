# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- An [Agent Plugins](https://agent-plugins.org) manifest pair at the repository root, `plugin.json` and
  `mcp.json`, so hosts that read that specification — Cursor among them — can install the server without
  hand-written configuration. The launcher names an exact version the way the Claude Code plugin's does,
  and the release workflow now checks that pin against the tag alongside the five version fields it
  already checked. The root `plugin.json` deliberately declares no `version`: the schema makes it
  optional, nothing installs from it, and a field nothing reads is a field that drifts. Claude Code
  users are unaffected — its own manifest under `.claude-plugin/` is unchanged, and the two live side
  by side.

- A `gemini-extension.json`, so the server installs as a [Gemini CLI](https://google-gemini.github.io/gemini-cli/)
  extension. Its launcher names the same exact package version the other manifests name, and both its
  fields join the set the release workflow checks against the tag. The extension name has to equal the
  repository directory name, which is why it reads `resharper-cli-mcp` rather than the shorter server
  key the configuration examples use.

### Changed

- The Claude Code plugin launches an exact server version rather than whatever NuGet holds at the
  time. Its `dotnet dnx` argument now names `Zphil.ReSharperCli@<version>`, so the marketplace commit
  a user installs determines the server code that runs; two installs of one commit, weeks apart, no
  longer execute different builds. The plugin manifest's own `version` moves with it, because Claude
  Code ships an installed plugin an update only when that field changes — pinning the launcher alone
  would have stranded every existing install on its install-time version. Run
  `claude plugin update resharper-cli-mcp@resharper-cli-mcp` to pick up a newer server. Both fields
  are release-time version sites now, checked against the tag before anything reaches NuGet.

## [1.5.0] - 2026-08-26

### Added

- MCP progress notifications from `resharper_inspect` and `resharper_cleanup`. A run in flight reports
  itself every ten seconds: the wait for another run on the same cache, then the cache state `jb` opened,
  then a running count of the files it has analysed, each message carrying the elapsed time and the run cap.
  A caller can now see a run approaching the cap and raise `RESHARPER_MCP_TIMEOUT_SECS` before the call
  fails. Both the ten-second beat and the per-file count come off one timer, so a cold run that names 1,332
  files sends about fifty notifications rather than 1,332, and the interior gaps in `jb`'s own output —
  measured at up to 42 seconds mid-stream — are covered by the same mechanism. Notifications reach only a
  client that sends a `progressToken`; one that does not gets an unchanged result, and the parameter stays
  out of the advertised tool schema. There is no percentage: `jb` announces no file count up front and its
  two sweeps report different totals for one solution, so a bar would be measuring the cap rather than the
  work. The background pre-warm reports nothing, having no caller to report to.

- `resharper_reset_cache` reports its queue wait as MCP progress. A reset takes the same per-solution run
  lock a `jb` run does, so it can queue behind another session's analysis for up to the full wait budget —
  and until now it did so silently, since a reset spawns no `jb` to stream. It now sends the same
  ten-second heartbeat the analysis tools send while queued, reading `cache reset on App.slnx: waiting for
  another run on this solution's ReSharper cache — 2 minutes 3 seconds`, and goes quiet once it holds the
  lock, because the deletes take moments and a beat past that point would describe a wait that had ended.
  The messages name no run cap, since a reset spends none of the run budget. Notifications reach only a
  client that sends a `progressToken`; the tool's schema, its result, and its log line are unchanged.

- A run's progress lines and its timeout message can name what the last comparable run of this solution
  cost. The heartbeat says how long a run has been going against the cap, but nothing said how long
  this solution usually takes — the other half of telling slow from stuck. A `jb` run that exits
  cleanly now records its duration in a sidecar beside the warm marker, keyed by the cache state it
  started from — `cold`, `seeded`, or `warm` — because one remembered number would lie: a single
  measured solution ran 497 seconds cold, 456 seeded, and 39 warm. Where a figure is recorded, the
  cache-state clause carries it — `cold (none on disk; the last cold run took 8 minutes 17 seconds)` —
  on both the opening log line and the progress messages sent while `jb` loads the solution, and the
  timeout message adds `The last cold run of this solution took 8 minutes 17 seconds.` beside the
  advice it already gives. Queue time is excluded from the recorded duration; a run against a
  part-built cache records and quotes nothing, since two resumptions of differently killed runs are not
  comparable; a freshly seeded checkout quotes nothing until a run of its own finishes; and
  `resharper_reset_cache` clears the record along with the warm marker. With no figure recorded, every
  line reads byte-for-byte as it did.

- The warm marker records the `jb` build that wrote it, and a cache another build wrote reads as `stale`
  rather than `warm`. Measured across the 2026.2.0.2 → 2026.2.1 patch bump, on a static tree and a fresh
  cache home: the old build's cold run took 116 seconds, the new build's first run over that same
  generation took 220, and its immediate second run took 64 — `jb` gets no warm-run value from an earlier
  build's cache and does cold-shaped work rebuilding it in place, while the cache-state line promised a
  minute. The line now reads `stale (cache written by jb 2026.2.0.2, this is 2026.2.1, and jb rebuilds it)`,
  the recorded-cost quote switches to the cold figure for the same reason, and a sibling checkout whose
  cache an earlier build wrote is passed over as a seeding donor. A marker written before this release
  names no build and reads as stale until the next clean run rewrites it. JetBrains ships roughly thirty
  stable releases a year, so an upgrade is routine rather than rare.

- A `report` argument on `resharper_inspect`. Set it to `Markdown` and the complete itemised findings are
  written to a file, which the response names above the summary it already returned. This is the way to get
  every finding with its own message out of a solution-wide run: such a run always exceeds the output budget,
  and the rendering that fits collapses issues repeating a rule within a file to one example. It is off by
  default, and markdown is the only format — `jb` writes one report per run and this server needs the SARIF
  to build the summary, so XML, HTML or raw SARIF would each cost a second full run. The file lands under the
  system temp directory, in a directory the server owns, and is deleted once it is 7 days old.

- A `detail` argument on `resharper_inspect`, naming the most detailed level its response may use — `Full`
  down to a one-line `Minimal`. Until now the only way to reach the rollup was to overflow the output
  budget and let the ladder fall into it. It caps rather than pins: rendering starts at the level named and
  still steps below it when the result does not fit, so nothing is chopped mid-line, and the
  `DETAIL REDUCED` note says which of the two happened. A level that was asked for no longer suggests
  narrowing the scan, since the caller has just done that. The `jb` run is unaffected — the same analysis,
  the same issues, fewer of them spelt out — and `Full`, the default, leaves every existing response
  byte-for-byte as it was. Pairs with `report`: `detail=Minimal report=Markdown` gives a one-line verdict
  in the response and every finding in the file.

- The file log records what each call did to the ReSharper cache. At `Information`, a `jb` run is bracketed by
  two lines: one as it starts, naming whether the cache was warm, cold, part-built or dropped by a reset and
  how long the run queued behind another, and one as it ends, with the exit code and the duration. Cache
  transplants state whether they seeded and why not when they declined, and a seeded run reports the donor's
  size and how long the copy took. A cache reset says what it dropped. A pre-warm names how its pass ended —
  `Warmed`, `Failed`, `Capped` for one killed at the run cap, `Cancelled` for one a tool call took the cache
  generation back from, and `Skipped` only when no `jb` started at all. Raise the level with
  `RESHARPER_MCP_LOG_LEVEL=Information`; the default stays `Warning`.
- A startup line naming the version, process id, cache home, resolved run cap and pre-warm setting, so the
  configuration a server is actually running under can be read rather than inferred.
- A `run` column: a four-digit counter over one process, carried by every line one tool call or one pre-warm
  pass causes. Concurrent work shares the daily log file, and this is what reads it apart. Lines belonging to
  no run — startup, shutdown — show `----`.
- One `Debug` line per `jb` candidate probed during discovery, naming the candidate, what it answered and how
  long the probe took. A candidate that fails before a later one succeeds left no trace at any level: the
  common case is `jb` missing from the `PATH` an MCP client hands its child, so discovery falls through to
  `~/.dotnet/tools`, and the probe that failed first escapes before the process lines are written. Its cost
  reached the reader only as an unexplained delay ahead of the first call.
- A README badge naming the latest stable ReSharper command-line tools release the daily contract check
  verified. The check publishes the version it tested as Shields endpoint JSON on a workflow-owned `badges`
  branch — green when the run held on every runner, red naming the version that did not — and a dispatch
  that pins a version or opts into prereleases leaves the badge alone.

### Changed

- The timeout message no longer promises that a retry "resumes rather than starting over" without evidence.
  A run killed having analysed forty files and one killed at 1,200 read identically before; the message now
  names the count when it has one, and keeps the general wording when it does not. The log line for a run
  killed at the cap carries the same count.
- The pre-warm's outcome moved from `Debug` to `Information`, pairing with the start it already logged. At
  `Information` a log previously showed pre-warms beginning and never ending.
- `ModelContextProtocol` and `Microsoft.Hosting` are held at `Warning`, so `Information` carries what this
  server did rather than framework chatter. The per-request timing the MCP SDK used to supply is replaced by
  this server's own run lines.
- Every log line now carries the class that wrote it. Most previously came from a static logger that bypassed
  the logging pipeline and rendered with an empty source field.
- The README opens with the cache lifecycle the server runs over `jb` (the run queue, checkout seeding, the
  background pre-warm, cache reset, and output sizing) before installation, and drops the detail the
  `resharper://guides/setup` and `resharper://guides/configuration` resources already carry.
- The README and the setup guide no longer claim warm runs "finish in seconds", which no measured run
  supported. Cache durations are stated as properties of the machine and the solution (cold takes minutes,
  warm is several times faster, and warm cost does not track file count), with the guidance to time your own
  solution rather than extrapolate from its size. The seeded-cache premium is likewise stated as a cost that
  grows with the donor's size and the checkouts' drift, not as a number.

### Fixed

- A server killed outright — as happens when the tool binary is replaced under a running session — no longer
  strands the `jb` it had going. A stranded `jb` keeps the ReSharper cache generation open after the OS has
  released the dead server's queue lock, so the next run reads the queue as free, starts anyway, and `jb`
  forks a second empty generation rather than waiting: that run does cold-cache work, and the fork stays on
  disk and is counted in the cache from then on. A shutdown the client asks for was never affected — the
  server already waits for a speculative `jb` to let go before exiting. Each platform now gets the strongest
  primitive it offers, and they are not equivalent. On Windows a job object with `KILL_ON_JOB_CLOSE` covers
  `jb` and everything `jb` starts, and holds through a kill no in-process handler can intercept. On Linux
  `setpriv --pdeathsig SIGKILL` covers `jb` itself but not a worker it forks afterwards, and needs util-linux
  2.33 or later. macOS has no equivalent and keeps the previous behaviour, as does any machine where the
  primitive cannot be set up. The startup line names the guarantee in force as `orphan guard`, and
  `resharper_reset_cache` still clears a fork already on disk.

## [1.4.0] - 2026-08-16

### Changed

- Seeding a fresh checkout's cache from a warm copy of the same repository now also fires where the
  checkout holds the **part-built remnant of a run that never finished** — until now the deliberate rule
  was that any existing generation is left alone, and in the field that rule meant the feature never fired
  at all. The shape is circular: the first run on a new checkout is exactly the one long enough to hit the
  cap, and the moment it is killed it leaves a stunted cache behind; from then on there is a generation
  there, so every later call declines to seed and resumes the stunted one instead. A reset was no remedy
  either, since it records that the next run is meant to be cold. What separates a cache worth keeping from
  those leftovers is the marker every successful run leaves beside its generation: **a marker means hands
  off** — any marker, including the empty form older versions wrote — and no marker at all means no run
  against that path ever finished. Nothing is deleted until the whole copy has been made and is standing
  beside the remnant, so a copy that fails or is cancelled leaves the remnant exactly where it was and the
  run resumes it as before. `resharper_reset_cache` is unchanged and still outranks all of this: a reset is
  a request for a cold rebuild, and it is honoured until a run against that solution succeeds. One bounded
  residual: a checkout analysed only by `jb` directly, never through this server, has no marker for the
  server to read, so its cache reads as leftovers and can be replaced by a copy — costing one re-key, once.
  Measured on a real remnant: a 21 MB generation left by a run killed two days earlier was replaced by a
  277 MB copy of the warm checkout beside it, and the call it had been blocking returned in 456 s where the
  same call had previously been killed at the cap having produced nothing.

### Fixed

- `--settings` is no longer passed for a settings file `jb` discovers on its own — the
  `{solution}.DotSettings` beside the solution, or the machine-wide `GlobalSettingsStorage.DotSettings`
  when no adjacent file exists. The flag is not additive: it mounts its file as a Custom layer above
  ReSharper's whole layer stack, so passing an already-discovered file silently demoted every project's
  own `{project}.csproj.DotSettings` — the layer that outranks the solution's on a direct `jb` run, and
  exactly how a repo narrows a rule for one project. Measured on a solution carrying such a layer: a
  direct `jb` run reported 0 findings and this server reported 83, every one of them scoped away by the
  project layer it had demoted. The global case was the same bug one level up, and worse for being
  personal configuration: in a repo with no `.sln.DotSettings`, a machine-wide IDE preferences file
  outranked every layer the repo checked in. Both tools now agree with a direct run, and `--settings` is
  reserved for the case it exists for — a `JB_SETTINGS_PATH` naming a file `jb` cannot find itself. One
  visible side effect: a `{solution}.DotSettings.user` now outranks `{solution}.DotSettings`, which is
  `jb`'s own default and matches the IDE.
- `resharper_reset_cache` no longer queues behind the background cache pre-warm. The rule that a call
  you are waiting on outranks speculative work was written into the code that runs `jb`, so the one tool
  that runs no `jb` at all was the one it never covered: a reset took the queue lock directly and waited
  its turn — for up to the whole run cap, behind a pass rebuilding exactly what it had been called to
  delete. That precedence now belongs to the callers rather than to the spawn, and is taken by
  everything you are waiting on whether or not it runs `jb`. Reclaiming is faster than queueing but not
  instant: the cache generation comes free only once the cancelled `jb` has been killed and reaped, so a
  reset can still spend a second or two on the lock — against minutes before. Admitted that quickly, it
  can also meet a dying `jb` still holding memory-mapped cache files, so a delete the filesystem refuses
  is now retried briefly before being reported.
- A call that cancels the background pre-warm is no longer denied the warm cache it was about to be seeded
  from. Cancelling kills the speculative `jb` and the killed run keeps its place in the queue until that
  process tree has been reaped — up to five seconds — while the call went looking for a cache to copy after
  waiting only two. Where the two are different solutions the call's own queue place is uncontended and
  granted at once, so it arrived early, found the cache it wanted still held by the run it had just killed,
  and did what it does at any other doubt: declined silently and took the cold run seeding exists to avoid.
  That is the fresh-worktree case the seeding was built for. The wait is now derived from the reap rather
  than set beside it, so the two cannot drift apart again.
- A `files` entry written as an absolute path now reaches `jb` in a spelling it can match. `--include`
  takes "a set of relative paths" and wildcards — `jb`'s own help text — and matches them against the
  solution model rather than against the disk, so an absolute path arrived as a pattern that could never
  hit. `resharper_cleanup` showed it as exit code 3 and `No items were found to cleanup`; measured in the
  field, 27 absolute paths cleaned nothing while the same 27 passed relative to the solution root cleaned
  every one. `resharper_inspect` was quieter and worse: `jb` exits 0 having matched nothing, so a scan that
  looked at no file at all came back as `No issues found.` Both tools now translate an absolute entry
  against the solution root on the way to `jb`, which is what makes the documented "relative to the solution
  root, or absolute" true rather than aspirational. An entry with no relative form — one on another volume —
  is passed as it was written, and cleanup's report still echoes the path you asked for rather than the
  translated one.
- A `resharper_cleanup` run that `jb` exits non-zero on is now reported as a failed pass. The wording is
  the fix: `jb`'s own is `No items were found to cleanup`, and an error quoting that verbatim reads as
  "nothing needed changing" to an agent that has just edited the files it named — which is how a whole
  cleanup pass came to be skipped after 27 edits. The error now says outright that no file was cleaned up,
  lists the `--include` patterns `jb` was given (the translated spelling, which is the one thing the caller
  cannot otherwise see), and names what still causes a run to match nothing: a file that is on disk but
  belongs to no project in the solution. A run killed at the ten-minute cap keeps its own message — it may
  already have rewritten files, so it cannot claim otherwise.

## [1.3.0] - 2026-08-13

### Added

- `resharper_reset_cache` drops the solution's ReSharper cache generations, so the next inspection or
  cleanup rebuilds its analysis from cold. ReSharper's solution-wide index can miss invalidating the
  consumers of a declaration you reshaped, and it then reports `Cannot resolve symbol` in files nobody
  edited while the compiler and the test suite stay green — indefinitely, because re-running returns the
  identical set. The ReSharper CLI exposes no cache-invalidation option of its own (`--caches-home` only
  chooses where caches live), and this server picks that directory, so the operation belongs here. It takes
  the same queue lock the analysis tools take, and holds it across the delete: a reset waits for a `jb` run
  in flight rather than deleting the cache underneath it, and a run starting meanwhile waits for the reset —
  which deleting the directories by hand cannot do. It also reclaims the cold generations a concurrent `jb`
  forks and abandons. It drops only what provably belongs to the solution it was pointed at: `jb`'s directory
  names record a hash of the solution's full path, which the server reproduces, so a cache home shared by two
  checkouts of one repository is ordinary — the generations carrying another path's hash are named in the
  report and left where they are, and a hash matching nothing deletes nothing.
- A solution with no ReSharper cache is now seeded from a warm one belonging to another copy of the same
  solution, before the run that would otherwise build it from cold. `jb` keys a cache generation by the
  solution's absolute path, so every fresh `git worktree` — or clone, or copied directory — is cold however
  much analysis of the same code sits beside it in the cache home, and the pre-warm cannot cover it: it warms
  whatever solution the *server's* directory resolves, while a session working in a worktree passes its own
  `solutionPath` on every call. That leaves the one case where a cold run is guaranteed as also the one most
  likely to exceed the cap and come back as a timeout. Nothing inside a cache binds it to a path — `jb`
  validates a generation against its own format version and rebuilds it in place when that does not match —
  so the copy is either accepted and re-keyed by the run that opens it or discarded exactly as an absent
  cache would be. Re-keying is not free: it costs `jb` about a minute, near enough the same on a small
  solution as on a large one, in exchange for the difference between a cold analysis and a warm one. That
  makes this a decisive win where a cold run would have passed the cap and a poor trade on a solution that
  goes cold in a minute anyway, which is worth knowing before reading the copy as free speed. Two guardrails
  keep it honest. The generation to copy is named by the marker a successful run writes rather than derived
  from `jb`'s undocumented directory naming, so if that naming ever changes there is no name to record, no
  donor to find, and the feature switches itself off instead of copying to a directory nothing will read.
  And a reset still means cold: `resharper_reset_cache` leaves a record that suppresses seeding entirely
  until a run against that solution succeeds, so the tool for getting rid of a bad index cannot be undone by
  an optimisation. Everything else about it is best-effort and silent — no donor, a donor another `jb`
  currently holds, a copy that fails part way, or a generation already in place (a stunted one included)
  all end in the cold run the call was going to have.
- An inspection result that contains compilation errors now leads with a note saying how to read them:
  build the solution, and if the compiler accepts the code the index is stale and the errors are phantoms
  that will repeat on every re-run. It names the resolved cache directory, which the caller cannot derive,
  and the tool that clears it. The note states the discriminator rather than a conclusion — this server
  cannot tell a phantom from a genuine compilation error, and an agent mid-edit usually has the real kind.
  Like the configuration warnings, it is charged to the output budget before the result is rendered, so it
  survives every step of detail reduction.
- The `resharper://guides/setup` resource gains a "Phantom compilation errors" section covering the same
  ground at length: the signature, the build test that identifies it, the cure, and why `--no-build` is not
  the cause — `jb` builds its solution model from source for a solution's own projects and resolves
  declarations added since the last build without one.

### Changed

- Each `jb` run is now capped at **10 minutes** rather than 5, and the new `RESHARPER_MCP_TIMEOUT_SECS`
  moves that cap (in seconds, default `600`, clamped to 60–86,400; anything unreadable falls back to the
  default). It takes the server's own prefix rather than `JB_`, because that is the line the existing
  variables already draw: every `JB_` one becomes something `jb` is told, and this one arms a kill timer
  `jb` never learns about. Seconds rather than minutes so a cap can be pitched just above a run you have
  timed, and a cap that is not a round number of minutes is reported back the way you set it — a run
  stopped at 455 seconds says "7 minutes 35 seconds", not "8 minutes". The old cap sat inside the
  normal working range of a cold whole-solution analysis rather than beyond it, so it killed runs that were
  making steady progress. Nothing outside this server ever asked for it either — an MCP client's own
  tool-call limit is far longer — which means a timeout here has always been this server's own choice, and
  is now one you can change. The same value bounds the queue wait, so a call stays bounded by wait + run.
- A run that hits the cap now says whose cap it is, names the variable that raises it, and heads off the
  retry that cannot work: `jb` analyses the whole solution whatever the report is scoped to, so narrowing a
  retry with `files` makes it no faster. The setup guide previously advised exactly that; it no longer does.
- A tool call that hits the run cap now arms another background cache pre-warm, against the solution that
  timed out. Previously the first tool call to reach the runner retired speculative work for the life of the
  process, so the moment it was worth most — the cache part-built, the error advising a retry, the user idle
  reading it — was exactly the moment the server had guaranteed it would never run again. Measurement
  settled the assumption that advice rests on: a run killed at the cap does leave a cache a retry resumes
  from, and the resumed run reports the same issues a clean cold run does. It does not resume from exactly
  where it stopped, though — whatever was in flight when it was killed is redone — so several capped runs
  still cost appreciably more than one run allowed to finish, and raising `RESHARPER_MCP_TIMEOUT_SECS`
  remains the fix when a cold analysis simply takes longer than the cap. The pre-warm still runs one pass at
  a time, still stands down while a real call is in flight, and re-arms on nothing else: not a timer, not
  each message, and never on its own timeout.

### Fixed

- A background cache pre-warm that runs out of that budget is an ordinary skip rather than an unexpected
  failure. Speculative work has nobody waiting on it, and a solution large enough to exceed the cap from
  cold is precisely the one pre-warming exists for, so it no longer logs a warning for its own best case.

## [1.2.1] - 2026-08-07

### Fixed

- A `.DotSettings` file that ReSharper reads happily no longer turns the declared-cleanup-profile feature
  off. The profile a solution declares was read with a strict XML parser, and ReSharper's own settings
  reader is more forgiving than the XML spec — most visibly about comment content, where a `--` inside
  `<!-- … -->` is illegal XML that ReSharper and `jb` accept without complaint. One such comment in a
  settings file meant every `resharper_cleanup` call that omitted `profile` silently applied
  `Built-in: Full Cleanup` instead of the profile the repo had declared — measured in the field, and the
  broader profile strips exactly the named arguments a narrowed profile is usually defined to protect. A
  file that fails a strict parse is now retried with its comments discarded, so it reads as `jb` reads it.
  A well-formed file takes the same path it always did and cannot be affected by the retry.
- Configuration that was silently dropped is now reported in the tool result instead of only in a log file.
  A settings file this server cannot read at all makes `resharper_cleanup` lead with a warning naming the
  file and the fault: the fallback profile has already rewritten the files by the time the result is
  rendered, so it is not something to leave in a log. `resharper_inspect` stays quiet about that one — `jb`
  received the settings file and parses it itself, so inspection severities are unaffected. The other
  case, `JB_SETTINGS_PATH` naming a file that does not exist, drops the settings from the run entirely and
  so is reported by both tools. The warning is charged to the output budget before the result is rendered,
  so it survives every step of detail reduction and the total stays within budget as before.
- A `files` element that joins several paths — `["a.cs, b.cs"]` where `["a.cs", "b.cs"]` was meant — now
  works on both tools instead of throwing the call away. It is a mistake the array parameter invites, and it
  failed in two different ways: `resharper_cleanup` rejected the joined string as a file that does not exist,
  while `resharper_inspect` handed it to `jb` as one pattern, matched nothing, and reported "No issues
  found." — a clean bill of health for a scan that never looked at the files asked for. Elements are now
  split on `;` and `,` at the tool edge, with surrounding whitespace trimmed. An element that names a file
  that really is on disk is never reinterpreted, so a legitimate `Foo,Bar.cs` still cleans up and splitting
  can only rescue a call that was otherwise certain to fail; and when a fragment does not exist, the error
  names that fragment rather than the whole joined string.

## [1.2.0] - 2026-08-05

### Added

- The server now warms the ReSharper cache in the background as soon as a client connects, so the cold
  run a session's first call would otherwise pay for happens during the idle minutes before it. It targets
  exactly the solution a call with no `solutionPath` would find and does nothing when there is none, runs
  at most once per session, and is skipped when any `jb` run against that solution's cache succeeded within
  the last hour — a real tool call refreshes that just as a pre-warm does, so working in a repo does not
  earn its next session a redundant analysis. A tool call arriving while one is in flight **cancels** it and
  takes the cache, waiting only for `jb` to be killed and reaped rather than for the run to finish, so
  within one server process pre-warming can never make a call slower. Across processes it can: a pre-warm in
  another server cannot be cancelled, so a call there queues behind it exactly as it queues behind another
  session's real call. It is a full solution analysis, and `RESHARPER_MCP_PREWARM=off` turns it off. Being
  honest about the size of this: it does not make warm runs faster — 1.1.1's run serialization did that —
  it moves the cold cost off the first call and onto session start.

### Changed

- A solution-wide `resharper_inspect` now comes back complete instead of cut off. Previously a result
  over the output budget was chopped at a line boundary, so the tail of the list simply vanished and the
  only remedy was to re-run more narrowly — and the budget was usually spent on near-duplicates: one real
  run returned 150 issues across 24 files, **120 of them the same rule** repeated across four DTO files,
  each message naming a different property. That run now returns complete at about 30% of its former size,
  every issue counted and every file named. Issues repeating a rule within a file collapse to one line
  carrying their line numbers and one example message
  (`` `NotAccessedPositionalProperty.Global` [WARNING] x30, lines 13-42 ``), and if that is still too
  large the response steps down further — the eight most-affected files, then a rules-and-files rollup,
  then a one-line summary — each step naming itself in a `DETAIL REDUCED` note. **A truncated result was
  an incomplete list of issues; a reduced one is complete but less detailed**, so it is safe to conclude
  from. A scan already within budget is byte-for-byte unchanged, and hard truncation remains only as a
  last-resort backstop. `resharper_cleanup`, which has degraded this way since 1.0.2, now describes its own
  reduction per level in the same note rather than a generic one.

### Fixed

- Two sessions working on one solution no longer make each other slow, or leave stale caches behind.
  ReSharper lets only one `jb` at a time use a solution's cache, and a second one that tries does not
  wait — it silently forks an empty copy of the cache and starts from cold. Both runs then pay the
  cold-cache cost, and the one that lost the race usually times out: in a measured case it had covered 79
  of a solution's 208 files by the time the run holding the warm cache had finished all 208. The forked
  copy is permanent, too, at a few hundred megabytes each. Calls against one solution now queue for the
  warm cache instead of forking, across every client on the machine, since it is the cache that is
  contended rather than the server. A call waits up to 5 minutes for a run already in flight before
  starting its own 5-minute run; if that wait runs out, the error says a run against that solution is
  already going rather than starting a second one. Nothing changes when only one call is running.
- A broken `jb` installation is no longer mistaken for a working one, and no longer stops the search.
  An installation was accepted on the strength of its probe exiting cleanly, so one that exited 0 without
  printing a version was adopted and then failed on the first real call; worse, one that printed nothing
  at all ended discovery with an unhandled error rather than moving on. Both now count as a failed
  candidate, so the search continues to the next location and, when none work, ends at the same message
  naming everything it tried and how to install the ReSharper command line tools.

## [1.1.1] - 2026-08-01

### Changed

- Moved to version 2.0.0 of the MCP C# SDK, which implements the 2026-07-28 protocol revision. A client
  that speaks it discovers the server through `server/discover` rather than the `initialize` handshake
  and holds no session, reaching the same two tools, prompt, and guide resources with the same schemas
  and the same argument handling. Clients on earlier revisions are unaffected: an `initialize` handshake
  returns exactly what it returned before.

## [1.1.0] - 2026-07-27

### Added

- Claude Code plugin install. This repository doubles as a single-plugin marketplace, so
  `/plugin marketplace add andypgray/resharper-cli-mcp` followed by
  `/plugin install resharper-cli-mcp@resharper-cli-mcp` brings up both tools, the
  `derive_style_guide` prompt, and both guide resources without hand-editing `.mcp.json`. The plugin
  starts the server with `dotnet dnx`, which fetches the package from NuGet on first use, so there
  is no `dotnet tool install` step for the wrapper itself, and it points ReSharper's caches at the
  plugin's own data directory rather than your source tree. A new
  [PRIVACY.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/PRIVACY.md) states what the
  server does and does not send anywhere: nothing leaves the machine.
- `resharper://guides/setup` MCP resource — an on-demand guide to *running* the server, as opposed to
  configuring ReSharper: installing and locating `jb` (`PATH`, then `~/.dotnet/tools`, because the child
  process an MCP client starts often does not inherit the shell's `PATH`), which solution a call runs
  against and why discovery is top-level-only with no parent walk, why the first call is slow and what the
  5-minute cap means, how `MAX_MCP_OUTPUT_TOKENS` caps output and why a truncated inspection is an
  incomplete list of issues rather than a clean solution, every environment variable, and where logs go.
  Two guides rather than one broadened one: each names a single routing condition, so a "why did this time
  out?" pull does not also load 8 KB of `.DotSettings` prose, and vice versa.

### Changed

- The always-loaded server instructions are down from 1,960 to 992 bytes (−49%) — a **measured 262 fewer
  resident tokens in every session** that connects this server, including the majority that never call a
  tool. Clients that defer tool schemas (Claude Code 2.1.220 and later) keep only tool *names* resident and
  fetch each schema on demand, but server instructions still ride verbatim in every session's system
  prompt, so the instructions now carry only the cross-tool routing that no single schema can state: which
  tool is read-only, the call-cleanup-once-at-the-end batching rule, that cleanup cannot change behavior
  and so needs no re-inspect or rebuild, and one conditional signpost per guide resource. The per-tool
  restatements, the `files` glob syntax, the `severity` value list (already carried by the schema's `enum`
  array), solution discovery, the cold-cache note, and the timeout all moved into the tool schemas and the
  two guide resources. The unofficial-wrapper notice moved too: it stays where a human reads it — the
  NuGet description, the README's first paragraph, and `.mcp/server.json` — and still travels to agents in
  both guide resources and in the server title negotiated on `initialize`. No behavior change.
- Tool parameter descriptions gained the gotchas that left the instructions, at per-fetch instead of
  per-session cost: `solutionPath` now records that it overrides `JB_SOLUTION_PATH` and working-directory
  discovery (and no longer says "to analyze", which was never right for the mutating tool),
  `resharper_inspect`'s `files` shows the glob format, its `severity` warns that `Error` is ReSharper's
  compilation-error level rather than a tier of high-priority warnings so raising to it usually reports
  nothing, and `resharper_cleanup`'s `files` records that a non-wildcard path that does not exist fails the
  whole call before anything is rewritten.
- The `resharper://guides/configuration` resource and its description now scope themselves explicitly to
  what ReSharper *enforces* and point onward to `resharper://guides/setup` for anything about running the
  server.
- `resharper_cleanup` now takes its default profile from the solution instead of always falling back to
  `Built-in: Full Cleanup`. With no `profile` argument it uses the profile named under
  `/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue` in the resolved settings file —
  ReSharper's own "profile to use when nobody picks one" — and `Built-in: Full Cleanup` only when no
  settings file declares one. This is what makes a repo-wide narrowing stick: a repo that defined a
  profile to protect a deliberate style had to get every caller to pass its name, and an agent that did
  not know the profile existed silently got Full Cleanup and the rewrite the profile was meant to
  prevent. The `profile` argument still overrides, per call. An unreadable or malformed settings file
  degrades to the built-in default with a warning rather than failing the call.
- The `resharper://guides/configuration` resource gains the profile-resolution order, the fact that a
  cleanup profile is a full enumeration rather than a diff against a built-in (an omitted task reads as
  off), why an inspection severity provably cannot reach cleanup (cleanup overrides the severity of
  every rule its enabled modules need), and that the trailing newline at EOF is an editorconfig-only
  knob with no `.DotSettings` equivalent.
- Solution, settings, and cleanup-profile discovery now runs on every call instead of being cached per
  solution for the life of the server process. Without this the declared-profile feature above could not
  be adopted in the session that adopts it: an agent following the configuration guide would write the
  `SilentCleanupProfile` entry, call `resharper_cleanup`, and still get Full Cleanup from the cached
  pre-edit config — the exact rewrite the profile was defined to prevent — until the client restarted the
  server. The `jb inspectcode --version` probe, the one genuinely expensive step, is still cached for the
  process; what now repeats is a directory enumeration, a few existence checks, and a small XML read,
  against a `jb` run that takes seconds to minutes.
- The `derive_style_guide` prompt teaches declaring the profile under `SilentCleanupProfile` rather than
  passing `profile` on every call, and warns that a profile is a full enumeration of tasks rather than a
  diff against a built-in — it is the surface most likely to author the `.sln.DotSettings` the rest of
  this release reads.

### Fixed

- A blank `profile` argument (`""` or whitespace) no longer reaches jb as `--profile=` and fails the run.
  It now reads as unspecified and falls through to the solution's declared profile, matching how a blank
  *declared* profile already read; a padded name is trimmed. The rule has one definition, shared by both
  entry points.
- A blank entry in `resharper_cleanup`'s `files` now fails as a user error naming the offending position,
  instead of surfacing an `ArgumentException` from path resolution as an internal server error.

## [1.0.2] - 2026-07-15

### Added

- `resharper://guides/configuration` MCP resource — an on-demand guide to how ReSharper configuration
  works: inspection severities drive `resharper_inspect` while the cleanup profile drives
  `resharper_cleanup` (two independent axes — hiding an inspection does not stop cleanup), how to protect
  a deliberate style from cleanup, settings and `.editorconfig` discovery, and the `.DotSettings` key
  shapes. The always-loaded server instructions carry only a short signpost pointing at it.

### Changed

- `resharper_cleanup` now reports which files it actually changed on disk, hashing each concrete file
  before and after the run and classifying it as changed, unchanged, status-unknown, or a wildcard
  pattern — instead of a bare "completed" line that hid whether cleanup rewrote a file. Solution-wide
  runs degrade the per-file detail progressively to stay within the output budget. Purely observational:
  the cleanup itself is unchanged.

## [1.0.1] - 2026-07-10

### Fixed

- Package the MCP manifest at `.mcp/server.json`. It was landing at `.mcp//server.json` (a double
  slash) because the csproj `PackagePath` used a backslash and packing runs on Linux, so nuget.org
  could not generate the VS Code MCP server configuration. No change to server behavior.

## [1.0.0] - 2026-07-09

Initial public release — an MCP stdio server wrapping JetBrains' ReSharper Command Line Tools.
Unofficial; not affiliated with or endorsed by JetBrains.

### Added

- `resharper_inspect` tool — runs ReSharper InspectCode over the solution and returns the issues
  grouped by file (read-only). Scope with a `files` glob and filter by `severity`.
- `resharper_cleanup` tool — runs ReSharper CleanupCode to reformat and normalize the given files
  in place, using a named cleanup `profile`.
- `derive_style_guide` prompt — walks an agent through deriving an intentional, `.editorconfig`-first
  ReSharper/StyleCop style guide for a legacy codebase, validated with `resharper_inspect`.
- Solution, settings, and `jb` discovery from the working directory or the `JB_SOLUTION_PATH`,
  `JB_SETTINGS_PATH`, `JB_CACHE_HOME`, `JB_EXTENSIONS`, and `JB_EXTENSION_SOURCE` environment
  variables.
- Output truncation honoring the client's `MAX_MCP_OUTPUT_TOKENS` budget.
- Ships as a .NET global tool and MCP server (`PackAsTool` + `PackageType=McpServer`), published to
  NuGet with SLSA build provenance and registered on the MCP registry.

[Unreleased]: https://github.com/andypgray/resharper-cli-mcp/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.5.0
[1.4.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.4.0
[1.3.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.3.0
[1.2.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.2.1
[1.2.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.2.0
[1.1.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.1.1
[1.1.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.1.0
[1.0.2]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.0
