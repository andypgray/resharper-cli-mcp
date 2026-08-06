# Configuring ReSharper for this server

> Unofficial wrapper: not affiliated with or endorsed by JetBrains.

This guide covers what ReSharper *enforces* — which issues are reported and which styles are rewritten.
It does not cover running the server: for a call that cannot find `jb` or the solution, times out, or
comes back shortened, read the `resharper://guides/setup` resource instead.

## The two axes (the load-bearing fact)

The two tools read two independent configuration axes; almost every surprise comes from tuning one axis
and expecting the other to change.

| Tool | Reads | Controls |
|---|---|---|
| `resharper_inspect` | **inspection severities** | which issues are *reported*, and at what level |
| `resharper_cleanup` | **code style**, applied through its cleanup **profile** | how files are *rewritten* in place |

They do not share a switch:

- Setting an inspection to **`DO_NOT_SHOW`** (or editorconfig severity `none`) removes that issue from
  `resharper_inspect` output. It does **not** stop `resharper_cleanup` from rewriting that style. Cleanup
  does not merely ignore your severity — it *overrides* it, raising every rule its enabled modules need
  back to `SUGGESTION` for the duration of the run. `DO_NOT_SHOW` cannot reach it by construction.
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
   leaves the offending task off — for named arguments that task is `ArrangeArgumentsStyle` — and either
   declare it as the solution's default (see below) or pass its name as `resharper_cleanup`'s `profile`
   argument. This is the durable, repo-wide fix.

   **A profile is not a diff against a built-in.** Any task the profile's XML omits reads back as that
   task's own default, which is *off*. So "Full Cleanup minus one task" means enumerating every task Full
   Cleanup turns on and setting the one you want skipped to `False` — copy the shape from an
   IDE-generated profile rather than writing a short blob and expecting the rest to be inherited. After
   authoring one, verify it: run both profiles over a deliberately messy scratch file and diff the two
   results. They should differ only where you intended.
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
  `/Default/CodeStyle/CodeCleanup/Profiles/=<ProfileName>/@EntryIndexedValue`, as a single XML-encoded
  blob — one `<Profile name="…">` element enumerating the cleanup tasks, not a tree of sub-keys. The
  `name` attribute inside the blob, not the settings key, is what you pass as the `profile` argument.
  `Built-in: Full Cleanup` is the fallback and touches everything.

## Which profile a call runs without a `profile` argument

`resharper_cleanup` resolves its profile in this order:

1. the `profile` argument on the call,
2. `/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue` in the resolved settings file —
   ReSharper's own "profile to use when nobody picks one",
3. `Built-in: Full Cleanup`.

Declaring the profile in `.sln.DotSettings` is what makes a repo-wide narrowing stick: it travels with the
repo and applies to callers who do not know the profile exists. The settings file is re-read on every call,
so a profile you declare now governs the next `resharper_cleanup` — no server restart. Note that
`jb cleanupcode` does **not** read that key: a direct CLI run always defaults to Full Cleanup and needs an
explicit `--profile`.

**If step 2 cannot be read, the cleanup result says so.** ReSharper's settings reader is more forgiving than
the XML spec — most visibly about comment content, where a `--` inside `<!-- … -->` is illegal XML that
ReSharper reads without complaint — so this server tolerates the same thing rather than losing a profile
`jb` itself would have honored. When a settings file is broken past that, the `resharper_cleanup` result
leads with a `WARNING:` naming the file and the fault, because the fallback to `Built-in: Full Cleanup` has
already rewritten the code the declared profile existed to protect. `resharper_inspect` stays quiet about
it: `jb` still received `--settings` and parses that file perfectly well, so inspection severities are
unaffected. The other warning — `JB_SETTINGS_PATH` naming a file that does not exist — appears on **both**
tools, since it drops the settings file from the run entirely.

## What cannot be configured here

The trailing newline at end of file is an **editorconfig-only** knob: `insert_final_newline` is an
editorconfig standard property with no `.DotSettings` equivalent. So in a repo with no `.editorconfig`
there is no way to stop cleanup stripping that newline — no settings-file entry reaches it. Adding an
`.editorconfig` with `insert_final_newline = true` is the only lever.

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
