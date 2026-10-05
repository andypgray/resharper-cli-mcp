# resharper-cli-mcp

<!-- mcp-name: io.github.andypgray/resharper-cli-mcp -->

[![CI](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/ci.yml) [![works with jb](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fandypgray%2Fresharper-cli-mcp%2Fbadges%2Fjb-contract.json "Checked daily against the latest stable ReSharper command-line tools")](https://github.com/andypgray/resharper-cli-mcp/actions/workflows/jb-contract.yml) [![OpenSSF Scorecard](https://img.shields.io/ossf-scorecard/github.com/andypgray/resharper-cli-mcp?label=openssf+scorecard)](https://scorecard.dev/viewer/?uri=github.com/andypgray/resharper-cli-mcp) [![NuGet](https://img.shields.io/nuget/v/Zphil.ReSharperCli?logo=nuget&label=nuget)](https://www.nuget.org/packages/Zphil.ReSharperCli) [![NuGet downloads](https://img.shields.io/nuget/dt/Zphil.ReSharperCli?label=downloads)](https://www.nuget.org/packages/Zphil.ReSharperCli)

resharper-cli-mcp is an MCP server that runs JetBrains' ReSharper command-line tools for a coding agent. Its `resharper_inspect` tool runs `jb inspectcode` and returns the issues, and `resharper_cleanup` runs `jb cleanupcode` over the files the agent names. `jb` reads the solution's `.DotSettings` and `.editorconfig` as ReSharper does in the IDE, so the agent is held to the same severities and code style. The server is unofficial, not affiliated with or endorsed by JetBrains, and it runs a `jb` you install yourself.

## Why not call `jb` directly?

An agent can run `jb` from a shell. It then gets a report file that can list thousands of issues, a cleanup profile it must name on every call, and a cold cache in each new checkout. The server handles each of these:

- Issues come back as markdown grouped by file, re-rendered at lower detail until they fit the client's output budget.
- `jb cleanupcode` runs `Built-in: Full Cleanup` unless it is given `--profile`, whatever the solution's settings say. The server passes the profile the solution names under `SilentCleanupProfile`, and reports which files cleanup changed.
- The cache is warmed when a client connects, unless `RESHARPER_MCP_PREWARM=off`. A new worktree or clone is seeded from another checkout's warm cache.
- Runs are queued, because otherwise a second concurrent `jb` on one solution builds a cold cache of its own. 
- A run reports progress every ten seconds, with the elapsed time and the cap.

## Run times

Every run analyses the whole solution, because resolving symbols needs the full solution model. `files` narrows what is reported, not what is analysed. The first run against a solution builds ReSharper's caches and typically takes minutes, then later runs against the same cache are several times faster.

Each run is capped at `RESHARPER_MCP_TIMEOUT_SECS`, defaulting to 10 minutes. If your client's own tool-call timeout is shorter than a cold run, raise that too.

## Quickstart

The server needs the .NET 10 SDK and JetBrains' [ReSharper Command Line Tools](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html), which are free and need no ReSharper license. Both install as .NET global tools:

```bash
dotnet tool install -g JetBrains.ReSharper.GlobalTools
dotnet tool install -g Zphil.ReSharperCli
```

Register the command `resharper-cli-mcp`, which takes no arguments, with your MCP client. For Claude Code, add it to `.mcp.json`:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp"
    }
  }
}
```

The server uses the one `.sln` or `.slnx` in its working directory. When there are none or several, set `JB_SOLUTION_PATH` in the config's `env` block.

Other clients differ only in where the entry goes and the key around it:

| Client | Where the entry goes | Top-level key |
|---|---|---|
| Visual Studio 2022 17.14+ and 2026 | `.mcp.json` beside the solution | `servers`, with `"type": "stdio"` |
| VS Code | `.vscode/mcp.json` | `servers`, with `"type": "stdio"` |
| Cursor | `.cursor/mcp.json`, or `~/.cursor/mcp.json` for every project | `mcpServers` |
| Rider AI Assistant | Settings \| Tools \| AI Assistant \| MCP | `mcpServers` |
| Junie | `.junie/mcp/mcp.json`, or `~/.junie/mcp/mcp.json` for every project | `mcpServers` |
| Cline | `cline_mcp_settings.json`, under Configure MCP Servers | `mcpServers` |

Inside Rider, the IDE's own analysis already gives its agents the inspection results, and `resharper_inspect` would run a second analysis of the solution alongside it. There the server's sole use is `resharper_cleanup`, because Rider's MCP tools can reformat a file but cannot run a cleanup profile.

For Codex CLI, run `codex mcp add resharper -- resharper-cli-mcp`. [llms-install.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/llms-install.md) has the full entry for each client as a checklist, so you can point an agent at it to do the install.

VS Code, Visual Studio, Cursor and LM Studio can add the server in one click once both tools are installed:

[![Install in VS Code](https://img.shields.io/badge/VS_Code-Install_Server-0098FF?style=flat-square&logo=visualstudiocode&logoColor=white)](https://insiders.vscode.dev/redirect/mcp/install?name=resharper&config=%7B%22type%22%3A%22stdio%22%2C%22command%22%3A%22resharper-cli-mcp%22%7D) [![Install in Visual Studio](https://img.shields.io/badge/Visual_Studio-Install_Server-5C2D91?style=flat-square&logo=visualstudio&logoColor=white)](https://vs-open.link/mcp-install?%7B%22name%22%3A%22resharper%22%2C%22type%22%3A%22stdio%22%2C%22command%22%3A%22resharper-cli-mcp%22%7D) [![Add to Cursor](https://img.shields.io/badge/Cursor-Install_Server-000000?style=flat-square&logo=cursor&logoColor=white)](https://cursor.com/en/install-mcp?name=resharper&config=eyJjb21tYW5kIjoicmVzaGFycGVyLWNsaS1tY3AifQ==) [![Add to LM Studio](https://img.shields.io/badge/LM_Studio-Install_Server-4338CA?style=flat-square&logo=lmstudio&logoColor=white)](https://lmstudio.ai/install-mcp?name=resharper&config=eyJjb21tYW5kIjoicmVzaGFycGVyLWNsaS1tY3AifQ%3D%3D)

### Install as a Claude Code plugin

This repository is also a Claude Code plugin marketplace with one plugin:

```
/plugin marketplace add andypgray/resharper-cli-mcp
/plugin install resharper-cli-mcp@resharper-cli-mcp
```

The plugin starts the server with `dotnet dnx`, pinned to one release, and `claude plugin update resharper-cli-mcp@resharper-cli-mcp` moves the pin to the latest. You still install the .NET 10 SDK and `jb`, and ReSharper's caches live in the plugin's data directory.

### Install as a Claude Desktop extension

Download `resharper-cli-mcp-<version>.mcpb` from the [latest release](https://github.com/andypgray/resharper-cli-mcp/releases/latest), double-click it, and set **Solution file** to the `.sln` or `.slnx` to analyse. Claude Desktop starts the server outside your repository, so it has no working directory to find a solution in. The bundle carries only the server, so install the .NET 10 runtime and the ReSharper Command Line Tools first. **Run cap** moves the 10-minute limit on one run.

## Tools

| Tool | Mutates files | What it does |
|---|---|---|
| `resharper_inspect` | no | Runs `jb inspectcode` and returns the issues, grouped by file. |
| `resharper_cleanup` | yes | Runs `jb cleanupcode` on the given files and reports which ones changed on disk. |
| `resharper_reset_cache` | no (deletes caches) | Drops the solution's ReSharper cache so the next run starts cold, or reclaims the cache a deleted checkout left behind. |

`resharper_inspect` takes `files` globs, solution-relative or absolute, and a `severity` of `Suggestion`, `Warning` (the default) or `Error`. At `severity=Suggestion` a response reads:

```text
Found 2 issue(s) across 1 file(s):

