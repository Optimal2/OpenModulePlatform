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
    /// </summary>
    Duplicate,
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
            or PackageLibraryDefinitionProblem.Duplicate;
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
        IReadOnlyList<PackageLibrarySourceDefinition> sourceDefinitions)
    {
        ArgumentNullException.ThrowIfNull(libraryDefinitions);
        ArgumentNullException.ThrowIfNull(sourceDefinitions);

        var findings = new List<PackageLibraryDefinitionFinding>();
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

        // Two readable library files that declare the same module key: the
        // version comparison above cannot catch a rename that kept its version,
        // because neither copy is older than the source. Both files would ship.
        // Files without a readable identity are already blocked as Unreadable.
        foreach (var libraryDefinition in libraryDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(libraryDefinition.ReadError)
                || string.IsNullOrWhiteSpace(libraryDefinition.ModuleKey)
                || string.IsNullOrWhiteSpace(libraryDefinition.DefinitionVersion))
            {
                continue;
            }

            PackageLibraryDefinition? other = null;
            foreach (var candidate in libraryDefinitions)
            {
                if (ReferenceEquals(candidate, libraryDefinition)
                    || string.IsNullOrWhiteSpace(candidate.ModuleKey)
                    || !string.Equals(candidate.ModuleKey, libraryDefinition.ModuleKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                other = candidate;
                break;
            }

            if (other is not null)
            {
                findings.Add(new PackageLibraryDefinitionFinding(
                    libraryDefinition.FileName,
                    libraryDefinition.ModuleKey,
                    libraryDefinition.DefinitionVersion,
                    SourceVersion: null,
                    RepositoryKey: null,
                    PackageLibraryDefinitionProblem.Duplicate,
                    OtherFileName: other.FileName,
                    OtherVersion: other.DefinitionVersion));
            }
        }

        return findings;
    }

    public static IReadOnlyList<string> DescribeFindings(
        IReadOnlyList<PackageLibraryDefinitionFinding> findings,
        int checkedCount)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var lines = new List<string>();
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
                        + $"({finding.OtherVersion}) both declare this module key; the package would ship both. Remove the leftover file.");
                    break;
            }
        }

        var stale = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Stale);
        var orphaned = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Orphan);
        var unreadable = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Unreadable);
        var duplicate = findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Duplicate);
        lines.Add(
            $"Package library definitions: {checkedCount} file(s) checked; {stale} stale, {orphaned} orphaned, {unreadable} unreadable, {duplicate} duplicate.");

        return lines;
    }

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
    IReadOnlyList<PackageLibraryDefinitionFinding> Findings)
{
    public int StaleCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Stale);

    public int OrphanCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Orphan);

    public int UnreadableCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Unreadable);

    public int DuplicateCount =>
        Findings.Count(finding => finding.Problem == PackageLibraryDefinitionProblem.Duplicate);

    /// <summary>Findings that must stop the package build.</summary>
    public int BlockingCount => Findings.Count(finding => finding.Blocks);

    public IReadOnlyList<string> Describe() =>
        PackageLibraryDefinitionGate.DescribeFindings(Findings, CheckedCount);
}
