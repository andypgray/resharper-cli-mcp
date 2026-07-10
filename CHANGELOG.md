# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-07-10

### Fixed

- Package the MCP manifest at `.mcp/server.json`. It was landing at `.mcp//server.json` (a double
  slash) because the csproj `PackagePath` used a backslash and packing runs on Linux, so nuget.org
  could not generate the VS Code MCP server configuration. No change to server behavior.

## [1.0.0] - 2026-07-09

Initial public release — an MCP stdio server wrapping JetBrains' ReSharper Command Line Tools.
Unofficial; not affiliated with or endorsed by JetBrains.

### Added

- `resharper_inspect` tool — runs ReSharper InspectCode over the solution and returns the issues
  grouped by file (read-only). Scope with a `files` glob and filter by `severity`.
- `resharper_cleanup` tool — runs ReSharper CleanupCode to reformat and normalize the given files
  in place, using a named cleanup `profile`.
- `derive_style_guide` prompt — walks an agent through deriving an intentional, `.editorconfig`-first
  ReSharper/StyleCop style guide for a legacy codebase, validated with `resharper_inspect`.
- Solution, settings, and `jb` discovery from the working directory or the `JB_SOLUTION_PATH`,
  `JB_SETTINGS_PATH`, `JB_CACHE_HOME`, `JB_EXTENSIONS`, and `JB_EXTENSION_SOURCE` environment
  variables.
- Output truncation honoring the client's `MAX_MCP_OUTPUT_TOKENS` budget.
- Ships as a .NET global tool and MCP server (`PackAsTool` + `PackageType=McpServer`), published to
  NuGet with SLSA build provenance and registered on the MCP registry.

[1.0.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.0
