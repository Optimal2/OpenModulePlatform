using System.Xml.Linq;

namespace OpenModulePlatform.Installer.Tests;

public sealed class SelfContainedRuntimeVersionTests
{
    // The installer is published self-contained, so it carries its own .NET runtime.
    // RuntimeFrameworkVersion pins that runtime; a security bump of the central
    // Microsoft packages must move it too, or the installer keeps shipping the
    // vulnerable runtime (Dependabot alerts 16-26, 2026-11-11).
    [Fact]
    public void RuntimeFrameworkVersion_FollowsTheCentralMicrosoftPackageVersion()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var hostingVersion = props.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == "Microsoft.Extensions.Hosting")
            .Attribute("Version")!.Value;

        var project = XDocument.Load(Path.Combine(root, "OpenModulePlatform.Installer", "OpenModulePlatform.Installer.csproj"));
        var runtimeVersion = project.Descendants("RuntimeFrameworkVersion").Single().Value.Trim();

        Assert.Equal(hostingVersion, runtimeVersion);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
