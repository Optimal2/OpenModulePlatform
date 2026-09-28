using System.Text.Json.Nodes;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>Why a package-library module definition file was flagged.</summary>
internal enum PackageLibraryDefinitionProblem
{
    /// <summary>
    /// The library file is owned by a configured source repository but carries an
    /// older definition version than that source. Blocking: the refresh should
    /// have updated it, so a stale copy means the package ships outdated content.
    /// </summary>
    Stale,

    /// <summary>
    /// No configured source repository owns the library file's module key. Listed
    /// but not blocking: a developer machine that has not cloned every configured
    /// repository would otherwise be unable to sync at all, because the roots of
    /// missing repositories are silently dropped from the resolved source set.
    /// The listing is repeated on every run so the file cannot disappear silently
    /// into shipped packages.
    /// </summary>
    Orphan,

    /// <summary>
    /// The library file could not be read as a module definition. Blocking: a file
    /// whose identity cannot be verified must not read as a passing check.
    /// </summary>
    Unreadable,

    /// <summary>
    /// Another library file declares the same module key. Blocking: this is what a
    /// definition-file rename looks like when the version was not bumped - the
    /// sync loop copies by file name, so the lingering old copy is never
    /// overwritten, and the package would ship both files for the same module.
    /// One finding per module key, naming every file that declares it.
    /// </summary>
    Duplicate,

    /// <summary>
    /// A moduleDefinitions entry in a source repository manifest lacks moduleKey,
    /// definitionVersion or path. Blocking: the sync skips such an entry, so the
    /// library file it should own is never updated and would otherwise read as
    /// an orphan with advice that does not apply (the repository IS configured).
    /// </summary>
    IncompleteSource,
}

/// <summary>One module definition file as it sits in the package library.</summary>
/// <param name="FileName">Library file name, used to name the offender.</param>
/// <param name="ModuleKey">moduleKey read from the file, or null when unreadable.</param>
/// <param name="DefinitionVersion">definitionVersion read from the file, or null.</param>
/// <param name="ReadError">Why the file could not be read, when it could not.</param>
internal sealed record PackageLibraryDefinition(
    string FileName,
    string? ModuleKey,
    string? DefinitionVersion,
    string? ReadError = null);

/// <summary>One moduleDefinitions entry from a source repository manifest.</summary>
internal sealed record PackageLibrarySourceDefinition(
    string ModuleKey,
    string DefinitionVersion,
    string RepositoryKey);

/// <summary>
/// A moduleDefinitions entry the sync cannot use because a required field is empty.
/// </summary>
/// <param name="RepositoryKey">The manifest's repository.</param>
/// <param name="Index">Zero-based position in the manifest's moduleDefinitions array.</param>
/// <param name="ModuleKey">moduleKey as written, or empty.</param>
/// <param name="MissingFields">The empty fields, for example "definitionVersion, path".</param>
internal sealed record PackageLibraryIncompleteSourceEntry(
    string RepositoryKey,
    int Index,
    string ModuleKey,
    string MissingFields);

/// <summary>A configured developer source root that the resolution dropped.</summary>
/// <param name="Path">The configured path.</param>
/// <param name="Reason">Why it is not part of the resolved source set.</param>
internal sealed record SkippedDeveloperSourceRoot(string Path, string Reason);

/// <summary>One flagged package-library definition file.</summary>
/// <param name="FileName">Library file name, used to name the offender.</param>
/// <param name="ModuleKey">moduleKey the finding is about.</param>
/// <param name="PackageVersion">definitionVersion read from the file, or empty.</param>
/// <param name="SourceVersion">The owning source's version, for Stale findings.</param>
/// <param name="RepositoryKey">The owning source repository, when known.</param>
/// <param name="Problem">Why the file was flagged.</param>
/// <param name="OtherFileName">The other file in a Duplicate finding.</param>
/// <param name="OtherVersion">Its definition version, when known.</param>
internal sealed record PackageLibraryDefinitionFinding(
    string FileName,
    string ModuleKey,
    string PackageVersion,
    string? SourceVersion,
    string? RepositoryKey,
    PackageLibraryDefinitionProblem Problem,
    string? OtherFileName = null,
    string? OtherVersion = null)
{
    /// <summary>Whether this finding must stop the package build.</summary>
    public bool Blocks =>
        Problem is PackageLibraryDefinitionProblem.Stale
            or PackageLibraryDefinitionProblem.Unreadable
            or PackageLibraryDefinitionProblem.Duplicate
            or PackageLibraryDefinitionProblem.IncompleteSource;
}

