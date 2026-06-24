using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ImportFileArchiveDestinationTests
{
    [Fact]
    public void CreateUniquePath_UsesTimestampedImportFileName_WhenDestinationIsAvailable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var utcNow = new DateTime(2026, 6, 24, 10, 11, 12, 345, DateTimeKind.Utc);

            var destination = ImportFileArchiveDestination.CreateUniquePath(
                root,
                Path.Join(root, "package.zip"),
                utcNow);

            Assert.Equal(Path.Join(root, "20260624-101112-345-package.zip"), destination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateUniquePath_SkipsArchiveAndErrorSidecarCollisions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var utcNow = new DateTime(2026, 6, 24, 10, 11, 12, 345, DateTimeKind.Utc);
            File.WriteAllText(Path.Join(root, "20260624-101112-345-package.zip"), string.Empty);
            File.WriteAllText(Path.Join(root, "20260624-101112-345-01-package.zip.error.txt"), string.Empty);

            var destination = ImportFileArchiveDestination.CreateUniquePath(
                root,
                Path.Join(root, "package.zip"),
                utcNow);

            Assert.Equal(Path.Join(root, "20260624-101112-345-02-package.zip"), destination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), "omp-import-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
