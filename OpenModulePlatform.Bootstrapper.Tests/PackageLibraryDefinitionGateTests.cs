namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The reverse direction of the module-definition sync.
///
/// The sync loop iterates SOURCE definitions, so a package-library file whose
/// owning repository is not part of developerSource.sourceRoot is never visited:
/// it keeps shipping in every universal package at whatever version it happens to
/// carry. The gate walks the library instead - every definition file in
/// data/global/module-definitions must be owned by a configured source
/// repository, must not be older than that source, and must be the only file
/// declaring its module key.
/// </summary>
public sealed class PackageLibraryDefinitionGateTests
{
    private static PackageLibraryDefinition Library(
        string fileName,
        string moduleKey,
        string version)
        => new(fileName, moduleKey, version);

    private static PackageLibraryDefinition UnreadableLibrary(string fileName)
        => new(fileName, ModuleKey: null, DefinitionVersion: null, ReadError: "JSON is invalid");

    private static PackageLibrarySourceDefinition Source(
        string moduleKey,
        string version,
        string repositoryKey = "demo-repo")
        => new(moduleKey, version, repositoryKey);

    [Fact]
    public void ALibraryDefinitionOlderThanItsSourceIsStale()
    {
        // The whole reason the gate exists: a library copy that lags behind its
        // source repository ships outdated in every universal package.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "demo", "1.2.3")],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Stale, finding.Problem);
        Assert.True(finding.Blocks);
        Assert.Equal("demo", finding.ModuleKey);
        Assert.Equal("1.2.3", finding.PackageVersion);
        Assert.Equal("1.2.6", finding.SourceVersion);
        Assert.Equal("demo-repo", finding.RepositoryKey);
    }

    [Fact]
    public void ALibraryDefinitionNoSourceRepositoryOwnsIsListedAsOrphan()
    {
        // The measured blind spot: the owning repository is missing from the
        // configured source roots, so the sync loop never reaches the file.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "demo", "1.2.3")],
            [Source("other", "0.3.119", "other-repo")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Orphan, finding.Problem);
        Assert.False(finding.Blocks);
    }

