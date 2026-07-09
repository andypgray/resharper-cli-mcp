# Contributing to resharper-cli-mcp

resharper-cli-mcp is an MCP stdio server that wraps JetBrains' ReSharper command-line tools (`jb inspectcode` and `jb cleanupcode`) and exposes them to coding agents as two tools. Thanks for your interest in working on it. For what it does and how to install it, see the [README](README.md); this document is about working on the code.

## What contributions land well here

- Bug reports reproduced on a public or open-source solution. A repro on something we can all clone is the most actionable thing you can file.
- MCP client compatibility fixes. The server targets any stdio MCP client; a fix for how a specific client launches, configures, or talks to it is welcome.
- Discovery and formatting improvements. Solution and settings resolution, SARIF parsing, and the issue markdown are the parts most likely to need adjusting for a new environment.
- Changes that keep the server a thin, faithful wrapper over `jb`. It does not reinterpret ReSharper's output or add analysis of its own, and staying close to the reference tools is a design goal.

## Development setup

- The .NET 10 SDK is required. The solution file is `Zphil.ReSharperCli.slnx` (`.slnx`, not `.sln`), which needs a current SDK to load.
- Windows, macOS, and Linux are supported. The server resolves the JetBrains shared settings directory per platform.
- The unit tests do not need `jb` installed. Running the server against a real solution does: `dotnet tool install -g JetBrains.ReSharper.GlobalTools`.

Build the whole solution:

```bash
dotnet build Zphil.ReSharperCli.slnx
```

## Running tests

```bash
dotnet test Zphil.ReSharperCli.slnx

# A single test by fully-qualified name
dotnet test Zphil.ReSharperCli.slnx --filter "FullyQualifiedName~SarifParserTests"
```

The test project has exactly two fakeable seams, worth understanding before you add a test:

- `IProcessRunner` is the only process-spawning seam, faked with NSubstitute. A test that needs `jb` output has the substitute return a canned `ProcessResult`, or, for the inspect round trip, write a SARIF fixture to the `-o=` path it received. No test spawns a real `jb`.
- `IEnvironment` is the only environment seam, backed by a hand-rolled `FakeEnvironment` whose current and home directories point at per-test temp dirs. Every environment variable, current directory, and home directory read in product code goes through it.

No test mutates the real process environment. A `SetEnvironmentVariable` call in a test is a defect: it would break the parallel run, which depends on every environment read being routed through `IEnvironment`. `ProcessRunnerTests` is the one class that spawns real processes, and it uses `dotnet` and `ping`/`sleep`, never `jb`.

SARIF fixtures live under `tests/Zphil.ReSharperCli.Tests/Fixtures/Sarif/` and are copied to the output directory as content.

## Pull request expectations

- Small and focused: one change per PR.
- A green build and passing tests (see [Running tests](#running-tests)).
- XML doc comments on public members.
- Behavioral stability is load-bearing. The `jb` argument order, the error-message text, and the formatter output are pinned by tests; a change to any of them needs a stated reason in the PR.

## Versioning and releases

The project follows semantic versioning from 1.0.0. Breaking changes to the tool surface bump the major version. Release notes live on [GitHub Releases](https://github.com/andypgray/resharper-cli-mcp/releases); there is no `CHANGELOG` file. A maintainer tags `v*` and CI publishes the package to NuGet.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE), the same license as the project.