/// <summary>
/// The reverse direction of the module-definition sync, as a pure function.
/// </summary>
/// <remarks>
/// The sync loop iterates SOURCE definitions: for every module definition a
/// configured repository owns, it copies the newest version into the package
/// library. A library file whose owning repository is NOT part of the configured
/// source roots is never visited by that loop - it keeps shipping in every
/// universal package at whatever version it happens to carry, without ever being
/// updated. The same blind spot exists for a library file that lingers under an
/// old name after a repository renamed its definition file; a rename without a
/// version bump is caught by the duplicate check instead.
///
/// This gate walks the library instead: every definition file in
/// data/global/module-definitions must be owned by a configured source
/// repository and must not be older than that source. Stale, duplicate and
/// unreadable findings block the package build; orphaned files are listed on
/// every run with the two ways to resolve them. Orphans stay non-blocking
/// because a source root whose repository is not cloned locally is dropped
/// silently, and blocking on that would make every sync fail on machines that
/// simply have fewer clones.
/// </remarks>
internal static class PackageLibraryDefinitionGate
{
    public static IReadOnlyList<PackageLibraryDefinitionFinding> Evaluate(
        IReadOnlyList<PackageLibraryDefinition> libraryDefinitions,
        IReadOnlyList<PackageLibrarySourceDefinition> sourceDefinitions,
        IReadOnlyList<PackageLibraryIncompleteSourceEntry>? incompleteSourceEntries = null)
    {
        ArgumentNullException.ThrowIfNull(libraryDefinitions);
        ArgumentNullException.ThrowIfNull(sourceDefinitions);
        incompleteSourceEntries ??= [];

        var findings = new List<PackageLibraryDefinitionFinding>();
        foreach (var entry in incompleteSourceEntries)
        {
            findings.Add(new PackageLibraryDefinitionFinding(
                $"{entry.RepositoryKey} omp-components.json moduleDefinitions[{entry.Index}]",
                entry.ModuleKey,
                PackageVersion: string.Empty,
                SourceVersion: null,
                entry.RepositoryKey,
                PackageLibraryDefinitionProblem.IncompleteSource,
                OtherFileName: entry.MissingFields));
        }

        foreach (var libraryDefinition in libraryDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(libraryDefinition.ReadError)
                || string.IsNullOrWhiteSpace(libraryDefinition.ModuleKey)
                || string.IsNullOrWhiteSpace(libraryDefinition.DefinitionVersion))
            {
                findings.Add(new PackageLibraryDefinitionFinding(
                    libraryDefinition.FileName,
                    string.IsNullOrWhiteSpace(libraryDefinition.ModuleKey)
                        ? libraryDefinition.FileName
                        : libraryDefinition.ModuleKey,
                    libraryDefinition.DefinitionVersion ?? string.Empty,
                    SourceVersion: null,
                    RepositoryKey: null,
                    PackageLibraryDefinitionProblem.Unreadable));
                continue;
            }

            var newestSource = FindNewestOwningSource(libraryDefinition.ModuleKey, sourceDefinitions);
            if (newestSource is null)
            {
                if (incompleteSourceEntries.Any(entry =>
                        string.Equals(entry.ModuleKey, libraryDefinition.ModuleKey, StringComparison.OrdinalIgnoreCase)))
                {
                    // A configured repository does claim this module; its entry is
                    // incomplete and already blocks. ORPHAN advice would mislead.
                    continue;
                }

                findings.Add(new PackageLibraryDefinitionFinding(
                    libraryDefinition.FileName,
                    libraryDefinition.ModuleKey,
                    libraryDefinition.DefinitionVersion,
                    SourceVersion: null,
                    RepositoryKey: null,
                    PackageLibraryDefinitionProblem.Orphan));
                continue;
            }

            if (Program.CompareVersionText(libraryDefinition.DefinitionVersion, newestSource.DefinitionVersion) < 0)
            {
                findings.Add(new PackageLibraryDefinitionFinding(
                    libraryDefinition.FileName,
                    libraryDefinition.ModuleKey,
                    libraryDefinition.DefinitionVersion,
                    newestSource.DefinitionVersion,
                    newestSource.RepositoryKey,
                    PackageLibraryDefinitionProblem.Stale));
            }
        }

