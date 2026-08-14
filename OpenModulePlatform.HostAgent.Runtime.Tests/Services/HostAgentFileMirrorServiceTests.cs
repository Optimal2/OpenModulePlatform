using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostAgentFileMirrorServiceTests
{
    [Fact]
    public async Task MirrorConfiguredFilesAsync_ContentFiles_CopiesUpdatesAndDeletesStaleTargets()
    {
        var testRoot = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"omp-content-mirror-tests-{Guid.NewGuid():N}"));
        var reportsSource = Path.Join(testRoot, "source", "ContentReports");
        var pagesSource = Path.Join(testRoot, "source", "ContentPages");
        var reportsTarget = Path.Join(testRoot, "target", "ContentReports");
        var pagesTarget = Path.Join(testRoot, "target", "ContentPages");

        try
        {
            Directory.CreateDirectory(reportsSource);
            Directory.CreateDirectory(pagesSource);
            Directory.CreateDirectory(reportsTarget);
            Directory.CreateDirectory(pagesTarget);

            await File.WriteAllTextAsync(
                Path.Join(reportsSource, "content-test-status.json"),
                """{"title":"first","queries":[{"sql":"select 1"}]}""");
            await File.WriteAllTextAsync(
                Path.Join(pagesSource, "content-test-file.html"),
                "<h1>first</h1>");
            await File.WriteAllTextAsync(
                Path.Join(reportsTarget, "stale-report.json"),
                "{}");
            await File.WriteAllTextAsync(
                Path.Join(pagesTarget, "stale-page.html"),
                "<p>stale</p>");

            var options = new FakeOptionsMonitor<HostAgentSettings>
            {
                CurrentValue = new HostAgentSettings
                {
                    FileMirrors =
                    [
                        CreateMirror(reportsSource, reportsTarget),
                        CreateMirror(pagesSource, pagesTarget)
                    ]
                }
            };
            var service = new HostAgentFileMirrorService(
                options,
                NullLogger<HostAgentFileMirrorService>.Instance);

            await service.MirrorConfiguredFilesAsync(CancellationToken.None);

            Assert.Equal(
                """{"title":"first","queries":[{"sql":"select 1"}]}""",
                await File.ReadAllTextAsync(Path.Join(reportsTarget, "content-test-status.json")));
            Assert.Equal(
                "<h1>first</h1>",
                await File.ReadAllTextAsync(Path.Join(pagesTarget, "content-test-file.html")));
            Assert.False(File.Exists(Path.Join(reportsTarget, "stale-report.json")));
            Assert.False(File.Exists(Path.Join(pagesTarget, "stale-page.html")));

            await File.WriteAllTextAsync(
                Path.Join(reportsSource, "content-test-status.json"),
                """{"title":"second","queries":[{"sql":"select 2"}]}""");
            await File.WriteAllTextAsync(
                Path.Join(pagesSource, "content-test-file.html"),
                "<h1>second</h1>");

            await service.MirrorConfiguredFilesAsync(CancellationToken.None);

            Assert.Contains(
                "second",
                await File.ReadAllTextAsync(Path.Join(reportsTarget, "content-test-status.json")));
            Assert.Equal(
                "<h1>second</h1>",
                await File.ReadAllTextAsync(Path.Join(pagesTarget, "content-test-file.html")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// A missing source is skipped, and the mirrors after it still run.
    /// </summary>
    /// <remarks>
    /// This used to assert that a missing source threw. Mirroring runs before host job
    /// processing and telemetry in the convergence cycle, so throwing meant a source on a
    /// briefly unreachable UNC share stopped jobs and resource collection on every tick
    /// (R7-D10). The healthy mirror is deliberately ordered *after* the broken one: the
    /// point is not just that the call returns, but that the failure does not consume the
    /// rest of the list.
    /// </remarks>
    [Fact]
    public async Task MirrorConfiguredFilesAsync_MissingSource_SkipsMirrorAndContinues()
    {
        var testRoot = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"omp-content-mirror-tests-{Guid.NewGuid():N}"));
        var missingSource = Path.Join(testRoot, "missing");
        var missingTarget = Path.Join(testRoot, "missing-target");

        var healthySource = Path.Join(testRoot, "healthy");
        var healthyTarget = Path.Join(testRoot, "healthy-target");
        Directory.CreateDirectory(healthySource);
        await File.WriteAllTextAsync(Path.Join(healthySource, "mirrored.txt"), "content");

        try
        {
            var options = new FakeOptionsMonitor<HostAgentSettings>
            {
                CurrentValue = new HostAgentSettings
                {
                    FileMirrors =
                    [
                        CreateMirror(missingSource, missingTarget),
                        CreateMirror(healthySource, healthyTarget)
                    ]
                }
            };
            var service = new HostAgentFileMirrorService(
                options,
                NullLogger<HostAgentFileMirrorService>.Instance);

            await service.MirrorConfiguredFilesAsync(CancellationToken.None);

            Assert.False(Directory.Exists(missingTarget));
            Assert.True(File.Exists(Path.Join(healthyTarget, "mirrored.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static HostAgentFileMirrorSettings CreateMirror(string source, string target)
        => new()
        {
            SourcePath = source,
            TargetPath = target,
            DeleteStaleTargetEntries = true
        };
}
