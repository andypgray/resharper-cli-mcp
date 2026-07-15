# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.2] - 2026-07-15

### Added

- `resharper://guides/configuration` MCP resource — an on-demand guide to how ReSharper configuration
  works: inspection severities drive `resharper_inspect` while the cleanup profile drives
  `resharper_cleanup` (two independent axes — hiding an inspection does not stop cleanup), how to protect
  a deliberate style from cleanup, settings and `.editorconfig` discovery, and the `.DotSettings` key
  shapes. The always-loaded server instructions carry only a short signpost pointing at it.

### Changed

- `resharper_cleanup` now reports which files it actually changed on disk, hashing each concrete file
  before and after the run and classifying it as changed, unchanged, status-unknown, or a wildcard
  pattern — instead of a bare "completed" line that hid whether cleanup rewrote a file. Solution-wide
  runs degrade the per-file detail progressively to stay within the output budget. Purely observational:
  the cleanup itself is unchanged.

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

[Unreleased]: https://github.com/andypgray/resharper-cli-mcp/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.2
[1.0.1]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.1
[1.0.0]: https://github.com/andypgray/resharper-cli-mcp/releases/tag/v1.0.0