        // Two or more readable library files that declare the same module key:
        // the version comparison above cannot catch a rename that kept its
        // version, because neither copy is older than the source. Every file
        // would ship. One finding per module key, so the count is the number of
        // modules to clean up, not the number of files involved. Files without
        // a readable identity are already blocked as Unreadable.
        var duplicateGroups = libraryDefinitions
            .Where(definition => string.IsNullOrWhiteSpace(definition.ReadError)
                && !string.IsNullOrWhiteSpace(definition.ModuleKey)
                && !string.IsNullOrWhiteSpace(definition.DefinitionVersion))
            .GroupBy(definition => definition.ModuleKey!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);
        foreach (var group in duplicateGroups)
        {
            var first = group.First();
            var others = group.Skip(1).ToArray();
            findings.Add(new PackageLibraryDefinitionFinding(
                first.FileName,
                first.ModuleKey!,
                first.DefinitionVersion!,
                SourceVersion: null,
                RepositoryKey: null,
                PackageLibraryDefinitionProblem.Duplicate,
                OtherFileName: string.Join(", ", others.Select(other => other.FileName)),
                OtherVersion: string.Join(", ", others.Select(other => other.DefinitionVersion))));
        }

        return findings;
    }

    public static IReadOnlyList<string> DescribeFindings(
        IReadOnlyList<PackageLibraryDefinitionFinding> findings,
        int checkedCount,
        IReadOnlyList<SkippedDeveloperSourceRoot>? skippedSourceRoots = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        skippedSourceRoots ??= [];

        var lines = new List<string>();
        // Listed first: a dropped root is the usual reason a module below reads
        // as ORPHAN, and nothing else in the output names the dropped path.
        foreach (var skipped in skippedSourceRoots)
        {
            lines.Add($"  SKIPPED source root {skipped.Path}: {skipped.Reason}; module definitions it owns are not checked against it.");
        }

        foreach (var finding in findings)
        {
            switch (finding.Problem)
            {
                case PackageLibraryDefinitionProblem.Stale:
                    lines.Add(
                        $"  STALE   {finding.ModuleKey}: {finding.FileName} is {finding.PackageVersion} but source repository "
                        + $"{finding.RepositoryKey} has {finding.SourceVersion}; the refresh did not update it.");
                    break;

                case PackageLibraryDefinitionProblem.Orphan:
                    lines.Add(
                        $"  ORPHAN  {finding.ModuleKey}: {finding.FileName} ({finding.PackageVersion}) is not owned by any configured "
                        + "source repository; refresh never updates it, but it still ships in every universal package. "
                        + "Add the owning repository to developerSource.sourceRoot or remove the file.");
                    break;

                case PackageLibraryDefinitionProblem.Unreadable:
                    lines.Add(
                        $"  BROKEN  {finding.FileName}: moduleKey/definitionVersion could not be read; the file ships in every "
                        + "universal package unverified.");
                    break;

                case PackageLibraryDefinitionProblem.Duplicate:
                    lines.Add(
                        $"  DUPLICATE {finding.ModuleKey}: {finding.FileName} ({finding.PackageVersion}) and {finding.OtherFileName} "
                        + $"({finding.OtherVersion}) declare the same module key; the package would ship every copy. Remove the leftover file(s).");
                    break;

                case PackageLibraryDefinitionProblem.IncompleteSource:
                    lines.Add(
                        $"  BROKEN  {finding.FileName}"
                        + (string.IsNullOrWhiteSpace(finding.ModuleKey) ? string.Empty : $" ({finding.ModuleKey})")
                        + $": {finding.OtherFileName} is empty; the refresh skips this entry, so the library copy it should own "
                        + "is never updated. Complete the entry in the manifest.");
                    break;
            }
        }

        var stale = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Stale);
        var orphaned = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Orphan);
        var unreadable = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Unreadable);
        var duplicate = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Duplicate);
        var incomplete = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.IncompleteSource);
        lines.Add(
            $"Package library definitions: {checkedCount} file(s) checked; {stale} stale, {orphaned} orphaned, {unreadable} unreadable, "
            + $"{duplicate} duplicate, {incomplete} incomplete source entry(ies); {skippedSourceRoots.Count} configured source root(s) skipped.");

        return lines;
    }

    /// <summary>
    /// The moduleDefinitions entries of one manifest that the sync skips because
    /// moduleKey, definitionVersion or path is empty (or the entry is not an
    /// object). The sync reads the same fields the same way, so this is exactly
    /// the set it drops.
    /// </summary>
    public static IReadOnlyList<PackageLibraryIncompleteSourceEntry> FindIncompleteManifestEntries(
        JsonNode? manifest,
        string repositoryKey)
    {
        if (GetProperty(manifest, "moduleDefinitions") is not JsonArray items)
        {
            return [];
        }

        var entries = new List<PackageLibraryIncompleteSourceEntry>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var missing = new List<string>();
            foreach (var field in (string[])["moduleKey", "definitionVersion", "path"])
            {
                if (string.IsNullOrWhiteSpace(GetString(item, field)))
                {
                    missing.Add(field);
                }
            }

            if (missing.Count > 0)
            {
                entries.Add(new PackageLibraryIncompleteSourceEntry(
                    repositoryKey,
                    index,
                    GetString(item, "moduleKey"),
                    string.Join(", ", missing)));
            }
        }

        return entries;
    }

    /// <summary>
    /// The configured developer source roots that are not part of the resolved
    /// source set, each with the reason. A root configured twice (also with a
    /// different case or a trailing separator) is one root, not a skip.
    /// </summary>
    public static IReadOnlyList<SkippedDeveloperSourceRoot> FindSkippedSourceRoots(
        IEnumerable<string> configuredRoots,
        IReadOnlyList<string> resolvedRoots,
        Func<string, bool> directoryExists,
        Func<string, bool> hasManifest)
    {
        ArgumentNullException.ThrowIfNull(configuredRoots);
        ArgumentNullException.ThrowIfNull(resolvedRoots);

        var resolved = new HashSet<string>(resolvedRoots.Select(NormalizeSourceRoot), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<SkippedDeveloperSourceRoot>();
        foreach (var configuredRoot in configuredRoots)
        {
            var root = NormalizeSourceRoot(configuredRoot);
            if (!seen.Add(root) || resolved.Contains(root))
            {
                continue;
            }

            var reason = !directoryExists(root)
                ? "folder not found"
                : !hasManifest(root)
                    ? "no omp-components.json"
                    : "not used because no configured root is an OpenModulePlatform source repository";
            skipped.Add(new SkippedDeveloperSourceRoot(root, reason));
        }

        return skipped;
    }

    /// <summary>Full path without a trailing separator, so one folder has one spelling.</summary>
    public static string NormalizeSourceRoot(string root)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static JsonNode? GetProperty(JsonNode? node, string propertyName)
        => node is JsonObject obj
            ? obj.FirstOrDefault(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value
            : null;

    private static string GetString(JsonNode? node, string propertyName)
        => GetProperty(node, propertyName) is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;

    private static PackageLibrarySourceDefinition? FindNewestOwningSource(
        string moduleKey,
        IReadOnlyList<PackageLibrarySourceDefinition> sourceDefinitions)
    {
        // Several repositories may declare the same module key (a fork or a
        // rename in progress); the library copy is judged against the newest of
        // them, because that is the version the sync loop would copy.
        PackageLibrarySourceDefinition? newest = null;
        foreach (var source in sourceDefinitions)
        {
            if (!string.Equals(source.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (newest is null
                || Program.CompareVersionText(source.DefinitionVersion, newest.DefinitionVersion) > 0)
            {
                newest = source;
            }
        }

        return newest;
    }
}

/// <summary>The gate's result for one package library.</summary>
internal sealed record PackageLibraryDefinitionGateReport(
    int CheckedCount,
    IReadOnlyList<PackageLibraryDefinitionFinding> Findings,
    IReadOnlyList<SkippedDeveloperSourceRoot>? SkippedSourceRoots = null)
{
    public int StaleCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Stale);

    public int OrphanCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Orphan);

    public int UnreadableCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Unreadable);

    public int DuplicateCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Duplicate);

    public int IncompleteSourceCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.IncompleteSource);

    /// <summary>Findings that must stop the package build.</summary>
    public int BlockingCount => Findings.Count(finding => finding.Blocks);

    public IReadOnlyList<string> Describe() =>
        PackageLibraryDefinitionGate.DescribeFindings(Findings, CheckedCount, SkippedSourceRoots);
}
