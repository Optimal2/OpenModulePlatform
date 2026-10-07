using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The maintenance quarantine: a cleanup moves instead of deleting, the scan and the
/// cleanup leave the quarantine alone, and the sweep bounds it by age and size.
/// </summary>
public sealed class MaintenanceQuarantineTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResolveRoot_DefaultsBelowServicesRoot()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);

        var resolved = MaintenanceQuarantine.ResolveRoot(settings);

        Assert.Equal(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName), resolved);
    }

    [Fact]
    public void ResolveRoot_StillResolvesWhenDisabled_SoTheGuardsKeepProtectingIt()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        settings.MaintenanceQuarantine.IsEnabled = false;

        Assert.Equal(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName), MaintenanceQuarantine.ResolveRoot(settings));
    }

    [Fact]
    public void Settings_RejectAPathNamedLikeAHostAgentInstallDirectory()
    {
        var settings = new HostAgentMaintenanceQuarantineSettings { Path = Path.Join(Path.GetTempPath(), "HostAgent-Quarantine") };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Settings_RejectARetentionBeyondTenYears()
    {
        var settings = new HostAgentMaintenanceQuarantineSettings { RetentionDays = HostAgentMaintenanceQuarantineSettings.MaxRetentionDays + 1 };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void BuildDestinationPath_SkipsAPlainFileAtTheDestinationName()
    {
        using var root = new TempDirectory();
        var quarantine = Directory.CreateDirectory(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName)).FullName;
        File.WriteAllText(Path.Join(quarantine, "OMP.X__20261007-120000__finding-3"), "stray");

        var destination = MaintenanceQuarantine.BuildDestinationPath(quarantine, Path.Join(root.Path, "OMP.X"), 3, Now);

        Assert.EndsWith("-2", destination, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRoots_HasOneDefaultBelowEachDistinctRoot()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        settings.SelfUpgrade.InstallRoot = Path.Join(root.Path, "Install");

        var roots = MaintenanceQuarantine.ResolveRoots(settings);

        Assert.Equal(
            new[] { Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName), Path.Join(root.Path, "Install", MaintenanceQuarantine.DefaultFolderName) },
            roots);
    }

    [Fact]
    public void ResolveRoots_DeduplicatesTheDefaultLayout()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        settings.SelfUpgrade.InstallRoot = root.Path;

        Assert.Single(MaintenanceQuarantine.ResolveRoots(settings));
    }

    [Fact]
    public void ResolveRootFor_PicksTheRootOnTheDirectorysVolume()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);

        var resolved = MaintenanceQuarantine.ResolveRootFor(settings, Path.Join(root.Path, "OMP.Orphan"));

        Assert.Equal(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName), resolved);
    }

    [Fact]
    public void Settings_DefaultsValidate_SoAHostWithoutTheSectionStarts()
    {
        new HostAgentMaintenanceQuarantineSettings().Validate();
    }

    [Fact]
    public void ResolveRoot_UsesConfiguredPath()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        var configured = Path.Join(root.Path, "Elsewhere", "quarantine");
        settings.MaintenanceQuarantine.Path = configured;

        Assert.Equal(configured, MaintenanceQuarantine.ResolveRoot(settings));
    }

    [Fact]
    public void IsQuarantinePath_CoversRootChildrenAndParents()
    {
        var quarantine = Path.Join(Path.GetTempPath(), "services", ".maintenance-quarantine");

        Assert.True(MaintenanceQuarantine.IsQuarantinePath(quarantine, quarantine));
        Assert.True(MaintenanceQuarantine.IsQuarantinePath(quarantine, Path.Join(quarantine, "OMP.Thing__20261007-120000__finding-7")));
        Assert.True(MaintenanceQuarantine.IsQuarantinePath(quarantine, Path.Join(Path.GetTempPath(), "services")));
        Assert.False(MaintenanceQuarantine.IsQuarantinePath(quarantine, Path.Join(Path.GetTempPath(), "services", "OMP.Thing")));
        Assert.False(MaintenanceQuarantine.IsQuarantinePath((string?)null, quarantine));
    }

    [Fact]
    public void TryMoveToQuarantine_MovesDirectoryAndWritesSidecar()
    {
        using var root = new TempDirectory();
        var orphan = Path.Join(root.Path, "OMP.Orphan");
        Directory.CreateDirectory(Path.Join(orphan, "bin"));
        File.WriteAllText(Path.Join(orphan, "bin", "app.dll"), "x");
        var quarantine = Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName);

        var moved = MaintenanceQuarantine.TryMoveToQuarantine(
            quarantine, orphan, 42, "Orphan service-app directory", Now, CancellationToken.None,
            out var destination, out var refusal, out var sidecarWarning);
        Assert.Null(sidecarWarning);

        Assert.True(moved, refusal);
        Assert.False(Directory.Exists(orphan));
        Assert.Equal(Path.Join(quarantine, "OMP.Orphan__20261007-120000__finding-42"), destination);
        Assert.True(File.Exists(Path.Join(destination, "bin", "app.dll")));
        var sidecar = File.ReadAllText(destination + MaintenanceQuarantine.ReasonSidecarSuffix);
        Assert.Contains(orphan, sidecar, StringComparison.Ordinal);
        Assert.Contains("finding: 42", sidecar, StringComparison.Ordinal);
        Assert.Equal(Now, Directory.GetLastWriteTimeUtc(destination));
    }

    [Fact]
    public void TryMoveToQuarantine_SuffixesASecondMoveOfTheSameName()
    {
        using var root = new TempDirectory();
        var quarantine = Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName);
        var first = Directory.CreateDirectory(Path.Join(root.Path, "OMP.Twin")).FullName;
        Assert.True(MaintenanceQuarantine.TryMoveToQuarantine(quarantine, first, 1, "r", Now, CancellationToken.None, out var firstDestination, out _, out _));
        var second = Directory.CreateDirectory(Path.Join(root.Path, "OMP.Twin")).FullName;

        Assert.True(MaintenanceQuarantine.TryMoveToQuarantine(quarantine, second, 1, "r", Now, CancellationToken.None, out var secondDestination, out _, out _));

        Assert.NotEqual(firstDestination, secondDestination);
        Assert.EndsWith("-2", secondDestination, StringComparison.Ordinal);
        Assert.True(Directory.Exists(firstDestination));
        Assert.True(Directory.Exists(secondDestination));
    }

    [Fact]
    public void TryMoveToQuarantine_RefusesAnotherVolume()
    {
        using var root = new TempDirectory();
        var orphan = Directory.CreateDirectory(Path.Join(root.Path, "OMP.Orphan")).FullName;
        var sourceVolume = Path.GetPathRoot(orphan)!;
        // A drive letter that is not the source's; the move must be refused before it is tried.
        var otherVolume = sourceVolume.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ? @"R:\" : @"Q:\";
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var moved = MaintenanceQuarantine.TryMoveToQuarantine(
            Path.Join(otherVolume, "quarantine"), orphan, 5, "r", Now, CancellationToken.None,
            out _, out var refusal, out _);

        Assert.False(moved);
        Assert.Contains("another volume", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_RemovesAgedOutEntriesButKeepsTheNewestAndItsSidecar()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        settings.MaintenanceQuarantine.RetentionDays = 30;
        settings.MaintenanceQuarantine.RetentionMaxBytes = 0;
        var quarantine = Directory.CreateDirectory(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName)).FullName;
        var old = CreateQuarantined(quarantine, "Old", Now.AddDays(-40));
        var newest = CreateQuarantined(quarantine, "Newest", Now.AddDays(-35));

        MaintenanceQuarantine.Sweep(settings, NullLogger.Instance, Now);

        Assert.False(Directory.Exists(old));
        Assert.False(File.Exists(old + MaintenanceQuarantine.ReasonSidecarSuffix));
        Assert.True(Directory.Exists(newest));
        Assert.True(File.Exists(newest + MaintenanceQuarantine.ReasonSidecarSuffix));
    }

    [Fact]
    public void Sweep_RemovesOldestUntilUnderTheSizeCap()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);
        settings.MaintenanceQuarantine.RetentionDays = 0;
        settings.MaintenanceQuarantine.RetentionMaxBytes = 1024L * 1024;
        var quarantine = Directory.CreateDirectory(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName)).FullName;
        var oldest = CreateQuarantined(quarantine, "A", Now.AddDays(-3), payloadBytes: 700 * 1024);
        var middle = CreateQuarantined(quarantine, "B", Now.AddDays(-2), payloadBytes: 700 * 1024);
        var newest = CreateQuarantined(quarantine, "C", Now.AddDays(-1), payloadBytes: 700 * 1024);

        MaintenanceQuarantine.Sweep(settings, NullLogger.Instance, Now);

        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void Sweep_DoesNotCreateTheQuarantineFolder()
    {
        using var root = new TempDirectory();
        var settings = CreateSettings(root.Path);

        MaintenanceQuarantine.Sweep(settings, NullLogger.Instance, Now);

        Assert.False(Directory.Exists(Path.Join(root.Path, MaintenanceQuarantine.DefaultFolderName)));
    }

    [Fact]
    public void Settings_RejectAQuarantineWithoutRetention()
    {
        var settings = new HostAgentMaintenanceQuarantineSettings { RetentionDays = 0, RetentionMaxBytes = 0 };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Settings_RejectARelativePath()
    {
        var settings = new HostAgentMaintenanceQuarantineSettings { Path = "quarantine" };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    private static string CreateQuarantined(string quarantine, string name, DateTime stampUtc, int payloadBytes = 16)
    {
        var directory = Path.Join(quarantine, $"{name}__{stampUtc:yyyyMMdd-HHmmss}__finding-1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Join(directory, "payload.bin"), new byte[payloadBytes]);
        File.WriteAllText(directory + MaintenanceQuarantine.ReasonSidecarSuffix, "reason");
        Directory.SetLastWriteTimeUtc(directory, stampUtc);
        File.SetLastWriteTimeUtc(directory + MaintenanceQuarantine.ReasonSidecarSuffix, stampUtc.AddSeconds(1));
        return directory;
    }

    private static HostAgentSettings CreateSettings(string servicesRoot)
        => new()
        {
            ServicesRoot = servicesRoot,
            ServiceName = "OMP.HostAgent"
        };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"omp-quarantine-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
