# Configuring ReSharper for this server

> Unofficial wrapper: not affiliated with or endorsed by JetBrains.

This guide covers what ReSharper enforces, meaning which issues are reported and which styles cleanup
rewrites. Running the server is covered by the `resharper://guides/setup` resource, which is the one to read
when a call cannot find `jb` or the solution, times out, or comes back shortened.

## The two axes

The two tools read two independent configuration axes, and most surprises come from tuning one axis and
expecting the other to change.

| Tool | Reads | Controls |
|---|---|---|
| `resharper_inspect` | inspection severities | which issues are reported, and at what level |
| `resharper_cleanup` | code style, applied through its cleanup profile | how files are rewritten in place |

They do not share a switch:

- Setting an inspection to `DO_NOT_SHOW` (or the editorconfig severity `none`) removes that issue from
  `resharper_inspect` output, but it does not stop `resharper_cleanup` from rewriting that style. Cleanup
  overrides your severity for the length of the run, raising every rule its enabled modules need back to
  `SUGGESTION`, so `DO_NOT_SHOW` cannot reach it.
- In the other direction, defining or narrowing a cleanup profile changes what cleanup rewrites and leaves
  what `resharper_inspect` reports unchanged.

So "I set the rule to `DO_NOT_SHOW` but cleanup still changes my code" is expected behavior. You moved the
inspect axis, and cleanup runs on the style axis.

## Protecting a deliberate style from cleanup

A formatting choice that no settings layer records is unprotected. The next cleanup reverts it without a
build failure or an inspection to say so, and the file comes back written the other way. To keep the choice,
change the code's shape so there is no formatting left to revert, record the choice where `jb` reads it, or
fence the region in source. Re-applying the formatting by hand after each run does not hold, because every
later run reverts it again.

Two cases follow. In the first, a setting exists but cannot express "leave alone". In the second, no setting
that helps was found.

### Named arguments

You deliberately write named arguments, and `Built-in: Full Cleanup` keeps stripping them. It removes named
arguments that are redundant by position, and arguments equal to their default value. No inspection severity
stops that, and the style setting cannot either.

Argument style is binary, `positional` or `named`, with no neutral "leave alone" value. `named` makes cleanup
add names and `positional` makes it strip them. Neither means "don't touch", so no settings change alone can
make cleanup leave argument style as written.

### Line joining

A hand-written line break inside a call is recorded nowhere, and cleanup may join the call back onto one line
that runs past your margin. This was measured on 2026-09-07 against JetBrains CleanupCode 2026.2.1, with
`Built-in: Full Cleanup`. A call whose single argument is a lambda, wrapped by hand across two lines, came
back as one 123-character line.

Two findings from that measurement contradict what you might expect:

- **Turning wrapping off does not keep your breaks.** `WRAP_LINES` set to `False` was in force for some of
  the runs that joined. That setting decides whether the formatter adds breaks at the margin, and it does not
  keep the breaks you wrote.
- **No setting was found that keeps them.** The join appeared in 4 consecutive runs and then in none of 21
  further runs, whether under a declared `max_line_length`, under three keep-existing properties, or on a
  cold or a warm cache. Because it is intermittent there is no setting to recommend, and a settings change
  that seems to fix it has most likely failed to reproduce it.

Two of the options below held in every run, fencing the region and changing the shape so the break is not
needed.

To leave a style as written, use one of these four options. An inspection severity is not one of them.

1. Narrow the profile. Define a custom cleanup profile in the solution's `.sln.DotSettings` that leaves the
   offending task off (for named arguments, the task is `ArrangeArgumentsStyle`). Then declare it as the
   solution's default (see below), or pass its name as `resharper_cleanup`'s `profile` argument. This fix
   lasts and covers the whole repo.

   A profile lists every task it runs, and any task its XML omits reads back as that task's own default,
   which is off. So "Full Cleanup minus one task" means listing every task Full Cleanup turns on and setting
   the one you want skipped to `False`. Copy the shape from an IDE-generated profile instead of writing a
   short one and expecting the rest to be inherited. Then check it by running both profiles over a
   deliberately messy scratch file and diffing the results, which should differ only where you intended.
