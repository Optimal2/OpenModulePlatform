// Shared UI test support. Link this file into a repo's UI test project the
// same way OmpTestDatabaseProvisioner.cs is linked:
//   <Compile Include="$(OpenModulePlatformRoot)\tests\shared\Ui\*.cs" Link="TestSupport\Ui\%(Filename)%(Extension)" />
// The consuming project needs Microsoft.NET.Test.Sdk 18.10.0,
// Microsoft.Playwright 1.62.0, xunit 2.9.3,
// xunit.runner.visualstudio 4.0.0, and Xunit.SkippableFact 1.5.85.
// Runner 4.0.0 supports the xUnit.net v2 core used by these fixtures; do not
// migrate the shared fixture APIs to xunit.v3 solely for the runner upgrade.

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// Resolves repository-relative paths from the test assembly's output
/// location, so tests run identically from the IDE, dotnet test and CI.
/// </summary>
public static class UiTestPaths
{
    /// <summary>
    /// Set to 1 or true by a gate that runs the UI suite on purpose.
    /// </summary>
    public const string RequiredEnvironmentVariable = "OMP_UITESTS_REQUIRED";

    /// <summary>
    /// Whether <paramref name="value"/> (of <see cref="RequiredEnvironmentVariable"/>) asks for a required UI run.
    /// </summary>
    public static bool IsRequired(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this run requires the UI suite to run rather than skip: set by
    /// a gate (for example a consumer's local-ci with UI tests switched on).
    /// </summary>
    public static bool IsRequired() => IsRequired(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable));

    /// <summary>
    /// Walks up from the test assembly until the directory containing
    /// <paramref name="solutionFileName"/> is found.
    /// </summary>
    public static string FindRepoRoot(string solutionFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, solutionFileName)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not locate the repository root ({solutionFileName}).");
    }

    /// <summary>
    /// The build configuration and target framework the tests were compiled
    /// with, recovered from the output path, so the app-under-test starts from
    /// the matching output. The target framework segment anchors the search,
    /// so extra segments are tolerated: a project folder after bin
    /// (OmpIsolatedBuildRoot: …\bin\{Project}\{Configuration}\{Tfm}\), a
    /// platform folder, or a runtime identifier after the framework.
    /// </summary>
    public static (string Configuration, string Tfm) BuildOutputSegments()
        => BuildOutputSegments(AppContext.BaseDirectory);

    /// <summary>
    /// <see cref="BuildOutputSegments()"/> for an explicit output directory.
    /// </summary>
    public static (string Configuration, string Tfm) BuildOutputSegments(string baseDirectory)
    {
        var segments = Segments(baseDirectory);
        var tfmIndex = TfmIndex(segments);
        return tfmIndex > 0 ? (segments[tfmIndex - 1], segments[tfmIndex]) : ("Release", "net10.0");
    }

    /// <summary>
    /// The built executable of the app under test, or null when none is found.
    /// See <see cref="AppExecutableCandidates"/> for where it is looked for.
    /// </summary>
    public static string? FindAppExecutable(string projectDirectory, string projectName, string assemblyName, string baseDirectory)
        => AppExecutableCandidates(projectDirectory, projectName, assemblyName, baseDirectory).FirstOrDefault(File.Exists);

    /// <summary>
    /// Where the app's executable may be, in order: the default in-tree layout
    /// ({project}\bin\{Configuration}\{Tfm}\), then the OmpIsolatedBuildRoot
    /// layout next to the test project's own output
    /// ({root}\bin\{ProjectName}\{Configuration}\{Tfm}\), for the project name
    /// given and for every .csproj in the project folder.
    /// </summary>
    public static IReadOnlyList<string> AppExecutableCandidates(string projectDirectory, string projectName, string assemblyName, string baseDirectory)
    {
        var (configuration, tfm) = BuildOutputSegments(baseDirectory);
        var exeName = assemblyName + ".exe";
        var candidates = new List<string> { Path.Join(projectDirectory, "bin", configuration, tfm, exeName) };

        // …\bin\{TestProject}\{Configuration}\{Tfm}: the folder holding every project's output.
        var segments = Segments(baseDirectory);
        var tfmIndex = TfmIndex(segments);
        if (tfmIndex >= 3)
        {
            var outputRoot = string.Join(Path.DirectorySeparatorChar, segments[..(tfmIndex - 2)]);
            var projectNames = new List<string> { projectName };
            if (Directory.Exists(projectDirectory))
            {
                projectNames.AddRange(Directory.EnumerateFiles(projectDirectory, "*.csproj").Select(Path.GetFileNameWithoutExtension)!);
            }

            foreach (var name in projectNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Join(outputRoot, name, configuration, tfm, exeName));
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string[] Segments(string directory)
        => directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    // The last segment that looks like a target framework (net10.0, net10.0-windows).
    private static int TfmIndex(string[] segments)
        => Array.FindLastIndex(segments, s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^net\d+\.\d+(-[A-Za-z0-9.]+)?$"));

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

        return Path.GetFullPath(Path.Join(repoRoot, "..", "OpenModulePlatform"));
    }
}
