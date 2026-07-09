# Derive a ReSharper style guide for an existing codebase

Your goal is an **intentional** code-style configuration for this C# solution, so that a ReSharper
inspection reports *real* deviations from the team's house style instead of noise from
un-configured defaults. Work the steps below using this server's `resharper_inspect` tool together
with your own file reading and editing.

**Be honest about what this server does — this is load-bearing, not a disclaimer.** It wraps
JetBrains' ReSharper command-line tools and is **unofficial — not affiliated with or endorsed by JetBrains.**
Those command-line tools have **no "infer settings from existing code" verb**, and this server
**does not infer settings for you.** *You* derive the rules from evidence in the code and the tooling
already present; the server's role is to **validate** — `resharper_inspect` tells you whether a
proposed config makes the remaining inspection output meaningful. When you report back, say plainly
that you inferred the style and validated it with the inspect loop; never imply the tool learned it.

## 0. Prefer the official IDE detector when an IDE is available

If anyone on the team has ReSharper or Rider installed, JetBrains' first-party
**`ReSharper | Edit → Detect Code Style Settings`** learns formatting and naming rules from a sample
as large as the whole solution and writes them straight to `.editorconfig` (C#/C++). That is one
click and authoritative — prefer it for the baseline. Reach for this recipe when:

- no IDE or licence is available (a headless agent, CI, a new contributor), **or**
- you need what the detector does *not* do: reconcile the style with **StyleCop** and other
  analyzers already in the repo, and tune **inspection severities** through the `resharper_inspect`
  loop.

The two compose well: run the detector for the `.editorconfig` baseline, then use steps 1–7 here to
reconcile it with existing tooling and validate it.

## 1. Discover the tooling already in place — it wins

Configuration already committed to the repo is an authoritative statement of intent. Glob and read:

- `.editorconfig`, `.globalconfig` — existing style and analyzer-severity rules.
- `stylecop.json`, `*.ruleset` — StyleCop settings and legacy rule sets.
- any existing `*.DotSettings` / `*.sln.DotSettings` — ReSharper layers and cleanup profiles.
- `Directory.Build.props` / `Directory.Build.targets` — `AnalysisLevel`,
  `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`, `CodeAnalysisTreatWarningsAsErrors`.
- `*.csproj` / `Directory.Packages.props` — analyzer package references (`StyleCop.Analyzers`,
  `Roslynator.Analyzers`, `Meziantou.Analyzer`, …).

**Already-configured tooling wins: the guide encodes and extends it, never contradicts it.** Where
StyleCop is active, map its rules (member ordering, `this.` qualification, naming, file headers,
using placement) onto their ReSharper / editorconfig equivalents so the two tools **agree** rather
than fight — a config where cleanup re-churns what StyleCop just flagged is worse than no config.

## 2. Sample the code and measure consistency

For every dimension that config is silent on, read a representative spread — old and new files, a
few per project, not just one module — and infer the *de-facto* convention:

- indentation width and tabs-vs-spaces; Allman vs K&R braces
- `var` vs explicit type; file-scoped vs block-scoped namespaces
- `using` placement (inside/outside namespace) and sorting; `System.*` first
- expression-bodied members; trailing commas; braces on single-statement blocks
- naming: interface `I`-prefix, `_camelCase` private fields, PascalCase constants, async `Async` suffix

For each dimension **record the split** — for example "72% file-scoped / 28% block-scoped". A clear
supermajority → adopt it. A genuine mix with no dominant convention → mark the dimension
**conflicted** and carry it to step 3. **Do not silently pick a side on a real split.**

## 3. Resolve conflicts with the user — do not guess

Authoring a style config is an opinionated, mutating act; the repo's ethos is *read-only is
forgiving, mutation is conservative — never guess a profile.* So for **every** dimension you marked
conflicted, stop and ask the user. Present the observed split and offer two choices:

- **(a) Enforce a specific convention** — name the majority option and let them accept it or pick
  another. The `.editorconfig` rule (and its severity) will then flag the minority form as a cleanup
  target.
- **(b) Leave this dimension unconfigured** — set no rule (or severity `none`) so ReSharper ignores
  it and cleanup will not churn the codebase over a convention the team has not agreed on.

Where the host supports interactive questions, use that mechanism; otherwise present a numbered
decision list and wait. **Author nothing until every conflict is resolved.** Silently enforcing one
side of a real split is exactly the failure this step exists to prevent.

## 4. Author `.editorconfig`-first

`.editorconfig` is portable across ReSharper CLI, StyleCop.Analyzers, Roslyn, `dotnet format`, and
Rider, so it is the lowest-lock-in home for the guide. ReSharper's command-line tools read it from
the source tree automatically — no flag needed. Write or extend the root `.editorconfig`:

- formatting and style: `dotnet_*` and `csharp_*` keys (indentation, `var`, namespaces, `using`
  sorting, expression bodies, braces, trailing commas).
- naming: `dotnet_naming_rule.*` / `dotnet_naming_symbols.*` / `dotnet_naming_style.*` triples.
- severities: `dotnet_diagnostic.<id>.severity` and ReSharper's `resharper_*_highlighting` keys.

Consult the ReSharper *EditorConfig properties* reference for exact keys — it is the authoritative,
per-property source. Spill into `*.sln.DotSettings` **only** what `.editorconfig` cannot express:

- ReSharper-only inspection severities and formatter knobs with no editorconfig key, and
- **cleanup-profile definitions** — a named profile is required to pass as `resharper_cleanup`'s
  `profile` argument.

JetBrains publishes **no formal `.DotSettings` XML schema** — the file is IDE-generated. Treat it as
*generated, not hand-authored*: keep it minimal and adapt an existing `.DotSettings` example rather
than inventing XML. Document what lives in each file and why, and keep the two in sync.

## 5. Validate with the inspect loop — the crux

This is what replaces the missing "infer" command. Run `resharper_inspect` scoped with the `files`
glob to a representative folder, at a raised `severity`. For each rule that fires a lot, decide:

- **real house-style deviation** → keep it firing; it is a legitimate cleanup target, **or**
- **a ReSharper default that disagrees with the house style** → adjust the config so it stops firing.

Re-run until the remaining output is *intentional* — every surviving issue is one the team would
actually want flagged. That convergence is the whole point: you reach a trustworthy config by
iterating against real inspection output, not by guessing.

## 6. Set the legacy baseline and keep cleanup safe

A large legacy codebase will not be clean on day one, and that is fine. For each significant rule
decide **fix-now vs accept-for-now**; for accepted ones, suppress or lower the severity **with a
comment noting it is a baseline, not an endorsement**, so the debt is visible. To stop
`resharper_cleanup` from churning code you did not touch (reordered members, rewritten usings across
untouched files), define a **narrow profile** — e.g. `Custom: No Reordering` — in `*.sln.DotSettings`
and pass its name as `profile`. **Never run a full cleanup across the whole legacy tree in one go**;
clean per-change, batching only the files a task actually edited.

## 7. Report the outcome

Summarize:

- the files written or extended (`.editorconfig`, `*.sln.DotSettings`) — commit them together.
- the codified house style, dimension by dimension.
- the baseline: which rules are kept-firing vs suppressed/lowered, and why.
- the conflict decisions from step 3.
- anything still needing a human decision.

---

## Authoritative references

Fetch these for exact keys and semantics rather than relying on memory:

- ReSharper — **EditorConfig properties** (authoritative, per-property; the primary authoring
  reference): <https://www.jetbrains.com/help/resharper/EditorConfig_Properties.html>
- ReSharper — **Use EditorConfig**: <https://www.jetbrains.com/help/resharper/Using_EditorConfig.html>
- ReSharper/Rider — **Detect Code Style Settings** (the IDE-only "derive from existing code" feature;
  prefer it when an IDE is available):
  <https://blog.jetbrains.com/dotnet/2018/12/05/detection-code-styles-naming-resharper/>
- ReSharper — **Manage and share settings / `.DotSettings` layers** (note: no formal XML schema is
  published — `.DotSettings` is IDE-generated):
  <https://www.jetbrains.com/help/resharper/Sharing_Configuration_Options.html>
- StyleCop.Analyzers — **`stylecop.json` configuration**:
  <https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md>
  and its JSON schema:
  <https://raw.githubusercontent.com/DotNetAnalyzers/StyleCopAnalyzers/master/StyleCop.Analyzers/StyleCop.Analyzers/Settings/stylecop.schema.json>
- ReSharper CLI — **InspectCode**: <https://www.jetbrains.com/help/resharper/InspectCode.html> and
  **CleanupCode**: <https://www.jetbrains.com/help/resharper/CleanupCode.html>