### /repo/src/HomeController.cs
- **Line 8** [WARNING] `RedundantUsingDirective`: Using directive is not required by the code and can be safely removed.
- **Line 24** [SUGGESTION] `FieldCanBeMadeReadOnly.Local`: Field can be made readonly.
```

`report=Markdown` writes every issue with its own message to a file under the system temp directory. The response names the file, and the server deletes it after seven days. `detail` caps how detailed the response may be, down to a one-line `Minimal`, so `detail=Minimal report=Markdown` surveys a legacy solution in one call.

`resharper_cleanup` changes style, never behaviour: formatting, using directives, `var` style, modifier order, redundant qualifiers and parentheses, braces. Without a `profile` argument it uses the profile named under `SilentCleanupProfile` in the solution's settings, then `Built-in: Full Cleanup`. Where Full Cleanup would churn legacy code, name a narrower profile there. Every call then uses it, including calls from an agent that does not know it exists.

For a codebase with no style settings yet, start from ReSharper's [Detect Code Style Settings](https://blog.jetbrains.com/dotnet/2018/12/05/detection-code-styles-naming-resharper/), or in Rider [Auto-Detect Code Style Rules](https://www.jetbrains.com/help/rider/Code_Syntax_Style.html). The `derive_style_guide` prompt walks an agent through the same job without an IDE.

## Configuration

Set these in the client config's `env` block. All are optional. The `JB_` variables are passed on to `jb`, and the `RESHARPER_MCP_` ones govern the server itself.

| Variable | Default | Purpose |
|---|---|---|
| `JB_SOLUTION_PATH` | discovered | Solution to use when the working directory has none or several; the `solutionPath` tool argument overrides it for one call. |
| `JB_SETTINGS_PATH` | none | A `.DotSettings` file outside the solution's own, mounted as a Custom layer above the solution's and every project's settings. |
| `JB_CACHE_HOME` | `~/.jb-cache` | ReSharper cache directory. |
| `JB_EXTENSIONS` | none | Semicolon-separated ReSharper plugin IDs to load. |
| `JB_EXTENSION_SOURCE` | `jb`'s own | Custom NuGet source for those plugins. |
| `RESHARPER_MCP_TIMEOUT_SECS` | `600` | Cap in seconds on one `jb` run, and on the wait for one already in flight, clamped to 60–86,400. |
| `RESHARPER_MCP_PREWARM` | on | `off` disables the cache pre-warm when a client connects. |
| `RESHARPER_MCP_LOG_LEVEL` | `Warning` | Level for the rolling file log: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. The Microsoft names `Trace` and `Critical` also work, and any value it does not recognise falls back to `Warning`. |
| `MAX_MCP_OUTPUT_TOKENS` | 25,000 characters | Client output budget that responses are reduced to fit, at 2.5 characters per token. |

The solution comes from the `solutionPath` argument, then `JB_SOLUTION_PATH`, then the one `.sln` or `.slnx` in the working directory, with no walk up to a parent. Logs roll daily under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, and the platform equivalent elsewhere.

Two guides go deeper, and the server serves both as MCP resources for an agent to read. The [setup guide](https://github.com/andypgray/resharper-cli-mcp/blob/main/src/Zphil.ReSharperCli/Resources/setup-guide.md) (`resharper://guides/setup`) covers discovery, the cache, run times and output limits. The [configuration guide](https://github.com/andypgray/resharper-cli-mcp/blob/main/src/Zphil.ReSharperCli/Resources/configuration-guide.md) (`resharper://guides/configuration`) covers settings layers, cleanup profiles, and keeping a deliberate style from being reverted.

