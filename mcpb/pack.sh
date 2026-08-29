#!/usr/bin/env bash
#
# Builds the MCP Bundle (.mcpb) Claude Desktop installs: a zip holding manifest.json, the icon,
# and a framework-dependent publish of the server.
#
# One script drives both a local pack and the release workflow, so what ships is what was tested.
# Everything lands under artifacts/, which is gitignored and owned by the build.
#
# The bundle carries no runtime. `jb` is a .NET global tool, so a machine that can use this server
# already has one; a self-contained per-RID publish would carry the runtime to remove a dependency the
# user must have anyway, and would force an arm64-vs-x64 choice that `platform_overrides` cannot
# express, being keyed by OS rather than architecture. Hence `dotnet server/Zphil.ReSharperCli.dll`.
#
# `mcpb pack` copies Unix permission bits into the zip only when it runs on Unix, so a `binary`
# entry point packed on Windows arrives on macOS without +x. A .dll launched through `dotnet` needs
# no execute bit, which is what makes a Windows pack and a Linux pack interchangeable here.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj"
STAGE="$REPO_ROOT/artifacts/mcpb"

# The csproj <Version> is the one number behind every version site, this archive's name included.
# VersionSiteTests and release.yml hold mcpb/manifest.json to the same number.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ")"
[ -n "$VERSION" ] || { echo "ERROR: no <Version> in $CSPROJ" >&2; exit 1; }

OUTPUT="$REPO_ROOT/artifacts/resharper-cli-mcp-$VERSION.mcpb"

# A stale file left in the staging directory would ship inside the zip.
rm -rf "$STAGE"

# UseAppHost=false drops the native launcher: dead weight when the bundle launches through `dotnet`,
# and the file whose missing +x would fail on macOS. DebugType=none drops the .pdb.
dotnet publish "$CSPROJ" -c Release -o "$STAGE/server" -p:UseAppHost=false -p:DebugType=none

cp "$REPO_ROOT/mcpb/manifest.json" "$STAGE/manifest.json"

# The manifest names "icon.png" because an MCPB icon path is relative to the bundle root. Claude
# Desktop renders it at 512x512, which is why the 512 render is the one staged.
cp "$REPO_ROOT/assets/icon-512.png" "$STAGE/icon.png"

# Pinned rather than floating, the way this repo pins actions by SHA and NuGet by lockfile. `pack`
# validates the staged manifest — schema, and that the icon it names is a PNG that is really there —
# and refuses to write the zip if either fails.
npx --yes @anthropic-ai/mcpb@2.1.2 pack "$STAGE" "$OUTPUT"
