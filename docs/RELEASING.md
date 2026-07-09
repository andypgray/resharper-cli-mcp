# Releasing

resharper-cli-mcp publishes from a git tag. Pushing a `v*` tag runs [`release.yml`](../.github/workflows/release.yml), which builds, tests, packs, attests, and pushes the package to NuGet using trusted publishing (OIDC), so the repository stores no NuGet API key. A maintainer runs the tag push plus the one-time trust and registry setup below; everything in between is automated.

## What a tag push does

`release.yml` runs on any `v*` tag and:

1. Fails immediately unless the tag (without its `v`) equals the csproj `<Version>` and both version fields in [`.mcp/server.json`](../.mcp/server.json) (`.version` and `.packages[0].version`).
2. Builds and tests in `Release`.
3. Packs the `.nupkg` and `.snupkg`.
4. Emits signed SLSA build provenance with `actions/attest` and stages it as `attestation.intoto.jsonl`.
5. Trades the workflow's OIDC token for a short-lived nuget.org key (`NuGet/login`), then runs `dotnet nuget push --skip-duplicate`.
6. Creates a GitHub release carrying the `.nupkg`, `.snupkg`, and attestation bundle.

Steps 4 to 6 need the one-time setup below to already be in place.

## One-time setup

Do this once, before the first release.

Create the repository and push `main`:

```bash
gh repo create andypgray/resharper-cli-mcp --public --source . --remote origin --push
```

Add the NuGet trusted-publishing policy at [nuget.org trusted publishing](https://www.nuget.org/account/trustedpublishing):

| Field | Value |
|---|---|
| Package owner | `andypgray` |
| Repository owner | `andypgray` |
| Repository | `resharper-cli-mcp` |
| Workflow file | `release.yml` |
| Environment | leave blank |

The `NuGet/login` step authenticates against this policy, so it must exist before the first tag push or the push has no credentials.

Confirm [CI](../.github/workflows/ci.yml) is green on `main`. The release reruns build and test, so a red `main` fails the release too.

## Cut a release

The three version fields must agree or the release stops at step 1. Check them before tagging:

```bash
grep -oP '(?<=<Version>)[^<]+' src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj
jq -r '.version, .packages[0].version' .mcp/server.json
```

Then, using `1.0.0` as the example version:

1. If bumping, set the version in all three places (the csproj `<Version>`, and both `.version` and `.packages[0].version` in `.mcp/server.json`) and commit.
2. Tag and push:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. Watch the run with `gh run watch` or the Actions tab. On success the package is on nuget.org and a GitHub release holds the three assets.

## After NuGet indexes the package

nuget.org takes a few minutes to index a new version. Poll until the readme returns `200` (the package id is lowercased in this URL):

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  https://api.nuget.org/v3-flatcontainer/zphil.resharpercli/1.0.0/readme
```

Then register the server with the MCP registry. Run this after the package is live, because the registry validates the `<!-- mcp-name: ... -->` marker in the published NuGet readme:

```bash
mcp-publisher login github
mcp-publisher publish .mcp/server.json
```

## Harden the repository

Enable once, in repository settings:

- Description and topics, for discovery.
- Private vulnerability reporting, the intake named in [SECURITY.md](../SECURITY.md).
- Dependabot alerts and security updates, the pin freshness that [`dependabot.yml`](../.github/dependabot.yml) and SECURITY.md promise.
- Secret scanning with push protection.
- CodeQL code scanning.
- A ruleset protecting `main`.

## Verify a published release

Confirm the bytes as built, per [SECURITY.md](../SECURITY.md):

```bash
gh attestation verify Zphil.ReSharperCli.1.0.0.nupkg --repo andypgray/resharper-cli-mcp
```

Verify the GitHub release asset, not the nuget.org download: nuget.org appends a repository signature after upload, which changes the file hash.

## Courtesy note (optional)

The name uses "ReSharper" descriptively and the wrapper is unofficial. A short heads-up to marketing@jetbrains.com that the project exists is a courtesy, not an obligation.
