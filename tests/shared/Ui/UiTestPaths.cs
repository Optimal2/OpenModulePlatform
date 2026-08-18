// Shared UI test support. Link this file into a repo's UI test project the
// same way OmpTestDatabaseProvisioner.cs is linked:
//   <Compile Include="$(OpenModulePlatformRoot)\tests\shared\Ui\*.cs" Link="TestSupport\Ui\%(Filename)%(Extension)" />
// The consuming project needs the Microsoft.Playwright, xunit and
// Xunit.SkippableFact packages.

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// Resolves repository-relative paths from the test assembly's output
/// location, so tests run identically from the IDE, dotnet test and CI.
/// </summary>
public static class UiTestPaths
{
    /// <summary>
    /// Walks up from the test assembly until the directory containing
    /// <paramref name="solutionFileName"/> is found.
    /// </summary>
    public static string FindRepoRoot(string solutionFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, solutionFileName)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not locate the repository root ({solutionFileName}).");
    }

    /// <summary>
    /// The build configuration and target framework the tests were compiled
    /// with, recovered from the output path (…\bin\{Configuration}\{Tfm}\…),
    /// so the app-under-test starts from the matching output.
    /// </summary>
    public static (string Configuration, string Tfm) BuildOutputSegments()
    {
        var segments = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar);
        var binIndex = Array.FindLastIndex(segments, s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase));
        if (binIndex < 0 || binIndex + 2 >= segments.Length + 1)
        {
            return ("Release", "net10.0");
        }

        var configuration = binIndex + 1 < segments.Length ? segments[binIndex + 1] : "Release";
        var tfm = binIndex + 2 < segments.Length ? segments[binIndex + 2] : "net10.0";
        return (configuration, tfm);
    }

    /// <summary>
    /// Mirrors the OpenModulePlatformRoot default from the repos'
    /// Directory.Build.targets: the sibling checkout unless the environment
    /// variable overrides it.
    /// </summary>
    public static string OpenModulePlatformRoot(string repoRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("OpenModulePlatformRoot");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return Path.GetFullPath(Path.Combine(repoRoot, "..", "OpenModulePlatform"));
    }
}
