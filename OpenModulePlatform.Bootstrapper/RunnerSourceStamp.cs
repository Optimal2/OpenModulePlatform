// File: OpenModulePlatform.Bootstrapper/RunnerSourceStamp.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// Outcome of comparing the installer runner with the OpenModulePlatform sources it was built from.
/// </summary>
/// <param name="Refuse">True when the refresh must not run with this runner.</param>
/// <param name="Message">One line that says what was compared, or why nothing was.</param>
internal sealed record RunnerFreshness(bool Refuse, string Message);

/// <summary>
/// Detects an installer runner that is older than the OpenModulePlatform sources next to it.
/// </summary>
/// <remarks>
/// The package root carries a tracked OpenModulePlatform.Bootstrapper.exe that is only replaced
/// by scripts/deployment/update-installer-runner-only.ps1. Nothing used to notice when that
/// runner fell behind the sources. Measured 2026-11-27: a runner built 2026-10-20 still hashed
/// artifact directories with case-insensitive path ordering, while the host agent had switched
/// to ordinal ordering on 2026-11-21. The pre-stage gate therefore reported 27 byte-identical
/// artifacts as "changed under the same version" and refused to stage them.
///
/// The update script asks the new runner to write a stamp of the sources it was built from
/// (--write-runner-source-stamp). A refresh run by the runner in the package root compares that
/// stamp with the current sources and refuses on a mismatch or a missing stamp. The stamp is the
/// scoped source stamp of the runner's own project closure, so commits that do not touch the
/// installer do not make the runner stale.
/// </remarks>
internal static class RunnerSourceStamp
{
    internal const string StampFileSuffix = ".source-stamp.json";

    private const int FormatVersion = 1;

    internal static string GetStampPath(string runnerPath) => runnerPath + StampFileSuffix;

    internal static string GetRunnerProjectPath(string repositoryRoot)
        => Path.Join(repositoryRoot, "OpenModulePlatform.Bootstrapper", "OpenModulePlatform.Bootstrapper.csproj");

    /// <summary>The configured source root that holds the runner's own project, or null.</summary>
    internal static string? FindRepositoryRoot(IEnumerable<string> sourceRoots)
        => sourceRoots.FirstOrDefault(root =>
            !string.IsNullOrWhiteSpace(root) && File.Exists(GetRunnerProjectPath(root)));

    /// <summary>The stamp of the runner sources under <paramref name="repositoryRoot"/>, or null.</summary>
    internal static string? Compute(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var project = GetRunnerProjectPath(root);
        return File.Exists(project)
            ? Program.ComputeRunnerScopedSourceStamp(root, project)
            : null;
    }

    /// <summary>Writes the stamp of <paramref name="repositoryRoot"/> next to <paramref name="runnerPath"/>.</summary>
    internal static string Write(string runnerPath, string repositoryRoot)
    {
        var stamp = Compute(repositoryRoot)
            ?? throw new InvalidOperationException(
                $"Could not compute the runner source stamp: {GetRunnerProjectPath(repositoryRoot)} is missing or its project closure could not be resolved.");
        var document = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["sourceStamp"] = stamp,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        File.WriteAllText(
            GetStampPath(runnerPath),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
        return stamp;
    }

    /// <summary>
    /// Compares the runner with the sources. Only the runner in the package root is checked:
    /// that is the tracked copy that falls behind. A runner started from a build output folder
    /// is the source itself.
    /// </summary>
    internal static RunnerFreshness Evaluate(string? runnerPath, string payloadRoot, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(runnerPath) || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return new RunnerFreshness(false, "Runner source check: skipped, no OpenModulePlatform source root is configured.");
        }

        var runnerDirectory = Path.GetDirectoryName(Path.GetFullPath(runnerPath));
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(runnerDirectory ?? string.Empty),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot)),
                StringComparison.OrdinalIgnoreCase))
        {
            return new RunnerFreshness(false, "Runner source check: skipped, this runner is not the package-root copy.");
        }

        var expected = Compute(repositoryRoot);
        if (expected is null)
        {
            // Absence of a measurement must never read as a passing measurement.
            return new RunnerFreshness(true, $"Runner source check: could not compute the source stamp of {GetRunnerProjectPath(repositoryRoot)}.{UpdateHint(runnerPath, repositoryRoot)}");
        }

        var stampPath = GetStampPath(runnerPath);
        string? recorded;
        try
        {
            recorded = File.Exists(stampPath)
                ? JsonNode.Parse(File.ReadAllText(stampPath, Encoding.UTF8))?["sourceStamp"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            recorded = null;
        }

        if (string.IsNullOrWhiteSpace(recorded))
        {
            return new RunnerFreshness(true, $"Runner source check: {stampPath} is missing or unreadable, so the runner cannot show which sources it was built from.{UpdateHint(runnerPath, repositoryRoot)}");
        }

        return string.Equals(recorded.Trim(), expected, StringComparison.OrdinalIgnoreCase)
            ? new RunnerFreshness(false, "Runner source check: the runner matches the installer sources.")
            : new RunnerFreshness(true, $"Runner source check: the runner was built from older installer sources than {repositoryRoot}. Its artifact hashing and gates can disagree with the host agent.{UpdateHint(runnerPath, repositoryRoot)}");
    }

    private static string UpdateHint(string runnerPath, string repositoryRoot)
        => " Update it: powershell -NoProfile -File \""
           + Path.Join(repositoryRoot, "scripts", "deployment", "update-installer-runner-only.ps1")
           + "\" -PackageRoot \"" + Path.GetDirectoryName(Path.GetFullPath(runnerPath)) + "\" -RepositoryRoot \"" + repositoryRoot + "\"";
}

internal static partial class Program
{
    internal static string? ComputeRunnerScopedSourceStamp(string repositoryRoot, string projectFile)
        => InstallerForm.ComputeScopedSourceStateStamp(repositoryRoot, projectFile, includeSourceRoot: false);

    /// <summary>
    /// --write-runner-source-stamp --repository-root &lt;path&gt;: records which installer sources
    /// this runner was built from, next to the runner. Run by update-installer-runner-only.ps1.
    /// </summary>
    private static int RunWriteRunnerSourceStamp(string[] args)
    {
        var index = Array.IndexOf(args, "--repository-root");
        if (args.Length != 3 || index != 1)
        {
            Console.Error.WriteLine("Usage: OpenModulePlatform.Bootstrapper.exe --write-runner-source-stamp --repository-root <OpenModulePlatform repository>");
            return 1;
        }

        var runnerPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(runnerPath))
        {
            Console.Error.WriteLine("The runner path could not be determined.");
            return 1;
        }

        try
        {
            var stamp = RunnerSourceStamp.Write(runnerPath, args[2]);
            Console.WriteLine($"Runner source stamp {stamp} written to {RunnerSourceStamp.GetStampPath(runnerPath)}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
