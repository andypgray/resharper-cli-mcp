# resharper-cli-mcp

<!-- mcp-name: io.github.andypgray/resharper-cli-mcp -->

[![CI](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/ci.yml) [![works with jb](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fandypgray%2Fresharper-cli-mcp%2Fbadges%2Fjb-contract.json "Checked daily against the latest stable ReSharper command-line tools")](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/jb-contract.yml) [![OpenSSF Scorecard](https://img.shields.io/ossf-scorecard/github.com/andypgray/resharper-cli-mcp?label=openssf+scorecard)](https://scorecard.dev/viewer/?uri=github.com/andypgray/resharper-cli-mcp) [![NuGet](https://img.shields.io/nuget/v/Zphil.ReSharperCli?logo=nuget&label=nuget)](https://www.nuget.org/packages/Zphil.ReSharperCli) [![NuGet downloads](https://img.shields.io/nuget/dt/Zphil.ReSharperCli?label=downloads)](https://www.nuget.org/packages/Zphil.ReSharperCli)

resharper-cli-mcp is an MCP server that gives a C# coding agent ReSharper's solution-wide inspections (`resharper_inspect`) and its code cleanup (`resharper_cleanup`). It wraps JetBrains' `jb`, managing its cache and returning LLM-friendly markdown sized to a context window. It is unofficial — not affiliated with or endorsed by JetBrains. The server shells out to a `jb` you install yourself and bundles no JetBrains software.

## What the server adds

`jb` is built for a batch job: one run against one checkout, a report written to a file. An agent hits the same solution several times an hour, and what each call costs comes down to whether ReSharper's solution-wide index is already built. So the server owns the cache directory and runs a lifecycle over it:

- The first run happens before you ask for it. A speculative inspection starts as soon as a client connects, skipped when a run against that cache succeeded in the last hour; a tool call arriving mid-pass cancels it and takes the cache within a second or two. `RESHARPER_MCP_PREWARM=off` turns it off.

- Runs are serialized per solution. A cross-process lock keeps every client on one cache generation; a second concurrent `jb` forks a cold copy of its own and leaves it behind on disk. A `jb` you start yourself is outside that queue, so give it its own `--caches-home`.

- A fresh checkout is seeded from a warm one. Caches are keyed to the solution's absolute path, so a new worktree or clone starts cold. When a call finds no cache and a same-named sibling checkout has a warm one, the server copies it across, best-effort and never over a cache a successful run produced. The copy still has to be re-keyed, so a seeded run lands between warm and cold.

- SARIF becomes markdown that fits the client. Issues come back grouped by file, re-rendered at progressively lower detail until they fit the client's output budget, with every issue still counted and every file still named at each step.

## Quickstart

The server needs the .NET 10 SDK and JetBrains' [ReSharper Command Line Tools](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html). Both install as .NET global tools, and neither needs an IDE.

```bash
dotnet tool install -g JetBrains.ReSharper.GlobalTools
dotnet tool install -g Zphil.ReSharperCli
```

The server looks for `jb` on `PATH` and then in `~/.dotnet/tools`. An MCP client often starts the server without your shell's `PATH`, so a `jb` that answers in your terminal can still be invisible to it.

Register the server with your MCP client under the command `resharper-cli-mcp`. For Claude Code, add it to `.mcp.json`:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp"
    }
  }
}
```

The server finds a single `.sln`/`.slnx` in its working directory; when that directory holds zero or several, set `JB_SOLUTION_PATH` in the config's `env` block.

VS Code and Cursor users can add the server in one click, once both tools are installed:

[![Install in VS Code](https://img.shields.io/badge/VS_Code-Install_Server-0098FF?style=flat-square&logo=visualstudiocode&logoColor=white)](https://insiders.vscode.dev/redirect/mcp/install?name=resharper&config=%7B%22type%22%3A%22stdio%22%2C%22command%22%3A%22resharper-cli-mcp%22%7D) [![Add to Cursor](https://cursor.com/deeplink/mcp-install-dark.svg)](https://cursor.com/en/install-mcp?name=resharper&config=eyJjb21tYW5kIjoicmVzaGFycGVyLWNsaS1tY3AifQ==)

## Install as a Claude Code plugin

This repository doubles as a single-plugin marketplace, so the tools, the `derive_style_guide` prompt, and both guide resources arrive in one step:

```
/plugin marketplace add andypgray/resharper-cli-mcp
/plugin install resharper-cli-mcp@resharper-cli-mcp
```

The plugin starts the server with `dotnet dnx`, which fetches `Zphil.ReSharperCli` from NuGet on first use. You still need the .NET 10 SDK and the ReSharper Command Line Tools; ReSharper's caches live in the plugin's own data directory, outside your source tree.

## Tools

| Tool | Mutates files | What it does |
|---|---|---|
| `resharper_inspect` | no | Runs ReSharper InspectCode and returns the issues, grouped by file. |
| `resharper_cleanup` | yes | Runs ReSharper CleanupCode to reformat and normalize the given files in place. |
| `resharper_reset_cache` | no (deletes caches) | Drops the solution's ReSharper cache so the next run rebuilds its analysis from cold. |

Scope `resharper_inspect` with the `files` glob (entries may be solution-relative or absolute) and raise `severity` (`Suggestion`, `Warning`, `Error`; default `Warning`) to control how much comes back. Each issue carries a file, line, severity, rule ID, and message:

```text
Found 2 issue(s) across 1 file(s):

