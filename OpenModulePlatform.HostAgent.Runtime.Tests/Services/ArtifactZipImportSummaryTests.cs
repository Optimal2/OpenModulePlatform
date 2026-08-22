using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Covers the three import outcomes the package-level summary must keep apart:
/// IMPORTED (new version), SKIPPED (identical content under the same version -- a
/// normal rebuild, never a failure) and CONFLICT (different content under the same
/// version -- the real "forgot to bump" error). Classification is structural
/// (IsIdenticalSkip / IsVersionConflict), not message-text matching.
/// </summary>
public sealed class ArtifactZipImportSummaryTests
{
    private static ArtifactZipImportService.UniversalHostAgentImportItemResult Item(
        string status,
        string? message = null,
        bool isIdenticalSkip = false,
        bool isVersionConflict = false)
        => new("artifact-package", "artifacts/some-item.zip", status, message)
        {
            IsIdenticalSkip = isIdenticalSkip,
            IsVersionConflict = isVersionConflict
        };

    private static ArtifactZipImportService.UniversalHostAgentImportResult Result(
        params ArtifactZipImportService.UniversalHostAgentImportItemResult[] items)
        => new("omp-universal", "2026.08.22", items);

    [Fact]
    public void IdenticalSkip_CountsAsSkippedIdentical_NotAsFailure()
    {
        var result = Result(
            Item("Imported"),
            Item("Skipped", "The same artifact identity and content already exists.", isIdenticalSkip: true),
            Item("Skipped", "The module definition does not allow artifacts for this app."));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(1, result.SkippedIdenticalCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ConflictCount);
    }

    [Fact]
    public void VersionConflict_CountsAsConflictAndFailure()
    {
        var conflict = ArtifactZipImportService.BuildVersionConflictMessage(
            "omp_portal", "0.3.568", "web-app", "omp-portal", "aaaa", "bbbb");
        var result = Result(
            Item("Imported"),
            Item("Failed", conflict, isVersionConflict: true));

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(0, result.SkippedIdenticalCount);
    }

    [Fact]
    public void Summary_SeparatesImportedSkippedIdenticalAndConflicts()
    {
        var items = Enumerable.Range(0, 33).Select(_ => Item("Imported"))
            .Concat(Enumerable.Range(0, 12).Select(_ =>
                Item("Skipped", "The same artifact identity and content already exists.", isIdenticalSkip: true)))
            .ToArray();
        var result = Result(items);

        Assert.Equal(
            "45 items: 33 imported, 12 skipped (identical), 0 conflict(s).",
            ArtifactZipImportService.BuildPackageImportSummary(result));
    }

    [Fact]
    public void Summary_ReportsConflictsAndOtherFailuresSeparately()
    {
        var conflict = ArtifactZipImportService.BuildVersionConflictMessage(
            "omp_portal", "0.3.568", "web-app", "omp-portal", "aaaa", "bbbb");
        var result = Result(
            Item("Imported"),
            Item("Failed", conflict, isVersionConflict: true),
            Item("Failed", "The artifact zip is malformed."));

        Assert.Equal(
            "3 items: 1 imported, 0 skipped (identical), 1 conflict(s), 1 other failure(s).",
            ArtifactZipImportService.BuildPackageImportSummary(result));
    }

    [Fact]
    public void VersionConflictMessage_NamesBothHashesAndDemandsBump()
    {
        var message = ArtifactZipImportService.BuildVersionConflictMessage(
            "omp_portal", "0.3.568", "web-app", "omp-portal", "aaaa", "bbbb");

        Assert.StartsWith("The artifact content has changed under the same version.", message);
        Assert.Contains("omp_portal 0.3.568 (web-app, omp-portal)", message);
        Assert.Contains("Existing SHA-256: aaaa.", message);
        Assert.Contains("Incoming SHA-256: bbbb.", message);
        Assert.Contains("Bump the component version", message);
    }

    [Fact]
    public void VersionConflictMessage_MissingExistingHashIsCalledOut()
    {
        var message = ArtifactZipImportService.BuildVersionConflictMessage(
            "omp_portal", "0.3.568", "web-app", "omp-portal", null, "bbbb");

        Assert.Contains("Existing SHA-256: <none stored>.", message);
    }

    [Fact]
    public void VersionConflictException_IsAnExpectedImportFailure_AndCarriesHashes()
    {
        var ex = new ArtifactVersionConflictException("conflict", "aaaa", "bbbb");

        // IsExpectedImportFailure covers InvalidOperationException; the subtype must
        // not escape that classification, or a conflict would take the unexpected-
        // error path instead of the per-item failure path.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("aaaa", ex.ExistingSha256);
        Assert.Equal("bbbb", ex.IncomingSha256);
    }
}
