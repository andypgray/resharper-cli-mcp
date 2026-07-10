# Security

This document covers reporting a vulnerability in resharper-cli-mcp, the server's security model, and verifying the package you install.

## Supported versions

Only the latest release on nuget.org receives security fixes.

## Reporting a vulnerability

Report privately through [GitHub security advisories](https://github.com/andypgray/resharper-cli-mcp/security/advisories/new). Do not open a public issue for a security report. You can expect an acknowledgment within 7 days.

## Security model

resharper-cli-mcp is a local process, and its threat model follows from that:

- It runs as a stdio subprocess launched by your MCP client, with your user privileges. It opens no network listener, makes no outbound calls, and sends no telemetry. The two tools are the whole surface; there is no plugin search or other network feature.
- Each call shells out to `jb`, the ReSharper Command Line Tools you installed, which loads and analyzes your solution. `jb` reads and evaluates the solution's project files to do that, so inspect an untrusted solution with the same caution you would apply to opening it in an IDE.
- `resharper_cleanup` is the one tool that writes to disk: it rewrites the files you pass, in place. `resharper_inspect` is read-only.
- Logs under `%LOCALAPPDATA%\Zphil.ReSharperCli\logs` can contain absolute paths and the rule IDs and messages read from your solution. The sink keeps 7 daily rolling files, and nothing leaves the machine.

## Verify what you install

Each release ships a signed build provenance attestation. Download the `.nupkg` from the GitHub release and verify it as built:

```bash
gh attestation verify Zphil.ReSharperCli.<version>.nupkg --repo andypgray/resharper-cli-mcp
```

The release also carries the Sigstore bundle (`attestation.intoto.jsonl`) for offline verification with `gh attestation verify --bundle`.

The nuget.org copy differs. nuget.org appends a repository signature (`.signature.p7s`) after upload, which changes the file hash, so the attestation matches the GitHub release copy rather than the file you download from nuget.org. Use the GitHub release asset for digest verification, and `dotnet nuget verify <file>` for the nuget.org repository signature.

`dotnet tool install -g Zphil.ReSharperCli` and, on .NET 10, `dnx Zphil.ReSharperCli` install the same nuget.org package. `resharper-cli-mcp --version` prints `<version>+<commit>`, and that commit matches the release tag on andypgray/resharper-cli-mcp, a source cross-check that needs no tooling.

## Supply chain

Publishing uses NuGet trusted publishing (OIDC), so there is no long-lived API key to store or leak. The release workflow builds, tests, packs, and attests every package. Builds use SourceLink and a deterministic CI configuration. Every GitHub Actions dependency is pinned to a commit SHA, and every NuGet dependency is locked to a content hash in a committed `packages.lock.json` (restored in locked mode on CI); Dependabot keeps both current.