2. Exclude the file. `resharper_cleanup` rewrites only the files you list in `files`, so leave out a file
   whose style you want frozen. Cleanup is opt-in per path, and it has no "everything except this" mode.
3. Disable in source. For a rewrite that ReSharper also reports as an inspection, wrap the code in disable
   comments keyed to that rule ID so cleanup skips it. Use `// ReSharper disable once RedundantArgumentDefaultValue`
   for one call, or a `// ReSharper disable RedundantArgumentDefaultValue` … `// ReSharper restore RedundantArgumentDefaultValue`
   region. Read the exact rule ID off `resharper_inspect` output. The comment stays with the file and needs no
   settings file.

   Pure formatting takes a different comment. A rewrite the formatter makes has no rule ID to disable, so
   fence the region with `// @formatter:off` … `// @formatter:on`, the formatter's counterpart to
   `// ReSharper disable`. To change one property for the rest of the file instead of suspending the
   formatter entirely, use `// @formatter:<rule_ID> <value>`. The fence held in every run of the
   line-joining measurement above, including the runs that joined the unfenced copy of the same code.
4. Change the shape. This lasts longest of the four, because it leaves no formatting to revert. Where a
   hand-wrapped argument keeps being joined, extracting it to a local gives the call a short form the
   formatter has no reason to touch, and the declaration is on its own line because it is a separate
   statement. Cleanup left it unchanged in every run of the measurement above.

Choose (1) for a house rule, (4) where the code can take it, (3) for a one-off you must keep, and (2) for a
file you never want normalized.

## Where settings come from

`jb` mounts ReSharper's settings layers itself, as the IDE does. From lowest to highest precedence, they are
the machine-wide global layers (`GlobalSettingsStorage.DotSettings` in the JetBrains shared directory), the
solution's `{solution}.DotSettings`, and each project's own `{project}.csproj.DotSettings`. The personal
`.user` variants of the solution and project files each outrank their shared counterpart. The higher layer
wins a conflict, so a `{project}.csproj.DotSettings` is how a repo narrows a rule for one project, since it
outranks the solution layer, which outranks the global one.

This server resolves a settings file in this order:

1. `JB_SETTINGS_PATH`, when it is set and the file exists,
2. `{solution}.DotSettings` next to the solution (for example `App.sln.DotSettings`),
3. `GlobalSettingsStorage.DotSettings` in the JetBrains shared directory,
4. none.

It passes `--settings` to `jb` only when the result is a file `jb` does not find itself, which means a
`JB_SETTINGS_PATH` outside the two discovered locations. `--settings` mounts its file as a Custom layer above
the whole stack. Passing a discovered file along would therefore demote every project layer, and a
`{project}.csproj.DotSettings` the solution relies on would stop applying without any warning. Levels 2 and 3
still matter to this server, because the declared cleanup profile below is read from the resolved file, but
`jb` applies them on its own.

`jb` also applies `.editorconfig` from the source tree, with no flag and no `--settings`. StyleCop.Analyzers,
Roslyn, `dotnet format` and Rider read `.editorconfig` too, so it is the portable home for style rules. Use
`.sln.DotSettings` only for what it cannot express, which is ReSharper-only settings and cleanup-profile
definitions.

## The two DotSettings shapes you will edit

`.DotSettings` is XML that the IDE generates, with no published schema. Keep edits minimal, and copy an
existing entry instead of writing a new one by hand.

- An inspection severity (the inspect axis) is a single entry:

  ```xml
  <s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=RuleId/@EntryIndexedValue">DO_NOT_SHOW</s:String>
  ```

  `RuleId` is the identifier `resharper_inspect` prints for the issue (for example
  `RedundantUsingDirective`). Values are `ERROR`, `WARNING`, `SUGGESTION`, `HINT`, `DO_NOT_SHOW`. The
  editorconfig equivalents are `resharper_<rule>_highlighting = error|warning|suggestion|hint|none` and,
  for Roslyn analyzers, `dotnet_diagnostic.<id>.severity`.