### /repo/src/HomeController.cs
- **Line 8** [WARNING] `RedundantUsingDirective`: Using directive is not required by the code and can be safely removed.
- **Line 24** [SUGGESTION] `FieldCanBeMadeReadOnly.Local`: Field can be made readonly.
```

`resharper_cleanup` changes style, never behavior: formatting, using directives, `var` style, modifier order, redundant qualifiers and parentheses, braces. Write correct logic and let cleanup do the polish: call it once, at the end of a task, with every changed file batched into the one call. It reports which files it actually changed on disk.

For a legacy codebase where the fallback `Built-in: Full Cleanup` profile would churn code you did not touch, define a narrower profile (for example `Custom: No Reordering`) in the solution's `.sln.DotSettings` and name it under `SilentCleanupProfile`. Every call then uses it, including calls from an agent that does not know it exists.

## Run times

Each run is capped at 10 minutes; `RESHARPER_MCP_TIMEOUT_SECS` moves the cap. Narrowing a call with `files` will not make it finish sooner: resolving symbols across projects takes the whole solution model, so `files` decides what is reported, not how much is analysed. When an MCP client's own tool-call timeout is shorter than a cold run needs, the client gives up first; in Claude Code, raise it with a per-server `"timeout"` in `.mcp.json` or `MCP_TOOL_TIMEOUT`. The `resharper://guides/setup` resource carries all of this at troubleshooting depth, for an agent to pull when a call cannot find `jb`, times out, or comes back shortened.

## Configuration

Set these in the MCP client config's `env` block. All are optional. Each `JB_` variable becomes something `jb` itself is told; the `RESHARPER_MCP_` ones govern this server's own behaviour and never reach `jb`.

| Variable | Purpose |
|---|---|
| `JB_SOLUTION_PATH` | Solution to use when the working directory has zero or several; the `solutionPath` tool argument overrides it for one call. |
| `JB_SETTINGS_PATH` | Explicit `.DotSettings` file for `jb`, mounted as a Custom layer above the solution's and every project's own settings. |
| `JB_CACHE_HOME` | ReSharper cache directory (default `~/.jb-cache`). |
| `JB_EXTENSIONS` | Semicolon-separated ReSharper plugin IDs to load. |
| `JB_EXTENSION_SOURCE` | Custom NuGet source for those plugins. |
| `RESHARPER_MCP_TIMEOUT_SECS` | Cap in seconds on one `jb` run, and on the wait for one already in flight (default `600`, clamped to 60–86,400). |
| `RESHARPER_MCP_PREWARM` | `off` disables the background cache pre-warm above. |
| `RESHARPER_MCP_LOG_LEVEL` | Level for the rolling file log (default `Warning`). |
| `MAX_MCP_OUTPUT_TOKENS` | Client output budget the reduction ladder renders to fit (2.5 characters per token; 25,000 characters when unset). |