## Cleanup reminder hook

The single end-of-task cleanup is easy for an agent to forget. This Claude Code [PostToolUse hook](https://code.claude.com/docs/en/hooks) adds a one-line reminder to the agent's context after each `.cs` or `.razor` edit. It never edits code or calls the tool, so the agent still decides when to clean up. Add it to `.claude/settings.json`; the command needs a POSIX shell, which on Windows is Git Bash:

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

## Privacy Policy

resharper-cli-mcp collects nothing. It has no telemetry, analytics, accounts or remote logging, and it makes no network calls of its own. Each tool call runs the `jb` on your machine, and the results go back over stdio to the MCP client that started the server.

Everything the server writes stays on that machine: its diagnostic log, the inspection reports a call asks for, and bookkeeping files beside ReSharper's caches. The log and the reports can contain absolute paths and the rule IDs and messages read from your solution. [PRIVACY.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/PRIVACY.md) is the full policy, with where each file lives and how long it is kept.

## Contributing

Contributions are welcome. Bug reports reproduced on a public solution, MCP client-compatibility fixes, and improvements to discovery or output formatting land best. [CONTRIBUTING.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/CONTRIBUTING.md) covers the development setup and the test architecture, and [SECURITY.md](https://github.com/andypgray/resharper-cli-mcp/blob/main/SECURITY.md) covers reporting a security issue privately.

## License

MIT; see [LICENSE](https://github.com/andypgray/resharper-cli-mcp/blob/main/LICENSE).

JetBrains and ReSharper are trademarks of [JetBrains s.r.o.](https://www.jetbrains.com) This project is an independent wrapper of their [ReSharper Command Line Tools](https://www.jetbrains.com/resharper/features/command-line.html), which ship under JetBrains' own license.
