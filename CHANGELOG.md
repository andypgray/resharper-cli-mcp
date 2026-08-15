# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/andypgray/resharper-cli-mcp/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.3.0
[1.2.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.2.1
[1.2.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.2.0
[1.1.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.1.1
[1.1.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.1.0
[1.0.2]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.0
