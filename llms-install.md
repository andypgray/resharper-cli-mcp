# Installing resharper-cli-mcp

resharper-cli-mcp is an MCP server that gives a C# coding agent ReSharper's solution-wide
inspections and its code cleanup. It wraps JetBrains' `jb` command-line tools, which you install
yourself; it bundles no JetBrains software, and it is unofficial, not affiliated with or endorsed by
JetBrains.

This page is the install path, start to finish. [README.md](README.md) describes what the server
does once it is running.

## Prerequisites

The .NET 10 SDK. The server and the ReSharper tools it drives are both .NET global tools, so the
SDK is the only prerequisite; neither needs an IDE, and neither needs a ReSharper license, because
the [ReSharper Command Line Tools](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html)
are free.

## Install both tools

```bash
dotnet tool install -g JetBrains.ReSharper.GlobalTools
dotnet tool install -g Zphil.ReSharperCli
```

## Check that both resolve

```bash
jb --version
resharper-cli-mcp --version
```

Each prints a version. If `jb` is not found, the tools installed but their directory is not on your
`PATH`; it is `~/.dotnet/tools` on Linux and macOS, and `%USERPROFILE%\.dotnet\tools` on Windows.

The server looks for `jb` on `PATH` first and then in `~/.dotnet/tools`. That second location is
what covers the common failure: an MCP client often starts the server without your shell's `PATH`,
so a `jb` that answers in your terminal can still be invisible to the server. A default global-tool
install is found either way. If you installed the tools somewhere else, put that directory on the
`PATH` the client itself runs with.

Running `resharper-cli-mcp` with no arguments at a terminal prints a one-line reminder that it is a
stdio server started by a client. It does not hang, so that is a safe thing to try.

## Configure your MCP client

Every client is given the same server: the command `resharper-cli-mcp`, with no arguments. Only the
file and the surrounding key differ.

### Claude Code

Add the server to `.mcp.json` in the project root:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp"
    }
  }
}
```

Claude Code can also install the server as a plugin, which brings the prompt and both guide
resources with it. See [README.md](README.md#install-as-a-claude-code-plugin).

### VS Code

Add it to `.vscode/mcp.json`. The top-level key is `servers`, not `mcpServers`, and the transport is
named:

```json
{
  "servers": {
    "resharper": {
      "type": "stdio",
      "command": "resharper-cli-mcp"
    }
  }
}
```

### Cursor

Add it to `.cursor/mcp.json` for one project, or `~/.cursor/mcp.json` for every project:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp"
    }
  }
}
```

### Cline

Open the MCP Servers panel, choose Configure MCP Servers to open `cline_mcp_settings.json`, and add:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp"
    }
  }
}
```

### Any other client

The server speaks MCP over stdio and takes no arguments, so any client that can launch a command
will run it. Give it the command `resharper-cli-mcp` in whatever shape your client's configuration
takes.

## Point it at a solution

The server finds a single `.sln` or `.slnx` in its working directory. When that directory holds none
or several, name one with `JB_SOLUTION_PATH` in the client config's `env` block:

```json
{
  "mcpServers": {
    "resharper": {
      "command": "resharper-cli-mcp",
      "env": {
        "JB_SOLUTION_PATH": "/path/to/YourSolution.slnx"
      }
    }
  }
}
```

Discovery is top level only, with no walk up to a parent directory. The `solutionPath` tool argument
overrides both for a single call. The other environment variables are listed under
[Configuration](README.md#configuration) in the README.

## If a call is slow or fails

The first run against a solution builds ReSharper's solution-wide index and takes minutes. Later
runs against the same cache are several times faster. A fresh clone or worktree starts cold again,
because the cache is keyed to the solution's absolute path.

Each run is capped at 10 minutes, and `RESHARPER_MCP_TIMEOUT_SECS` moves that cap. If a cold
analysis needs longer than the cap, raise it rather than narrowing the call: `jb` analyses the whole
solution whatever the report is scoped to, so a narrower `files` argument decides what is reported,
not how much is analysed.

The server publishes a `resharper://guides/setup` resource covering all of this at troubleshooting
depth. An agent that cannot find `jb` or the solution, hits the cap, or gets a shortened response
should read that resource first.
