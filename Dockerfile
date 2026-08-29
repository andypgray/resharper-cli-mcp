# An introspection image for MCP directories: it builds this server and speaks stdio, so a
# directory can complete an MCP handshake and read tools/list, resources/list and prompts/list.
#
# It installs no JetBrains software. The ReSharper command-line tools this server wraps are not
# redistributable, so `jb` is absent here and every tool call in this image reports it missing.
# To run the server against a real solution, install the .NET global tool — see the README.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# No solution is mounted here, so a pre-warm would only probe for the absent `jb`.
ENV RESHARPER_MCP_PREWARM=off
# `--stdio` because a harness is free to allocate a pseudo-terminal (`docker run -it`), and without
# it the server would read that terminal as a human and print a message instead of speaking MCP.
ENTRYPOINT ["dotnet", "Zphil.ReSharperCli.dll", "--stdio"]
