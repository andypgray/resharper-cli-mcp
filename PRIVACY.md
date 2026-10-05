# Privacy

This policy covers resharper-cli-mcp in every form it is distributed: the MCP server published as `Zphil.ReSharperCli`, the Claude Code plugin, and the Claude Desktop bundle (`.mcpb`). Effective 2026-08-29; changes are tracked in this file's git history.

## What this project collects

Nothing. The server has no telemetry, no analytics, no accounts, and no remote logging. The author receives no data from your use of it.

## How your source code is processed

Everything runs on your machine. Each tool call shells out to `jb`, the JetBrains ReSharper Command Line Tools you installed, which analyzes your solution locally. The results (inspection issues and cleanup summaries derived from your code) are returned over stdio to the MCP client that launched the server, and written to a local file only when a call asks for an inspection report. This project operates no servers and never sees your code or the results.

## What your MCP client does with the results

Tool output becomes part of your agent conversation. How the MCP client (for example, Claude Code) stores or transmits that conversation is governed by that client's own privacy policy, not this one.

## Local logs

Diagnostic logs roll daily under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, and the platform-equivalent path elsewhere. They can contain absolute paths and the rule IDs and messages read from your solution. `RESHARPER_MCP_LOG_LEVEL` controls how much is written.

## Retention

Everything this project writes stays on your machine, in three places:

- The logs described above. The sink keeps 7 daily files and deletes the oldest as it rolls; deleting the directory yourself at any point is safe.
- Inspection reports, written only when a `resharper_inspect` call sets `report`, to a `resharper-cli-mcp-reports` directory under the system temp directory. A report lists the issues found in your solution with their paths and messages, and the server deletes reports older than seven days.
- Bookkeeping files in ReSharper's cache directory (`~/.jb-cache` unless `JB_CACHE_HOME` moves it), named `.resharper-cli-mcp-*`. They record when a run against a solution last succeeded, the solution's path, which `jb` build ran it, how long runs took, and whether its cache was reset. When the server seeds a new checkout's cache, it also copies ReSharper's own cache files within that directory. These are kept until you delete them; `resharper_reset_cache` removes a solution's cache along with the records of its runs.

Nothing is retained anywhere else, because nothing is sent anywhere else.

## Network access

The server itself makes no network calls. Two things around it do:

- **Package restore.** Installing the server, or launching it through the Claude Code plugin, which runs `dotnet dnx`, downloads the `Zphil.ReSharperCli` package from nuget.org, a Microsoft service with [its own privacy statement](https://go.microsoft.com/fwlink/?LinkId=521839). The Claude Desktop bundle carries the server inside it and restores nothing. If `JB_EXTENSIONS` is set, `jb` likewise restores those ReSharper extensions from NuGet.
- **JetBrains tools.** `jb` is JetBrains software you install separately, governed by [JetBrains' terms and privacy policy](https://www.jetbrains.com/legal/).

## Contact

Questions about this policy: open an issue on [andypgray/resharper-cli-mcp](https://github.com/andypgray/resharper-cli-mcp/issues), or email andypgray@protonmail.com.
