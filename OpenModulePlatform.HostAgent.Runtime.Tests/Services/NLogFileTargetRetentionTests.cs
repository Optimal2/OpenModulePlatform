using System.Text.Json;
using System.Text.Json.Nodes;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Every NLog file target the repository checks in or generates keeps 14 days of files.
/// </summary>
/// <remarks>
/// A file target without maxArchiveDays never removes a day file. The limit was added to
/// the checked-in appsettings.json files first, and then turned out to be missing from
/// three places that produce the configuration a host actually runs with: the template
/// HostAgent builds for WorkerManager, the file the HostAgent-first packaging registers on
/// the WorkerManager artifact (which replaces that template), and the NLog section a
/// HostAgent self-upgrade writes when the settings have none. Each was found one at a time.
///
/// These tests do not list the sources. They find them, so the next file or generator that
/// declares a file target is held to the same limit without anyone remembering to add it.
/// </remarks>
public sealed class NLogFileTargetRetentionTests
{
    private const int RetentionDays = 14;

    private static readonly string[] SkippedDirectories =
    [
        "bin", "obj", "node_modules", "artifacts", "TestResults", ".git", ".claude", ".psmodules", ".vs"
    ];

    [Fact]
    public void EveryCheckedInFileTarget_KeepsFourteenDays()
    {
        var root = OmpRepositoryFiles.FindRepositoryRoot();
        var targets = new List<(string File, string Target, JsonObject Definition)>();
        // Every appsettings file, wherever it lies: the project's own, its Packaging copy, the
        // environment variants and the examples.
        foreach (var file in EnumerateRepositoryFiles(root, "appsettings*.json"))
        {
            JsonNode? document;
            try
            {
                document = JsonNode.Parse(
                    File.ReadAllText(file),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
            }
            catch (JsonException)
            {
                // Not every .json in the repository is a configuration file.
                continue;
            }

            if (document is JsonObject settings && settings["NLog"] is JsonObject nlog && nlog["targets"] is JsonObject declared)
            {
                foreach (var (name, definition) in declared)
                {
                    CollectFileTargets(Path.GetRelativePath(root, file), name, definition, targets);
                }
            }
        }

        // The scan found what it is meant to find; an empty scan would prove nothing.
        Assert.True(targets.Count >= 15, $"Only {targets.Count} checked-in NLog file target(s) were found; the scan is not reaching the configuration files.");
        Assert.Contains(targets, target => target.File.StartsWith("examples", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.File.Contains("Packaging", StringComparison.OrdinalIgnoreCase));

        var withoutLimit = targets
            .Where(target => !HasRetentionLimit(target.Definition))
            .Select(target => $"{target.File} (target '{target.Target}')")
            .ToList();
        Assert.True(
            withoutLimit.Count == 0,
            $"NLog file target(s) without maxArchiveDays = {RetentionDays}: " + string.Join("; ", withoutLimit));
    }

    [Fact]
    public void EveryFileTargetWrittenByCode_DeclaresTheLimitBesideTheFileName()
    {
        // A generator builds the target as an object literal: the file name pattern and the
        // limit sit a few lines apart. A pattern with no limit near it is a new source that
        // was written without one.
        var root = OmpRepositoryFiles.FindRepositoryRoot();
        var generators = new List<string>();
        var withoutLimit = new List<string>();
        foreach (var pattern in new[] { "*.cs", "*.ps1" })
        {
            foreach (var file in EnumerateRepositoryFiles(root, pattern))
            {
                var relative = Path.GetRelativePath(root, file);
                if (IsTestSource(relative))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains("${shortdate}.log", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var location = $"{relative}:{index + 1}";
                    generators.Add(location);
                    var window = lines.Skip(Math.Max(0, index - 6)).Take(13);
                    if (!window.Any(line => line.Contains("maxArchiveDays", StringComparison.OrdinalIgnoreCase)
                        && line.Contains(RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
                    {
                        withoutLimit.Add(location);
                    }
                }
            }
        }

        Assert.Contains(generators, location => location.Contains("ArtifactConfigurationFileWriter.cs", StringComparison.Ordinal));
        Assert.Contains(generators, location => location.Contains("HostAgentSelfUpgradeService.cs", StringComparison.Ordinal));
        Assert.Contains(generators, location => location.Contains("package-hostagent-first.ps1", StringComparison.Ordinal));
        Assert.True(
            withoutLimit.Count == 0,
            $"NLog file target(s) written by code without maxArchiveDays = {RetentionDays}: " + string.Join("; ", withoutLimit));
    }

    [Fact]
    public void HostAgentDefaultLoggingSettings_FileTargetKeepsFourteenDays()
    {
        // What a self-upgrade writes into the next HostAgent's settings when they carry no
        // NLog section of their own.
        var settings = new JsonObject();

        HostAgentSelfUpgradeService.EnsureDefaultLoggingSettings(settings);

        var logFile = settings["NLog"]!["targets"]!["logfile"]!.AsObject();
        Assert.Equal("File", logFile["type"]!.GetValue<string>());
        Assert.Equal(RetentionDays, logFile["maxArchiveDays"]!.GetValue<int>());

        var checkedIn = JsonNode.Parse(
            OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.HostAgent.WindowsService", "appsettings.json"))!
            ["NLog"]!["targets"]!["logfile"]!.AsObject();
        Assert.Equal(checkedIn["maxArchiveDays"]!.GetValue<int>(), logFile["maxArchiveDays"]!.GetValue<int>());
        Assert.Equal(checkedIn["fileName"]!.GetValue<string>(), logFile["fileName"]!.GetValue<string>());
    }

    [Fact]
    public void HostAgentDefaultLoggingSettings_LeaveAnExistingNLogSectionAlone()
    {
        var settings = JsonNode.Parse("""{ "NLog": { "targets": { "logfile": { "type": "File", "maxArchiveDays": 3 } } } }""")!.AsObject();

        HostAgentSelfUpgradeService.EnsureDefaultLoggingSettings(settings);

        Assert.Equal(3, settings["NLog"]!["targets"]!["logfile"]!["maxArchiveDays"]!.GetValue<int>());
    }

    private static void CollectFileTargets(
        string file,
        string name,
        JsonNode? definition,
        List<(string File, string Target, JsonObject Definition)> targets)
    {
        if (definition is not JsonObject target)
        {
            return;
        }

        if (target["type"] is JsonValue type
            && type.TryGetValue<string>(out var typeName)
            && string.Equals(typeName, "File", StringComparison.OrdinalIgnoreCase))
        {
            targets.Add((file, name, target));
        }

        // A wrapper (AsyncWrapper, BufferingWrapper...) declares the real target inside it.
        CollectFileTargets(file, name, target["target"], targets);
    }

    private static bool HasRetentionLimit(JsonObject target)
    {
        var limit = target.FirstOrDefault(property => string.Equals(property.Key, "maxArchiveDays", StringComparison.OrdinalIgnoreCase)).Value;
        return limit is JsonValue value
            && ((value.TryGetValue<int>(out var number) && number == RetentionDays)
                || (value.TryGetValue<string>(out var text) && text == RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static bool IsTestSource(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments[0].Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment => segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".UiTests", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if (SkippedDirectories.Contains(info.Name, StringComparer.OrdinalIgnoreCase)
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }
}
