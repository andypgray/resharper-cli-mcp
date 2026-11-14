using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

public sealed class ServerHostBuilderTests
{
    [Fact]
    public void Create_AddsNoConfigurationSource_SoNothingWatchesTheWorkingDirectory()
    {
        // Act
        HostApplicationBuilder builder = ServerHostBuilder.Create();

        // Assert — the default builder's settings files are each a reload-on-change watch over the working
        // directory. The one source left is the empty store ConfigurationManager's constructor adds for its
        // indexer, so nothing is read from a file, the environment or the command line.
        List<string> sources = builder.Configuration.Sources.Select(Describe).ToList();
        sources.ShouldBe(["MemoryConfigurationSource"]);
    }

    /// <summary>Names a source, with a file source's path and reload setting, so a red shows what was added.</summary>
    private static string Describe(IConfigurationSource source)
    {
        return source is FileConfigurationSource file
            ? $"{source.GetType().Name} '{file.Path}' (reload on change: {file.ReloadOnChange})"
            : source.GetType().Name;
    }
}