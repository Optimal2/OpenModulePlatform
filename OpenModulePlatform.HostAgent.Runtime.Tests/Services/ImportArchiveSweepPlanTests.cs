using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// R12-F13. Import archive retention was time-only, which does not bound a store whose
/// volume is driven by refresh cadence rather than by age. These fix the policy of the
/// size cap that now runs alongside the age cutoff.
/// </summary>
public sealed class ImportArchiveSweepPlanTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    private const long OneGigabyte = 1024L * 1024 * 1024;

    private static ArtifactZipImportService.ImportArchiveEntry Entry(
        string name,
        long lengthBytes,
        int ageInDays,
        int ageInSeconds = 0)
        => new(
            $@"C:\OMP\ArtifactImports\processed\{name}",
            lengthBytes,
            Now.AddDays(-ageInDays).AddSeconds(-ageInSeconds));

    [Fact]
    public void EmptyArchive_PlansNothing()
    {
        var plan = ArtifactZipImportService.PlanImportArchiveSweep([], Now.AddDays(-30), 4 * OneGigabyte);

        Assert.Empty(plan.AgedOut);
        Assert.Empty(plan.OverSizeCap);
        Assert.Equal(0, plan.TotalBytesAfter);
    }

    [Fact]
    public void AgeCutoff_RemovesOnlyEntriesOlderThanTheCutoff()
    {
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("old-a.zip", 100, ageInDays: 40),
            Entry("old-b.zip", 100, ageInDays: 31),
            Entry("fresh.zip", 100, ageInDays: 2),
            Entry("newest.zip", 100, ageInDays: 0)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), maxTotalBytes: 0);

        Assert.Equal(["old-a.zip", "old-b.zip"], plan.AgedOut.Select(e => Path.GetFileName(e.Path)));
        Assert.Empty(plan.OverSizeCap);
        Assert.Equal(200, plan.TotalBytesAfter);
    }

    [Fact]
    public void SizeCapZero_LeavesTheSizeUnbounded()
    {
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("a.zip", 5 * OneGigabyte, ageInDays: 3),
            Entry("b.zip", 5 * OneGigabyte, ageInDays: 1)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), maxTotalBytes: 0);

        Assert.Empty(plan.AgedOut);
        Assert.Empty(plan.OverSizeCap);
        Assert.Equal(10 * OneGigabyte, plan.TotalBytesAfter);
    }

    [Fact]
    public void SizeCap_DeletesOldestFirstUntilTheArchiveFits()
    {
        // All four are inside the age window, so only the cap can remove anything -- which is
        // the whole point of R12-F13: time alone never touched these.
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("d1.zip", OneGigabyte, ageInDays: 4),
            Entry("d2.zip", OneGigabyte, ageInDays: 3),
            Entry("d3.zip", OneGigabyte, ageInDays: 2),
            Entry("d4.zip", OneGigabyte, ageInDays: 1)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), 2 * OneGigabyte);

        Assert.Empty(plan.AgedOut);
        Assert.Equal(["d1.zip", "d2.zip"], plan.OverSizeCap.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(2 * OneGigabyte, plan.TotalBytesAfter);
    }

    [Fact]
    public void SizeCap_KeepsTheNewestArchiveEvenWhenItAloneExceedsTheCap()
    {
        // The intended pass-through, tested explicitly (metod 4.5): a cap smaller than one
        // universal package must not empty the archive of the very file an operator opens
        // after a failed refresh.
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("older.zip", OneGigabyte, ageInDays: 2),
            Entry("newest.zip", 5 * OneGigabyte, ageInDays: 0)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), OneGigabyte / 2);

        Assert.Equal(["older.zip"], plan.OverSizeCap.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(5 * OneGigabyte, plan.TotalBytesAfter);
    }

    [Fact]
    public void AgeCutoff_KeepsTheNewestArchiveEvenWhenEverythingIsPastTheCutoff()
    {
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("a.zip", 100, ageInDays: 90),
            Entry("b.zip", 100, ageInDays: 60),
            Entry("c.zip", 100, ageInDays: 45)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), maxTotalBytes: 0);

        Assert.Equal(["a.zip", "b.zip"], plan.AgedOut.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(100, plan.TotalBytesAfter);
    }

    [Fact]
    public void NewestPackageIsProtectedTogetherWithItsErrorSidecar()
    {
        // MoveImportFile writes the reason sidecar after moving the package, so the sidecar is
        // always the newer of the two. Protecting "the newest entry" naively would have kept
        // a 200-byte .error.txt and deleted the failed package it explains.
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("old.zip", OneGigabyte, ageInDays: 5),
            Entry("newest.zip", OneGigabyte, ageInDays: 0, ageInSeconds: 10),
            Entry("newest.zip.error.txt", 200, ageInDays: 0)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), OneGigabyte / 2);

        var removed = plan.OverSizeCap.Select(e => Path.GetFileName(e.Path)).ToArray();
        Assert.Equal(["old.zip"], removed);
    }

    [Fact]
    public void AgeAndSizeAreReportedSeparatelyWhenBothBite()
    {
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("expired.zip", OneGigabyte, ageInDays: 40),
            Entry("kept-by-age.zip", OneGigabyte, ageInDays: 10),
            Entry("kept-by-age-2.zip", OneGigabyte, ageInDays: 5),
            Entry("newest.zip", OneGigabyte, ageInDays: 0)
        ];

        var plan = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), 2 * OneGigabyte);

        Assert.Equal(["expired.zip"], plan.AgedOut.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(["kept-by-age.zip"], plan.OverSizeCap.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(4 * OneGigabyte, plan.TotalBytesBefore);
        Assert.Equal(2 * OneGigabyte, plan.TotalBytesAfter);
    }

    [Fact]
    public void EntriesSharingATimestampProduceADeterministicPlan()
    {
        ArtifactZipImportService.ImportArchiveEntry[] entries =
        [
            Entry("b.zip", OneGigabyte, ageInDays: 3),
            Entry("a.zip", OneGigabyte, ageInDays: 3),
            Entry("newest.zip", OneGigabyte, ageInDays: 0)
        ];

        var first = ArtifactZipImportService.PlanImportArchiveSweep(entries, null, 2 * OneGigabyte);
        var second = ArtifactZipImportService.PlanImportArchiveSweep(entries.Reverse().ToArray(), null, 2 * OneGigabyte);

        Assert.Equal(["a.zip"], first.OverSizeCap.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(
            first.OverSizeCap.Select(e => e.Path),
            second.OverSizeCap.Select(e => e.Path));
    }

    [Fact]
    public void MeasuredLinusLaptopArchiveIsBroughtUnderTheDefaultCap()
    {
        // Before/after simulation on the real numbers (metod 7): measured 2026-08-16, the
        // processed archive held 66 files / 7,10 GB after 16 days -- every one of them inside
        // the 30-day window, so the shipping code deleted nothing and the archive was heading
        // for the ~18 GB the board projected. Against the 4 GB default the sweep now removes
        // the oldest until it fits, and keeps the newest.
        const long AverageBytes = 7_623_000_000L / 66;
        var entries = Enumerable.Range(0, 66)
            .Select(index => Entry($"import-{index:D2}.zip", AverageBytes, ageInDays: 16 - (index / 5)))
            .ToArray();

        var timeOnly = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), maxTotalBytes: 0);
        Assert.Empty(timeOnly.AgedOut);
        Assert.Empty(timeOnly.OverSizeCap);

        var capped = ArtifactZipImportService.PlanImportArchiveSweep(entries, Now.AddDays(-30), 4 * OneGigabyte);
        Assert.NotEmpty(capped.OverSizeCap);
        Assert.True(capped.TotalBytesAfter <= 4 * OneGigabyte);
        Assert.True(capped.TotalBytesAfter > 3 * OneGigabyte, "the sweep must stop at the cap, not empty the archive");
        Assert.DoesNotContain(
            "import-65.zip",
            capped.OverSizeCap.Select(e => Path.GetFileName(e.Path)));
    }
}
