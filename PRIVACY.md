# Privacy

This policy covers resharper-cli-mcp in every form it is distributed: the MCP server published as `Zphil.ReSharperCli`, the Claude Code plugin, and the Claude Desktop bundle (`.mcpb`). Effective 2026-08-29; changes are tracked in this file's git history.

## What this project collects

Nothing. The server has no telemetry, no analytics, no accounts, and no remote logging. The author receives no data from your use of it.

## How your source code is processed

Everything runs on your machine. Each tool call shells out to `jb`, the JetBrains ReSharper Command Line Tools you installed, which analyzes your solution locally. The results — inspection issues and cleanup summaries derived from your code — are returned over stdio to the MCP client that launched the server, and nowhere else. This project operates no servers and never sees your code or the results.

## What your MCP client does with the results

Tool output becomes part of your agent conversation. How the MCP client (for example, Claude Code) stores or transmits that conversation is governed by that client's own privacy policy, not this one.

## Local logs

Diagnostic logs roll daily under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` on Windows, and the platform-equivalent path elsewhere. They can contain absolute paths and the rule IDs and messages read from your solution. `RESHARPER_MCP_LOG_LEVEL` controls how much is written.

## Retention

Those logs are the only data this project writes, and they are written on your machine. The sink keeps 7 daily files and deletes the oldest as it rolls; deleting the directory yourself at any point is safe. Nothing is retained anywhere else, because nothing is sent anywhere else.

## Network access

The server itself makes no network calls. Two things around it do:

- **Package restore.** Installing the server, or launching it through the Claude Code plugin, which runs `dotnet dnx`, downloads the `Zphil.ReSharperCli` package from nuget.org, a Microsoft service with [its own privacy statement](https://go.microsoft.com/fwlink/?LinkId=521839). The Claude Desktop bundle carries the server inside it and restores nothing. If `JB_EXTENSIONS` is set, `jb` likewise restores those ReSharper extensions from NuGet.
- **JetBrains tools.** `jb` is JetBrains software you install separately, governed by [JetBrains' terms and privacy policy](https://www.jetbrains.com/legal/).

## Contact

Questions about this policy: open an issue on [andypgray/resharper-cli-mcp](https://github.com/andypgray/resharper-cli-mcp/issues), or email andypgray@protonmail.com.
