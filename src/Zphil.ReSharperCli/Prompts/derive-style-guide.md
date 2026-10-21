# Derive a ReSharper style guide for an existing codebase

Your goal is an intentional code-style configuration for this C# solution. With it in place, a ReSharper
inspection reports deviations from the team's house style instead of noise from unconfigured defaults.
Work through the steps below with this server's `resharper_inspect` tool and your own file reading and
editing.

Describe this server accurately to the user. It wraps JetBrains' ReSharper command-line tools and is
unofficial — not affiliated with or endorsed by JetBrains. Those tools have no "infer settings from
existing code" command, and this server does not infer settings for you. You derive the rules from evidence
in the code and in the tooling already present. The server's part is validation. `resharper_inspect` tells
you whether a proposed config makes the remaining inspection output meaningful. When you report back, say that
you inferred the style and validated it with the inspect loop, and never imply that the tool learned it.

## 0. Prefer the official IDE detector when an IDE is available

If anyone on the team has ReSharper or Rider installed, use JetBrains' own Detect Code Style Settings
feature. It learns formatting and naming rules from a sample as large as the whole solution and writes them
to `.editorconfig` (C# and C++). In ReSharper it is `ReSharper | Edit → Detect Code Style Settings`. In
Rider it is `Settings → Editor → Code Style → C# → Auto-Detect Code Style Rules` for the whole solution, or
Alt+Enter on a selection and then `Reformat and cleanup | Detect code style settings`. It takes one click
and its output is authoritative, so prefer it for the baseline. Use this recipe when:

- no IDE or licence is available (a headless agent, CI, a new contributor),
- the style has to agree with StyleCop and the other analyzers already in the repo, or
- inspection severities need tuning through the `resharper_inspect` loop.

The detector does neither of the last two, so use both. Run the detector for the `.editorconfig` baseline,
then use steps 1–7 to reconcile it with the existing tooling and validate it.

## 1. Discover the tooling already in place

Configuration already committed to the repo is an authoritative statement of intent. Glob and read:

- `.editorconfig` and `.globalconfig` (existing style and analyzer-severity rules).
- `stylecop.json` and `*.ruleset` (StyleCop settings and legacy rule sets).
- any existing `*.DotSettings` or `*.sln.DotSettings` (ReSharper layers and cleanup profiles).
- `Directory.Build.props` and `Directory.Build.targets` (`AnalysisLevel`, `EnforceCodeStyleInBuild`,
  `TreatWarningsAsErrors`, `CodeAnalysisTreatWarningsAsErrors`).
- `*.csproj` and `Directory.Packages.props` (analyzer package references such as `StyleCop.Analyzers`,
  `Roslynator.Analyzers` and `Meziantou.Analyzer`).

Already-configured tooling wins. The guide encodes and extends it, and never contradicts it. Where StyleCop
is active, map its rules (member ordering, `this.` qualification, naming, file headers, using placement)
onto their ReSharper and editorconfig equivalents so the two tools agree. A config under which cleanup
rewrites what StyleCop just flagged is worse than no config.

## 2. Sample the code and measure consistency

Infer the de facto convention for every dimension the existing config is silent on. Read a representative
spread of files (old and new, a few per project, across several modules) and check:

- indentation width and tabs or spaces; Allman or K&R braces
- `var` or explicit type; file-scoped or block-scoped namespaces
- `using` placement (inside or outside the namespace) and sorting; `System.*` first
- expression-bodied members; trailing commas; braces on single-statement blocks
- naming (interface `I` prefix, `_camelCase` private fields, PascalCase constants, async `Async` suffix)

For each dimension, record the split, for example "72% file-scoped, 28% block-scoped". Adopt a clear
supermajority. Where the files mix conventions with none dominant, mark the dimension conflicted and take it
to step 3. Never pick a side of a real split on your own.

## 3. Resolve conflicts with the user

Writing a style config changes how cleanup rewrites code, and this server never guesses a profile for a
change like that, so do not guess. For every dimension you marked conflicted, stop and ask the user. Present
the observed split and offer two choices:

- **(a) Enforce a convention.** Name the majority option and let the user accept it or pick another. The
  `.editorconfig` rule and its severity then flag the minority form as a cleanup target.
- **(b) Leave the dimension unconfigured.** Write no code-style rule for it, so `resharper_cleanup` has
  nothing to enforce and does not rewrite the codebase over a convention the team has not agreed on. Lowering
  the inspection severity to `none` is a separate setting. It hides the squiggle but does not stop cleanup,
  which reads the style rule and not the severity.

If the host supports interactive questions, use them. Otherwise present a numbered list of decisions and
wait. Write nothing until every conflict is resolved, because enforcing one side of a real split without
asking is the failure this step prevents.

## 4. Write `.editorconfig` first

`.editorconfig` works across the ReSharper command-line tools, StyleCop.Analyzers, Roslyn, `dotnet format`
and Rider, so it ties the guide to no single tool. The ReSharper command-line tools read it from the source
tree with no flag needed. Write or extend the root `.editorconfig`:

- `dotnet_*` and `csharp_*` keys for formatting and style (indentation, `var`, namespaces, `using` sorting,
  expression bodies, braces, trailing commas).
- `dotnet_naming_rule.*`, `dotnet_naming_symbols.*` and `dotnet_naming_style.*` triples for naming.
- `dotnet_diagnostic.<id>.severity` and ReSharper's `resharper_*_highlighting` keys for severities.

Take exact keys from ReSharper's EditorConfig properties reference, which documents each property. Put in
`*.sln.DotSettings` only what `.editorconfig` cannot express:

- ReSharper-only inspection severities and formatter settings with no editorconfig key, and
- cleanup-profile definitions, since a cleanup profile can be defined only there. Declaring one under
  `/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue` also makes it the profile every
  `resharper_cleanup` call uses by default, including calls from an agent that does not know it exists. The
  tool's `profile` argument overrides it per call.

JetBrains publishes no formal `.DotSettings` XML schema, and the IDE generates the file. Keep it minimal, and
adapt an existing `.DotSettings` example instead of writing new XML by hand. Document what each file contains
and why, and keep the two in sync.

## 5. Validate with the inspect loop

This loop stands in for the missing "infer" command. Run `resharper_inspect` with a `files` glob scoped to a
representative folder and a raised `severity`. For each rule that fires often, decide which it is:

- If it is a real deviation from the house style, keep it firing, since it is a cleanup target.
- If it is a ReSharper default that disagrees with the house style, adjust the config so it stops firing.

Re-run until every issue that remains is one the team wants flagged. Iterating against real inspection output
is how the config becomes trustworthy, so do not settle on a config by guessing.

## 6. Set the legacy baseline and keep cleanup safe

A large legacy codebase will not be clean on day one, and that is fine. For each significant rule, decide
whether to fix it now or accept it for now. For an accepted rule, suppress it or lower its severity with a
comment saying it is a baseline and not an endorsement, so the debt stays visible.

To stop `resharper_cleanup` from rewriting code you did not touch (reordered members, rewritten usings across
untouched files), define a narrow profile such as `Custom: No Reordering` in `*.sln.DotSettings`. Declare it
there under `SilentCleanupProfile` so it becomes the default for every call, and no caller has to remember to
pass it. A profile lists every cleanup task, and any task its XML omits reads as off, so copy the shape of an
IDE-generated profile instead of writing a short one by hand. Never run a full cleanup across the whole legacy
tree at once. Clean per change, in one call that lists only the files the task edited.

## 7. Report the outcome

Summarize:

- the files written or extended (`.editorconfig`, `*.sln.DotSettings`), committed together.
- the codified house style, dimension by dimension.
- the baseline, meaning which rules are kept firing and which are suppressed or lowered, and why.
- the conflict decisions from step 3.
- anything still needing a human decision.

---

## Authoritative references

Fetch these for exact keys and semantics rather than relying on memory:

- ReSharper's EditorConfig properties, the per-property reference to author from:
  <https://www.jetbrains.com/help/resharper/EditorConfig_Properties.html>
- ReSharper's Use EditorConfig: <https://www.jetbrains.com/help/resharper/Using_EditorConfig.html>
- Detect Code Style Settings in ReSharper and Rider, the IDE-only feature that derives a style from existing
  code, to prefer when an IDE is available:
  <https://blog.jetbrains.com/dotnet/2018/12/05/detection-code-styles-naming-resharper/>
- ReSharper's Manage and share settings, on `.DotSettings` layers (JetBrains publishes no formal XML schema
  for the file): <https://www.jetbrains.com/help/resharper/Sharing_Configuration_Options.html>
- StyleCop.Analyzers' `stylecop.json` configuration:
  <https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md>
  and its JSON schema:
  <https://raw.githubusercontent.com/DotNetAnalyzers/StyleCopAnalyzers/master/StyleCop.Analyzers/StyleCop.Analyzers/Settings/stylecop.schema.json>
- The ReSharper command-line tools' InspectCode: <https://www.jetbrains.com/help/resharper/InspectCode.html> and
  CleanupCode: <https://www.jetbrains.com/help/resharper/CleanupCode.html>
