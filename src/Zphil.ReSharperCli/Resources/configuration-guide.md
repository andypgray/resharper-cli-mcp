# Configuring ReSharper for this server

> Unofficial wrapper: not affiliated with or endorsed by JetBrains.

## The two axes (the load-bearing fact)

The two tools read two independent configuration axes; almost every surprise comes from tuning one axis
and expecting the other to change.

| Tool | Reads | Controls |
|---|---|---|
| `resharper_inspect` | **inspection severities** | which issues are *reported*, and at what level |
| `resharper_cleanup` | **code style**, applied through its cleanup **profile** | how files are *rewritten* in place |

They do not share a switch:

- Setting an inspection to **`DO_NOT_SHOW`** (or editorconfig severity `none`) removes that issue from
  `resharper_inspect` output. It does **not** stop `resharper_cleanup` from rewriting that style — cleanup
  never consults inspection severities.
- Conversely, defining or narrowing a cleanup profile changes what cleanup rewrites but does **not** change
  what `resharper_inspect` reports.

So "I set the rule to `DO_NOT_SHOW` but cleanup still changes my code" is expected, not a bug: you moved
the inspect axis while cleanup runs on the style axis.

## Protecting a deliberate style from cleanup

Example: you deliberately write **named arguments** and `Built-in: Full Cleanup` keeps stripping them (it
removes positionally-redundant named arguments, and arguments equal to their default value). No inspection
severity will stop that, and the style axis puts up a specific wall here:

**Argument style is binary — `positional` or `named`, with no neutral "leave alone" value.** `named` makes
cleanup *add* names; `positional` makes cleanup *strip* them; neither means "don't touch," so a settings
tweak alone cannot make cleanup leave argument style as-authored.

To leave a style as-authored, use one of these levers — **not** an inspection severity:

1. **Narrow the profile.** Define a custom cleanup profile in the solution's `.sln.DotSettings` that
   leaves the offending task off (e.g. no "Arrange argument style", no "Remove redundant arguments"), and
   pass its name as `resharper_cleanup`'s `profile` argument (for example `Custom: No Reordering`). This is
   the durable, repo-wide fix.
2. **Exclude the file.** `resharper_cleanup` only rewrites the files you list in `files`; simply don't pass
   a file whose style you want frozen. Cleanup is opt-in per path — there is no "everything except this"
   mode.
3. **Disable in source.** For a rewrite ReSharper also reports as an inspection, wrap the code in disable
   comments keyed to that rule ID so cleanup skips it — `// ReSharper disable once RedundantArgumentDefaultValue`
   for one call, or a `// ReSharper disable RedundantArgumentDefaultValue` … `// ReSharper restore RedundantArgumentDefaultValue`
   region. Read the exact rule ID off `resharper_inspect` output. This travels with the file and needs no
   settings file.

Choose (1) for a house rule, (3) for a one-off you must keep, (2) for a file you never want normalized.

## Where settings come from

Both tools resolve an explicit ReSharper settings file in this order:

1. `JB_SETTINGS_PATH`, when set and the file exists,
2. `{solution}.DotSettings` beside the solution (for example `App.sln.DotSettings`),
3. `GlobalSettingsStorage.DotSettings` in the JetBrains shared directory,
4. none.

**On top of that, `jb` automatically honors `.editorconfig` from the source tree** — no flag, no
`--settings` needed. Because `.editorconfig` is also read by StyleCop.Analyzers, Roslyn, `dotnet format`,
and Rider, it is the portable default home for style rules; spill into `.sln.DotSettings` only what it
cannot express — ReSharper-only knobs and cleanup-profile definitions.

## The two DotSettings shapes you will edit

`.DotSettings` is IDE-generated XML with no published schema; keep edits minimal and copy an existing
entry rather than inventing one.

- **An inspection severity** (inspect axis) is a single entry:

  ```xml
  <s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=RuleId/@EntryIndexedValue">DO_NOT_SHOW</s:String>
  ```

  `RuleId` is the identifier `resharper_inspect` prints for the issue (for example
  `RedundantUsingDirective`). Values are `ERROR`, `WARNING`, `SUGGESTION`, `HINT`, `DO_NOT_SHOW`. The
  editorconfig equivalents are `resharper_<rule>_highlighting = error|warning|suggestion|hint|none` and,
  for Roslyn analyzers, `dotnet_diagnostic.<id>.severity`.

- **A cleanup profile** (style axis) lives in the same file under
  `/Default/CodeStyle/CodeCleanup/Profiles/=<ProfileName>/…`. Its human name (for example
  `Custom: No Reordering`) is exactly what you pass as the `profile` argument. `Built-in: Full Cleanup`
  is the default and touches everything.

## Authoring a full style guide

To derive an *intentional* configuration for a whole codebase — sample the de-facto conventions, reconcile
them with StyleCop, and validate with the inspect loop — use this server's **`derive_style_guide`** prompt:
the complete, evidence-first recipe.

## Authoritative references (JetBrains)

Consult these for exact keys and behavior rather than relying on memory.

- **EditorConfig properties** (per-property reference — inspection severities, formatter knobs, and the
  argument-style keys): <https://www.jetbrains.com/help/resharper/EditorConfig_Properties.html>
- **Use EditorConfig** (how ReSharper reads `.editorconfig`, including from the source tree):
  <https://www.jetbrains.com/help/resharper/Using_EditorConfig.html>
- **Manage and share settings** (`.DotSettings` layers and their precedence):
  <https://www.jetbrains.com/help/resharper/Sharing_Configuration_Options.html>
- **Configure code inspection settings** (the inspect axis — severities):
  <https://www.jetbrains.com/help/resharper/Code_Analysis__Configuring_Warnings.html>
- **Ignore parts of the code** (the in-source `// ReSharper disable` / `// ReSharper restore` lever):
  <https://www.jetbrains.com/help/resharper/Ignore_Parts_of_Code.html>
- **Code cleanup profiles** (define or narrow a cleanup profile — the style axis):
  <https://www.jetbrains.com/help/resharper/Reference__Options__Tools__Code_Cleanup.html>
- **Code Syntax Style: Named/Positional Arguments** (the binary `positional`/`named` setting this guide
  warns about): <https://www.jetbrains.com/help/resharper/Argument_Style.html>
- ReSharper CLI — **InspectCode**: <https://www.jetbrains.com/help/resharper/InspectCode.html> and
  **CleanupCode**: <https://www.jetbrains.com/help/resharper/CleanupCode.html>