- A cleanup profile (the style axis) is stored in the same file under
  `/Default/CodeStyle/CodeCleanup/Profiles/=<ProfileName>/@EntryIndexedValue`, as one XML-encoded value, a
  single `<Profile name="…">` element that lists the cleanup tasks. The `profile` argument takes the `name`
  attribute inside that value, not the settings key. `Built-in: Full Cleanup` is the fallback, and it touches
  everything.

## Which profile a call runs without a `profile` argument

`resharper_cleanup` resolves its profile in this order:

1. the `profile` argument on the call,
2. `/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue` in the resolved settings file,
   ReSharper's own "profile to use when nobody picks one",
3. `Built-in: Full Cleanup`.

Declaring the profile in `.sln.DotSettings` makes a repo-wide narrowing last, because it is committed with
the repo and applies to callers who do not know the profile exists. The settings file is read again on every call, so
a profile you declare now governs the next `resharper_cleanup`, with no server restart. A direct
`jb cleanupcode` run does not read that key. It always defaults to Full Cleanup and needs an explicit
`--profile`.

If step 2 cannot be read, the cleanup result reports it. ReSharper's settings reader is more forgiving than
the XML specification, most visibly about comments. A `--` inside `<!-- … -->` is illegal XML, and ReSharper
reads it without complaint. This server tolerates the same, so it does not lose a profile that `jb` itself would
have applied. When a settings file is broken beyond that, the `resharper_cleanup` result opens with a
`WARNING:` naming the file and the fault. By then the fallback to `Built-in: Full Cleanup` has already
rewritten the code the declared profile was protecting. `resharper_inspect` gives no warning, because `jb`
reads that file itself and parses it without trouble, so inspection severities are unaffected. The other
warning, for a `JB_SETTINGS_PATH` that names a missing file, appears on both tools, because it drops the
settings file from the run entirely.

## What cannot be configured here

The trailing newline at the end of a file is set only in editorconfig, through the standard
`insert_final_newline` property, which has no `.DotSettings` equivalent. In a repo with no `.editorconfig`, no settings-file
entry can stop cleanup stripping that newline. Add an `.editorconfig` with `insert_final_newline = true` to
keep it.

## Authoring a full style guide

To derive an intentional configuration for a whole codebase, use this server's `derive_style_guide` prompt.
It walks through sampling the de facto conventions, reconciling them with StyleCop, and validating the result
with the inspect loop.

## Authoritative references (JetBrains)

Consult these for exact keys and behavior rather than relying on memory.

- EditorConfig properties, the per-property reference for inspection severities, formatter settings and the
  argument-style keys: <https://www.jetbrains.com/help/resharper/EditorConfig_Properties.html>
- Use EditorConfig, on how ReSharper reads `.editorconfig`, including from the source tree:
  <https://www.jetbrains.com/help/resharper/Using_EditorConfig.html>
- Manage and share settings, on `.DotSettings` layers and their precedence:
  <https://www.jetbrains.com/help/resharper/Sharing_Configuration_Options.html>
- Configure code inspection settings, on the inspect axis and its severities:
  <https://www.jetbrains.com/help/resharper/Code_Analysis__Configuring_Warnings.html>
- Ignore parts of the code, on the in-source `// ReSharper disable` and `// ReSharper restore` comments:
  <https://www.jetbrains.com/help/resharper/Ignore_Parts_of_Code.html>
- Code cleanup profiles, on defining or narrowing a cleanup profile on the style axis:
  <https://www.jetbrains.com/help/resharper/Reference__Options__Tools__Code_Cleanup.html>
- Code Syntax Style: Named/Positional Arguments, the binary `positional`/`named` setting described above:
  <https://www.jetbrains.com/help/resharper/Argument_Style.html>
- C# Line Breaks and Wrapping, the editorconfig properties behind the line-joining case above:
  <https://www.jetbrains.com/help/resharper/EditorConfig_CSHARP_LineBreaksPageSchema.html>
- Use comments to configure formatter, on the `// @formatter:off` fence and its per-property form:
  <https://www.jetbrains.com/help/resharper/Configure_Code_Formatting_Rules.html>
- The ReSharper command-line tools' InspectCode: <https://www.jetbrains.com/help/resharper/InspectCode.html>
  and CleanupCode: <https://www.jetbrains.com/help/resharper/CleanupCode.html>