    [Fact]
    public void ACurrentLibraryDefinitionOwnedByASourcePassesSilently()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("demo.module-definition.json", "demo", "1.2.6"),
                Library("omp_core.module-definition.json", "omp_core", "0.3.855"),
            ],
            [
                Source("demo", "1.2.6"),
                Source("omp_core", "0.3.855", "platform"),
            ]);

        Assert.Empty(findings);
    }

    [Fact]
    public void ALibraryDefinitionNewerThanItsSourceIsNotStale()
    {
        // A locally built-ahead library is the source side's problem (the status
        // view reports it as DIFF); the gate only refuses packages that would
        // ship something OLDER than the source.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "demo", "1.2.7")],
            [Source("demo", "1.2.6")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void SeveralSourcesWithTheSameModuleKeyAreJudgedAgainstTheNewest()
    {
        // A fork or rename in progress can declare the same module key from two
        // repositories; the sync loop would copy the newest, so that is the
        // version the library copy is judged against.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "demo", "1.2.5")],
            [
                Source("demo", "1.2.4", "old-repo"),
                Source("demo", "1.2.6", "new-repo"),
            ]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Stale, finding.Problem);
        Assert.Equal("1.2.6", finding.SourceVersion);
        Assert.Equal("new-repo", finding.RepositoryKey);
    }

    [Fact]
    public void ALibraryFileLeftBehindByARenameIsStillJudgedByModuleKey()
    {
        // The sync loop copies by file name, so a definition file left behind
        // under an old name is never overwritten - only the module key reveals
        // that it is a stale duplicate of a module the source still owns.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo-old.module-definition.json", "demo", "1.2.0")],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Stale, finding.Problem);
        Assert.Equal("demo-old.module-definition.json", finding.FileName);
    }

    [Fact]
    public void ARenameThatKeepsItsVersionIsFlaggedAsDuplicate()
    {
        // The version comparison cannot catch a rename without a version bump:
        // neither copy is older than the source, but the package would ship
        // both files for the same module. The pair is one finding that names
        // both files, not one finding per file.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("demo-old.module-definition.json", "demo", "1.2.6"),
                Library("demo.module-definition.json", "demo", "1.2.6"),
            ],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Duplicate, finding.Problem);
        Assert.True(finding.Blocks);
        Assert.Equal("demo-old.module-definition.json", finding.FileName);
        Assert.Equal("demo.module-definition.json", finding.OtherFileName);
        Assert.Equal("1.2.6", finding.OtherVersion);
    }

    [Fact]
    public void ADuplicateModuleKeyIsDescribedWithBothFileNames()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("demo-old.module-definition.json", "demo", "1.2.6"),
                Library("demo.module-definition.json", "demo", "1.2.6"),
            ],
            [Source("demo", "1.2.6")]);

        var lines = PackageLibraryDefinitionGate.DescribeFindings(findings, checkedCount: 2);

        Assert.Contains(
            lines,
            line => line.Contains("DUPLICATE", StringComparison.Ordinal)
                && line.Contains("demo-old.module-definition.json", StringComparison.Ordinal)
                && line.Contains("demo.module-definition.json", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreeFilesForOneModuleKeyAreOneDuplicateFindingNamingEveryFile()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("a.module-definition.json", "demo", "1.2.6"),
                Library("b.module-definition.json", "demo", "1.2.6"),
                Library("sub/c.module-definition.json", "DEMO", "1.2.6"),
            ],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Duplicate, finding.Problem);
        Assert.Equal("a.module-definition.json", finding.FileName);
        Assert.Equal("b.module-definition.json, sub/c.module-definition.json", finding.OtherFileName);
    }

    [Fact]
    public void ABlankModuleKeyWithoutAReadErrorIsUnreadableAndNamedByItsFile()
    {
        // A file that parses but carries no identity must not read as a
        // passing check; the file name is the only name left to report.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [new PackageLibraryDefinition("empty.module-definition.json", ModuleKey: "", DefinitionVersion: "1.2.3")],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Unreadable, finding.Problem);
        Assert.True(finding.Blocks);
        Assert.Equal("empty.module-definition.json", finding.ModuleKey);
    }

    [Fact]
    public void TheReportCountsDuplicatesAmongTheBlockers()
    {
        var report = new PackageLibraryDefinitionGateReport(
            2,
            PackageLibraryDefinitionGate.Evaluate(
                [
                    Library("demo-old.module-definition.json", "demo", "1.2.6"),
                    Library("demo.module-definition.json", "demo", "1.2.6"),
                ],
                [Source("demo", "1.2.6")]));

        Assert.Equal(1, report.DuplicateCount);
        Assert.Equal(1, report.BlockingCount);
    }

    [Fact]
    public void AnUnreadableLibraryFileIsBlocking()
    {
        // A file whose identity cannot be verified must not read as a passing
        // check; it ships in every universal package unverified.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [UnreadableLibrary("broken.module-definition.json")],
            [Source("demo", "1.2.6")]);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.Unreadable, finding.Problem);
        Assert.True(finding.Blocks);
    }

    [Fact]
    public void ModuleKeysAreMatchedCaseInsensitively()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "DEMO", "1.2.6")],
            [Source("demo", "1.2.6")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void TheDescriptionNamesEveryFindingAndEndsWithACountLine()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("demo.module-definition.json", "demo", "1.2.3"),
                Library("orphan.module-definition.json", "orphan", "1.0.0"),
                UnreadableLibrary("broken.module-definition.json"),
            ],
            [Source("demo", "1.2.6")]);

        var lines = PackageLibraryDefinitionGate.DescribeFindings(findings, checkedCount: 3);

        Assert.Contains(lines, line => line.Contains("STALE", StringComparison.Ordinal) && line.Contains("demo", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("ORPHAN", StringComparison.Ordinal) && line.Contains("orphan", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("BROKEN", StringComparison.Ordinal) && line.Contains("broken.module-definition.json", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("3 file(s) checked", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 stale, 1 orphaned, 1 unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOrphanDescriptionSaysWhatToDoAboutIt()
    {
        // A refusal or warning that does not say what to do next is one the
        // operator has to reverse-engineer.
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("demo.module-definition.json", "demo", "1.2.3")],
            []);

        var lines = PackageLibraryDefinitionGate.DescribeFindings(findings, checkedCount: 1);

        Assert.Contains(
            lines,
            line => line.Contains("developerSource.sourceRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIncompleteManifestEntryIsBrokenAndBlocksInsteadOfOrphaningItsLibraryFile()
    {
        // The sync skips a moduleDefinitions entry without definitionVersion, so
        // the library copy is never updated. Before, the gate only saw that no
        // source owned the file and called it ORPHAN with advice to add a
        // repository that was already configured.
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "moduleDefinitions": [
                { "moduleKey": "demo", "definitionVersion": "", "path": "sql/demo.module-definition.json" },
                { "moduleKey": "other", "definitionVersion": "1.0.0", "path": "sql/other.module-definition.json" }
              ]
            }
            """);
        var incomplete = PackageLibraryDefinitionGate.FindIncompleteManifestEntries(manifest, "demo-repo");

        var findings = PackageLibraryDefinitionGate.Evaluate(
            [
                Library("demo.module-definition.json", "demo", "1.2.3"),
                Library("other.module-definition.json", "other", "1.0.0"),
            ],
            [Source("other", "1.0.0")],
            incomplete);

        var finding = Assert.Single(findings);
        Assert.Equal(PackageLibraryDefinitionProblem.IncompleteSource, finding.Problem);
        Assert.True(finding.Blocks);
        Assert.Equal("demo", finding.ModuleKey);
        Assert.Equal("demo-repo", finding.RepositoryKey);

        var lines = PackageLibraryDefinitionGate.DescribeFindings(findings, checkedCount: 2);
        Assert.Contains(
            lines,
            line => line.Contains("BROKEN", StringComparison.Ordinal)
                && line.Contains("demo-repo omp-components.json moduleDefinitions[0]", StringComparison.Ordinal)
                && line.Contains("definitionVersion is empty", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("ORPHAN", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 incomplete source entry", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEmptyRequiredFieldOfAManifestEntryIsNamed()
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "moduleDefinitions": [
                { "moduleKey": "  ", "definitionVersion": "1.0.0" },
                "not-an-object",
                { "moduleKey": "ok", "definitionVersion": "1.0.0", "path": "ok.json" }
              ]
            }
            """);

        var entries = PackageLibraryDefinitionGate.FindIncompleteManifestEntries(manifest, "demo-repo");

        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].Index);
        Assert.Equal("moduleKey, path", entries[0].MissingFields);
        Assert.Equal(1, entries[1].Index);
        Assert.Equal("moduleKey, definitionVersion, path", entries[1].MissingFields);
        Assert.Empty(PackageLibraryDefinitionGate.FindIncompleteManifestEntries(
            System.Text.Json.Nodes.JsonNode.Parse("""{ "components": [] }"""),
            "demo-repo"));
    }

    [Fact]
    public void AnIncompleteEntryWithoutAModuleKeyStillBlocks()
    {
        var findings = PackageLibraryDefinitionGate.Evaluate(
            [Library("orphan.module-definition.json", "orphan", "1.0.0")],
            [],
            [new PackageLibraryIncompleteSourceEntry("demo-repo", 3, "", "moduleKey")]);

        var report = new PackageLibraryDefinitionGateReport(1, findings);

        Assert.Equal(1, report.IncompleteSourceCount);
        Assert.Equal(1, report.OrphanCount);
        Assert.Equal(1, report.BlockingCount);
    }

    [Fact]
    public void SkippedConfiguredSourceRootsAreNamedWithTheirReason()
    {
        // Before, a configured root without omp-components.json was dropped
        // silently and the modules it owns showed up as unexplained orphans.
        var existing = Path.GetFullPath(Path.Join(Path.GetTempPath(), "omp-gate-existing"));
        var noManifest = Path.GetFullPath(Path.Join(Path.GetTempPath(), "omp-gate-no-manifest"));
        var missing = Path.GetFullPath(Path.Join(Path.GetTempPath(), "omp-gate-missing"));

        var skipped = PackageLibraryDefinitionGate.FindSkippedSourceRoots(
            [existing, noManifest, missing, noManifest + Path.DirectorySeparatorChar, existing.ToUpperInvariant()],
            [existing],
            directoryExists: root => !string.Equals(root, missing, StringComparison.OrdinalIgnoreCase),
            hasManifest: root => string.Equals(root, existing, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, skipped.Count);
        Assert.Equal(noManifest, skipped[0].Path);
        Assert.Equal("no omp-components.json", skipped[0].Reason);
        Assert.Equal(missing, skipped[1].Path);
        Assert.Equal("folder not found", skipped[1].Reason);

        var lines = new PackageLibraryDefinitionGateReport(0, [], skipped).Describe();
        Assert.Contains(lines, line => line.Contains("SKIPPED source root " + noManifest, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("2 configured source root(s) skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanLibraryReportsZeroFindings()
    {
        var lines = PackageLibraryDefinitionGate.DescribeFindings([], checkedCount: 12);

        var summary = Assert.Single(lines);
        Assert.Contains("0 stale, 0 orphaned, 0 unreadable", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportCountsBlockersSeparatelyFromOrphans()
    {
        var report = new PackageLibraryDefinitionGateReport(
            3,
            PackageLibraryDefinitionGate.Evaluate(
                [
                    Library("demo.module-definition.json", "demo", "1.2.3"),
                    Library("orphan.module-definition.json", "orphan", "1.0.0"),
                    UnreadableLibrary("broken.module-definition.json"),
                ],
                [Source("demo", "1.2.6")]));

        Assert.Equal(3, report.CheckedCount);
        Assert.Equal(1, report.StaleCount);
        Assert.Equal(1, report.OrphanCount);
        Assert.Equal(1, report.UnreadableCount);
        Assert.Equal(2, report.BlockingCount);
    }
}
