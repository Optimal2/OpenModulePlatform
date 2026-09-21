// File: OpenModulePlatform.Portal.Tests/Configuration/PackagedNLogConfigurationTests.cs
using Microsoft.Extensions.Configuration;
using NLog.Extensions.Logging;

namespace OpenModulePlatform.Portal.Tests.Configuration;

/// <summary>
/// The checked-in appsettings.json of a web app never reaches a host: the artifact payload
/// strips it and HostAgent writes the component's Packaging/appsettings.json instead. An app
/// packaged without an NLog section therefore runs with no log targets at all. These tests
/// parse every packaged and checked-in NLog section the way NLog does at startup
/// (throwConfigExceptions is on in all of them), so a missing section, a missing target or
/// an unknown layout renderer such as a mistyped ${scopeproperty} fails here instead of on
/// the host.
/// </summary>
public sealed class PackagedNLogConfigurationTests
{
    [Theory]
    [InlineData("OpenModulePlatform.Portal", "appsettings.json", true)]
    [InlineData("OpenModulePlatform.Portal", "Packaging/appsettings.json", true)]
    [InlineData("OpenModulePlatform.Auth", "appsettings.json", false)]
    [InlineData("OpenModulePlatform.Auth", "Packaging/appsettings.json", false)]
    [InlineData("OpenModulePlatform.Web.ContentWebAppModule", "appsettings.json", true)]
    [InlineData("OpenModulePlatform.Web.ContentWebAppModule", "Packaging/appsettings.json", true)]
    [InlineData("OpenModulePlatform.Web.iFrameWebAppModule", "appsettings.json", true)]
    public void NLogSection_ParsesWithFileAndConsoleTargets(string project, string relativePath, bool rendersCorrelationId)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepositoryPaths.Resolve(project, relativePath), optional: false)
            .Build();
        var section = configuration.GetSection("NLog");
        Assert.True(section.Exists(), $"{project}/{relativePath} has no NLog section.");

        var nlogConfiguration = new NLogLoggingConfiguration(section);

        var fileTarget = Assert.IsType<NLog.Targets.FileTarget>(nlogConfiguration.FindTargetByName("logfile"));
        Assert.NotNull(nlogConfiguration.FindTargetByName("console"));
        Assert.NotEmpty(nlogConfiguration.LoggingRules);

        var layout = fileTarget.Layout.ToString() ?? string.Empty;
        Assert.Equal(rendersCorrelationId, layout.Contains("${scopeproperty:item=CorrelationId}", StringComparison.Ordinal));
    }

    private static class RepositoryPaths
    {
        public static string Resolve(string project, string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
                {
                    return Path.Join(directory.FullName, project, relativePath);
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
        }
    }
}