**Solution discovery** tries, in order: the `solutionPath` argument, `JB_SOLUTION_PATH`, then a single `.sln`/`.slnx` in the working directory (top level only, no parent walk).

**Settings discovery** tries, in order: `JB_SETTINGS_PATH`, a `.DotSettings` file beside the solution, then `GlobalSettingsStorage.DotSettings` in the JetBrains shared directory. `jb` mounts the last two on its own, so the server passes `--settings` only for a `JB_SETTINGS_PATH` outside them (naming an already-mounted file would demote every project's own `.DotSettings`). On top of whichever settings apply, `jb` reads `.editorconfig` from the source tree automatically.

Logs roll daily under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, and the platform-equivalent path elsewhere. Nothing leaves the machine; [PRIVACY.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/PRIVACY.md) states that as policy.

## What ReSharper enforces

`resharper_inspect` obeys **inspection severities** (what gets reported); `resharper_cleanup` enforces **code style** through its cleanup **profile** (what gets rewritten). The two axes do not share a switch: setting a rule to `DO_NOT_SHOW` hides its issue, and cleanup goes on normalizing that style. The full model ships as an on-demand MCP resource, `resharper://guides/configuration`, for an agent to pull just before changing what ReSharper enforces.

For an existing codebase the `derive_style_guide` MCP prompt walks an agent through deriving an intentional style guide from the code you already have, `.editorconfig`-first, with ReSharper-only knobs spilling into `.sln.DotSettings`. If you have access to Resharper or Rider, JetBrains' first-party [Detect Code Style Settings](https://blog.jetbrains.com/dotnet/2018/12/05/detection-code-styles-naming-resharper/) is the better baseline; the prompt is the path for headless use.

## Cleanup reminder hook

The single end-of-task cleanup is easy for an agent to forget. This Claude Code [PostToolUse hook](https://code.claude.com/docs/en/hooks) appends a one-line reminder to the agent's context after each `.cs`/`.razor` edit; it never edits code or calls the tool itself, so the agent decides when to clean up. Add it to `.claude/settings.json`:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "grep -qiE '\"file_path\"[[:space:]]*:[[:space:]]*\"[^\"]*\\.(cs|razor)\"' && printf '%s' '{\"hookSpecificOutput\":{\"hookEventName\":\"PostToolUse\",\"additionalContext\":\"When this task is done, batch every edited .cs/.razor file into one resharper_cleanup call.\"}}' || true"
          }
        ]
      }
    ]
  }
}
```

The command uses `grep` and `printf`, so it needs a POSIX shell (on Windows, Git Bash).

## Contributing

Contributions are welcome. Bug reports reproduced on a public solution, MCP client-compatibility fixes, and improvements to discovery or output formatting land best. See [CONTRIBUTING.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/CONTRIBUTING.md) for the development setup (.NET 10 SDK) and the two-seam test architecture. To report a security issue privately, see [SECURITY.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/SECURITY.md).

## License

MIT; see [LICENSE](https://github.com/andypgray/resharper-cli-mcp/blob/main/LICENSE).

JetBrains and ReSharper are trademarks of [JetBrains s.r.o.](https://www.jetbrains.com) This project is an independent wrapper of their [ReSharper Command Line Tools](https://www.jetbrains.com/resharper/features/command-line.html), which ship under JetBrains' own license.
