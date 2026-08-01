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

    [Fact]
    public async Task MirrorConfiguredFilesAsync_MissingSource_Throws()
    {
        var testRoot = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"omp-content-mirror-tests-{Guid.NewGuid():N}"));
        var missingSource = Path.Join(testRoot, "missing");
        var target = Path.Join(testRoot, "target");
        var options = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = new HostAgentSettings
            {
                FileMirrors = [CreateMirror(missingSource, target)]
            }
        };
        var service = new HostAgentFileMirrorService(
            options,
            NullLogger<HostAgentFileMirrorService>.Instance);

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.MirrorConfiguredFilesAsync(CancellationToken.None));

        Assert.Contains(missingSource, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HostAgentFileMirrorSettings CreateMirror(string source, string target)
        => new()
        {
            SourcePath = source,
            TargetPath = target,
            DeleteStaleTargetEntries = true
        };
}
