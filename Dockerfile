# An introspection image for MCP directories: it builds this server and speaks stdio, so a
# directory can complete an MCP handshake and read tools/list, resources/list and prompts/list.
#
# It installs no JetBrains software. The ReSharper command-line tools this server wraps are not
# redistributable, so `jb` is absent here and every tool call in this image reports it missing.
# To run the server against a real solution, install the .NET global tool — see the README.

# Both bases are pinned by digest, so a build resolves to these layers rather than to whatever the
# `10.0` tag points at that day. A digest goes stale silently, so .github/dependabot.yml carries a
# docker entry to bump them weekly — without it the pin would freeze the image on an unpatched base.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
WORKDIR /app
COPY --from=build /app .
# No solution is mounted here, so a pre-warm would only probe for the absent `jb`.
ENV RESHARPER_MCP_PREWARM=off
# `--stdio` because a harness is free to allocate a pseudo-terminal (`docker run -it`), and without
# it the server would read that terminal as a human and print a message instead of speaking MCP.
ENTRYPOINT ["dotnet", "Zphil.ReSharperCli.dll", "--stdio"]
