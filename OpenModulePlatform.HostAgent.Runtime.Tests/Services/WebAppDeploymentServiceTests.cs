using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class WebAppDeploymentServiceTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of temp test directories.
            }
        }
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenOverlaySkippedByVersionMinimum_AttachesWarningWithOverlayKeyAndVersions()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.VersionSkippedConfigOverlays.Add(
            new ConfigOverlayVersionSkip("auth-oidc", "1.2.0", "0.3.183"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.Result.State);
        Assert.Contains("auth-oidc", last.Result.DiagnosticWarningMessage, StringComparison.Ordinal);
        Assert.Contains("0.3.183", last.Result.DiagnosticWarningMessage, StringComparison.Ordinal);
        Assert.Contains(descriptor.Version, last.Result.DiagnosticWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ArtifactWithLiteralOmpDb_WritesLiveConnectionAndWarnsOncePerArtifactVersion()
    {
        const string packagedConnectionString = "Data Source=localhost;Initial Catalog=OpenModulePlatform;Integrated Security=true;";
        var logger = new CaptureLogger<WebAppDeploymentService>();
        var (service, repository, _) = CreateServiceWithFakeRepository(logger: logger);
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = descriptor.ArtifactId,
            RelativePath = "appsettings.json",
            FileContent = $$"""
            { "ConnectionStrings": { "OmpDb": {{JsonSerializer.Serialize(packagedConnectionString)}} } }
            """
        });

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var written = File.ReadAllText(Path.Join(descriptor.InstallPath!, "appsettings.json"));
        using var document = JsonDocument.Parse(written);
        Assert.Equal(
            repository.GetConfiguredConnectionString(),
            document.RootElement.GetProperty("ConnectionStrings").GetProperty("OmpDb").GetString());

        var warnings = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("ConnectionStrings:OmpDb", StringComparison.Ordinal))
            .ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(descriptor.ArtifactId.ToString(System.Globalization.CultureInfo.InvariantCulture), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(packagedConnectionString, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(repository.GetConfiguredConnectionString(), warning.Message, StringComparison.Ordinal);

        var nextVersion = CreateWebAppDeploymentDescriptor(out _, version: "1.0.1");
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(nextVersion);
        await service.DeployDesiredWebAppsAsync(nextVersion.HostKey, CancellationToken.None);
        await service.DeployDesiredWebAppsAsync(nextVersion.HostKey, CancellationToken.None);
        warnings = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("ConnectionStrings:OmpDb", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("1.0.1", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_KeepsHostOwnedAllowedHostsTheArtifactLeavesOut()
    {
        // Pins the call site: the deployment itself must carry the host's AllowedHosts
        // into the written file, not only the gate helper in isolation.
        var logger = new CaptureLogger<WebAppDeploymentService>();
        var (service, repository, _) = CreateServiceWithFakeRepository(logger: logger);
        var descriptor = CreateWebAppDeploymentDescriptor(out _, version: "1.0.1");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = descriptor.ArtifactId,
            RelativePath = "appsettings.json",
            FileContent = """{ "ConnectionStrings": { "OmpDb": "{{Omp.Json.ConnectionStrings.OmpDb}}" } }"""
        });
        var deployedPath = Path.Join(descriptor.InstallPath!, "appsettings.json");
        File.WriteAllText(deployedPath, """
            {
              // Restricted by the operator.
              "AllowedHosts": "localhost;127.0.0.1",
            }
            """);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.Equal(HostDeploymentStatuses.Succeeded, repository.PublishedWebAppResults.Last().Result.State);
        using var document = JsonDocument.Parse(File.ReadAllText(deployedPath));
        Assert.Equal("localhost;127.0.0.1", document.RootElement.GetProperty("AllowedHosts").GetString());
        var carried = Assert.Single(logger.Entries, entry => entry.Message.Contains("host-owned keys", StringComparison.Ordinal));
        Assert.Contains("AllowedHosts", carried.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", carried.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenDeployedAppSettingsIsNotValidJson_FailsAndLeavesFileUntouched()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, version: "1.0.1");
        repository.DesiredWebAppDeployments.Add(descriptor);
        var deployedPath = Path.Join(descriptor.InstallPath!, "appsettings.json");
        const string broken = """{ "AllowedHosts": "secret-host.example" """;
        File.WriteAllText(deployedPath, broken);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Failed, last.Result.State);
        Assert.Contains("not valid JSON", last.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", last.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(broken, File.ReadAllText(deployedPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenNoOverlaySkippedByVersion_LeavesDiagnosticWarningMessageClean()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenDeploySetWarningExists_AttachesWarningToDiagnosticWarningMessage()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey);
        repository.DesiredWebAppDeployments.Add(descriptor);

        var deploySetWarnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [moduleInstanceKey] = "Deploy-set inconsistency in set 'default': versions differ."
        };

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, deploySetWarnings, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.NotEmpty(published);
        var last = published.Last();
        Assert.Contains("Deploy-set inconsistency", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenNoDeploySetWarning_LeavesDiagnosticWarningMessageClean()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.NotEmpty(published);
        var last = published.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_SingleHostWithLocalDataProtectionKeyPath_SkipsUncPathAdvisory()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.WebAppDataProtectionKeyPath = @"C:\OMP\DataProtectionKeys";
        repository.EnabledHostCount = 1;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateAppSettingsWithDataProtectionKeyPath(descriptor.ArtifactId, @"C:\OMP\DataProtectionKeys"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_MultiHostWithLocalDataProtectionKeyPath_EmitsStructuredUncPathAdvisory()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.WebAppDataProtectionKeyPath = @"C:\OMP\DataProtectionKeys";
        repository.EnabledHostCount = 2;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateAppSettingsWithDataProtectionKeyPath(descriptor.ArtifactId, @"C:\OMP\DataProtectionKeys"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains(
            OmpAuthConfigurationWarning.DataProtectionKeyPathNotUncPathCode,
            last.Result.DiagnosticWarningMessage,
            StringComparison.Ordinal);
        // The stored warning is a single JSON line, so backslashes in the path are escaped.
        Assert.Contains(@"C:\\OMP\\DataProtectionKeys", last.Result.DiagnosticWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_MultiHostWithUncDataProtectionKeyPath_SkipsUncPathAdvisory()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.WebAppDataProtectionKeyPath = @"\\localhost\omp-dataprotection";
        repository.EnabledHostCount = 2;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateAppSettingsWithDataProtectionKeyPath(descriptor.ArtifactId, @"\\localhost\omp-dataprotection"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_SingleHostWithMismatchedDataProtectionKeyPath_KeepsMismatchWarningOnly()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.WebAppDataProtectionKeyPath = @"C:\OMP\DataProtectionKeys";
        repository.EnabledHostCount = 1;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateAppSettingsWithDataProtectionKeyPath(descriptor.ArtifactId, @"D:\Other\Keys"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains(
            OmpAuthConfigurationWarning.DataProtectionKeyPathMismatchCode,
            last.Result.DiagnosticWarningMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            OmpAuthConfigurationWarning.DataProtectionKeyPathNotUncPathCode,
            last.Result.DiagnosticWarningMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_TokenBasedDataProtectionKeyPath_RendersTokenBeforeComparison()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.WebAppDataProtectionKeyPath = @"C:\OMP\DataProtectionKeys";
        repository.EnabledHostCount = 1;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateAppSettingsWithDataProtectionKeyPath(descriptor.ArtifactId, "{{Omp.Json.HostAgent.WebAppDataProtectionKeyPath}}"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var last = repository.PublishedWebAppResults.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
        Assert.Equal(@"C:\OMP\DataProtectionKeys", last.Result.EffectiveOmpAuthDataProtectionKeyPath);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ContentSha256Unchanged_SkipsRedeployment()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        ApplyConfigurationForAlreadyAppliedDeployment(repository, settings, descriptor);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.Single(published);
        Assert.Equal(HostDeploymentStatuses.Succeeded, published[0].Result.State);
        Assert.False(published[0].Result.Applied);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ContentSha256Changed_SameArtifactIdAndVersion_TriggersRedeployment()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "bb22", deployedContentSha256: "aa11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        ApplyConfigurationForAlreadyAppliedDeployment(repository, settings, descriptor);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.Equal(2, published.Count);
        Assert.Equal(HostDeploymentStatuses.Running, published[0].Result.State);
        Assert.Equal(HostDeploymentStatuses.Succeeded, published[1].Result.State);
        Assert.True(published[1].Result.Applied);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_OverlayOwnedFileRemoved_WhenOverlayRemovedAndArtifactUnchanged()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        // First cycle: full deploy writes the overlay-owned file and records it.
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        Assert.True(File.Exists(overlayPath));

        // Overlay removed while the artifact and its content hash are unchanged: the
        // already-applied fast path must reconcile (remove) the recorded overlay file.
        repository.ArtifactConfigurationFiles.Clear();

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.False(File.Exists(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.Result.State);
        Assert.True(last.Result.Applied);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_OverlayContentEdit_ChangesDeployedContentOnNextCycle()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        const string relativePath = "odv.site.config.js";
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, "// overlay v1"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);
        Assert.Equal("// overlay v1", File.ReadAllText(overlayPath));

        // Overlay content edit with unchanged artifact identity: the already-applied
        // fast path must notice the desired bytes changed and re-apply on the next cycle.
        repository.ArtifactConfigurationFiles.Clear();
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, "// overlay v2"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.Equal("// overlay v2", File.ReadAllText(overlayPath));
        var last = repository.PublishedWebAppResults.Last().Result;
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.State);
        Assert.True(last.Applied);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_OverlayOwnedFile_ReplacedWhenArtifactNowOwnsThePath()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        const string relativePath = "odv.site.config.js";
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, "// overlay"));
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        Assert.Equal("// overlay", File.ReadAllText(overlayPath));

        // The artifact changes and now owns the same path; the overlay goes away. The path
        // is still desired, so it must be restored to the artifact's content, not removed.
        // Build a successor descriptor that keeps the same app instance/target but a new id.
        var nextSource = Path.Join(Path.GetDirectoryName(descriptor.InstallPath!)!, "source-v2");
        Directory.CreateDirectory(nextSource);
        File.WriteAllText(Path.Join(nextSource, "app.txt"), "content");
        var next = new WebAppDeploymentDescriptor
        {
            HostId = descriptor.HostId,
            HostKey = descriptor.HostKey,
            AppInstanceId = descriptor.AppInstanceId,
            AppInstanceKey = descriptor.AppInstanceKey,
            AppKey = descriptor.AppKey,
            ModuleInstanceKey = moduleInstanceKey,
            DisplayName = descriptor.DisplayName,
            ArtifactId = 2,
            Version = "1.0.1",
            SourceLocalPath = nextSource,
            ContentSha256 = "bb22",
            InstallPath = descriptor.InstallPath,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedArtifactId = 1,
            DeployedSourceLocalPath = descriptor.SourceLocalPath,
            DeployedTargetPath = descriptor.InstallPath,
            DeployedRuntimeName = "TestSite/",
            DeployedContentSha256 = "AA11"
        };
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(next);
        repository.ArtifactConfigurationFiles.Clear();
        repository.ArtifactConfigurationFiles.Add(new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 200,
            ArtifactId = 2,
            RelativePath = relativePath,
            FileContent = "// artifact v2"
        });

        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);
        Assert.Equal("// artifact v2", File.ReadAllText(overlayPath));

        // A subsequent already-applied cycle must not reconcile (remove) the now artifact-owned file.
        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);
        Assert.True(File.Exists(overlayPath));
        Assert.Equal("// artifact v2", File.ReadAllText(overlayPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_UnrelatedManualFile_IsPreservedDuringOverlayReconcile()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var manualPath = Path.Join(descriptor.InstallPath!, "operator-notes.txt");
        File.WriteAllText(manualPath, "operator");

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.False(File.Exists(Path.Join(descriptor.InstallPath!, "odv.site.config.js")));
        Assert.True(File.Exists(manualPath));
        Assert.Equal("operator", File.ReadAllText(manualPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ModifiedOverlayOwnedFile_IsLeftInPlaceAndWarns()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        File.WriteAllText(overlayPath, "// operator modified");

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(overlayPath));
        Assert.Equal("// operator modified", File.ReadAllText(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.Result.State);
        Assert.False(last.Result.Applied);
        Assert.Contains("modified", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_FullApply_RemovesOverlayOwnedExcludedFile_WhenOverlayRemoved()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        // appsettings.*.json is in the default mirror exclusions, so the generic mirror
        // never deletes this stale file; only the planned overlay removal can.
        const string relativePath = "appsettings.overlay.json";
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, """{ "Overlay": true }"""));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);
        Assert.True(File.Exists(overlayPath));

        // Overlay removed and a new artifact forces a full apply (not the fast path).
        repository.ArtifactConfigurationFiles.Clear();
        var next = CreateSuccessorDescriptor(descriptor, moduleInstanceKey);
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(next);

        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);

        Assert.False(File.Exists(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.Result.State);
        Assert.True(last.Result.Applied);
        var inventory = AppliedOverlayFileInventory.TryRead(settings.LocalArtifactCacheRoot, descriptor.InstallPath!, descriptor.AppInstanceId, out var diagnostic);
        Assert.Null(diagnostic);
        Assert.NotNull(inventory);
        Assert.Empty(inventory.Files);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_FullApply_RemovesOverlayOwnedFileUnderAppData_WhenOverlayRemoved()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        // App_Data is in the default mirror exclusions, so the generic mirror keeps stale files
        // there; only the planned removal reaches this path through the exclusion.
        const string relativePath = @"App_Data\feature-flag-overrides.json";
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, """{ "Feature": true }"""));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);
        Assert.True(File.Exists(overlayPath));

        repository.ArtifactConfigurationFiles.Clear();
        var next = CreateSuccessorDescriptor(descriptor, moduleInstanceKey);
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(next);

        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);

        Assert.False(File.Exists(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Equal(HostDeploymentStatuses.Succeeded, last.Result.State);
        Assert.True(last.Result.Applied);
        // The trusted inventory lives outside the web root; the removal must not disturb it.
        var inventory = AppliedOverlayFileInventory.TryRead(settings.LocalArtifactCacheRoot, descriptor.InstallPath!, descriptor.AppInstanceId, out var diagnostic);
        Assert.Null(diagnostic);
        Assert.NotNull(inventory);
        Assert.Empty(inventory.Files);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_FullApply_ModifiedOverlayOwnedExcludedFile_IsLeftAndOwnershipPreserved()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        const string relativePath = "appsettings.overlay.json";
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, """{ "Overlay": true }"""));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);
        File.WriteAllText(overlayPath, """{ "Overlay": "operator" }""");

        repository.ArtifactConfigurationFiles.Clear();
        var next = CreateSuccessorDescriptor(descriptor, moduleInstanceKey);
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(next);

        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);

        Assert.True(File.Exists(overlayPath));
        Assert.Equal("""{ "Overlay": "operator" }""", File.ReadAllText(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains("modified", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
        // Ownership survived the full apply, so a later cycle can still reconcile it.
        var inventory = AppliedOverlayFileInventory.TryRead(settings.LocalArtifactCacheRoot, descriptor.InstallPath!, descriptor.AppInstanceId, out _);
        Assert.NotNull(inventory);
        Assert.Contains(inventory.Files, file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_NullFilesInventory_FailsPerAppAndDoesNotAbortSweep()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var first = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        var second = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(first);
        repository.DesiredWebAppDeployments.Add(second);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(first.ArtifactId, "odv.site.config.js", "// overlay"));
        ApplyConfigurationForAlreadyAppliedDeployment(repository, CreateTestSettings(), first);

        await service.DeployDesiredWebAppsAsync(first.HostKey, CancellationToken.None);
        repository.PublishedWebAppResults.Clear();

        // Structurally valid JSON with an explicit null Files list in trusted state,
        // bound to the real app instance: a foreign id would fail the binding check before
        // the null list is ever enumerated, and the pre-fix sweep abort would not be exercised.
        var inventoryPath = AppliedOverlayFileInventory.GetTrustedPath(settings.LocalArtifactCacheRoot, first.InstallPath!, first.AppInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(inventoryPath)!);
        File.WriteAllText(inventoryPath, $$"""
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "AppInstanceId": "{{first.AppInstanceId}}", "TargetPath": {{System.Text.Json.JsonSerializer.Serialize(AppliedOverlayFileInventory.NormalizeTargetPath(first.InstallPath!))}}, "Files": null }
            """);

        await service.DeployDesiredWebAppsAsync(first.HostKey, CancellationToken.None);

        // Both deployments were processed: the malformed inventory failed per-app with a
        // warning instead of aborting the sweep with a NullReferenceException.
        Assert.True(repository.PublishedWebAppResults.Count >= 2);
        Assert.Contains("inventory", repository.PublishedWebAppResults[0].Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HostDeploymentStatuses.Succeeded, repository.PublishedWebAppResults.Last().Result.State);
    }

    private WebAppDeploymentDescriptor CreateSuccessorDescriptor(
        WebAppDeploymentDescriptor descriptor, string moduleInstanceKey)
    {
        var nextSource = Path.Join(Path.GetDirectoryName(descriptor.InstallPath!)!, "source-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nextSource);
        File.WriteAllText(Path.Join(nextSource, "app.txt"), "content");
        _tempPaths.Add(Path.GetDirectoryName(nextSource)!);
        return new WebAppDeploymentDescriptor
        {
            HostId = descriptor.HostId,
            HostKey = descriptor.HostKey,
            AppInstanceId = descriptor.AppInstanceId,
            AppInstanceKey = descriptor.AppInstanceKey,
            AppKey = descriptor.AppKey,
            ModuleInstanceKey = moduleInstanceKey,
            DisplayName = descriptor.DisplayName,
            ArtifactId = 2,
            Version = "1.0.1",
            SourceLocalPath = nextSource,
            ContentSha256 = "bb22",
            InstallPath = descriptor.InstallPath,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedArtifactId = 1,
            DeployedSourceLocalPath = descriptor.SourceLocalPath,
            DeployedTargetPath = descriptor.InstallPath,
            DeployedRuntimeName = "TestSite/",
            DeployedContentSha256 = "AA11"
        };
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ForeignInventory_IsNotUsedForRemoval()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        Assert.True(File.Exists(overlayPath));

        // Trusted state whose content is bound to a foreign app instance must not
        // authorize removal for this instance (binding mismatch is rejected on read).
        var inventoryPath = AppliedOverlayFileInventory.GetTrustedPath(settings.LocalArtifactCacheRoot, descriptor.InstallPath!, descriptor.AppInstanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(inventoryPath)!);
        File.WriteAllText(inventoryPath, $$"""
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "AppInstanceId": "{{Guid.NewGuid()}}", "TargetPath": {{System.Text.Json.JsonSerializer.Serialize(AppliedOverlayFileInventory.NormalizeTargetPath(descriptor.InstallPath!))}}, "Files": [] }
            """);

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.False(last.Result.Applied);
        Assert.Contains("inventory", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ForgedLegacyAppDataInventory_CannotDeleteUnrelatedFile()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var webConfigPath = Path.Join(descriptor.InstallPath!, "web.config");
        File.WriteAllText(webConfigPath, "<configuration />");

        // Forged but well-formed inventory in the app-pool-writable legacy location,
        // claiming ownership of web.config with its correct hash (F1). It must be treated
        // as UNKNOWN: no deletion authorized by it, ever.
        var legacyPath = AppliedOverlayFileInventory.GetPath(descriptor.InstallPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var webConfigHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(webConfigPath))).ToLowerInvariant();
        File.WriteAllText(legacyPath, $$"""
            { "SchemaName": "OpenModulePlatform.AppliedOverlayFiles.v1", "AppInstanceId": "{{descriptor.AppInstanceId}}", "Files": [ { "RelativePath": "web.config", "ContentLength": {{new FileInfo(webConfigPath).Length}}, "ContentSha256": "{{webConfigHash}}" } ] }
            """);

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(webConfigPath));
        Assert.Equal("<configuration />", File.ReadAllText(webConfigPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_PayloadCollisionFile_IsNeverRemovedOnFastPath()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        // The artifact payload itself ships this path; an overlay supplying the same path
        // must not make it overlay-owned (F2).
        File.WriteAllText(Path.Join(descriptor.SourceLocalPath, "payload.config.js"), "// payload");
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "payload.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var payloadPath = Path.Join(descriptor.InstallPath!, "payload.config.js");
        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        // Never deleted, and no longer left with overlay bytes: the actual current
        // artifact payload bytes are restored on the already-applied fast path (F1).
        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// payload", File.ReadAllText(payloadPath));
        Assert.True(repository.PublishedWebAppResults.Last().Result.Applied);

        // A harmless later poll does not redeploy or change the restored bytes.
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        Assert.False(repository.PublishedWebAppResults.Last().Result.Applied);
        Assert.Equal("// payload", File.ReadAllText(payloadPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ModifiedPayloadOverlaidFile_IsLeftInPlaceAndWarns()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        File.WriteAllText(Path.Join(descriptor.SourceLocalPath, "payload.config.js"), "// payload");
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "payload.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var payloadPath = Path.Join(descriptor.InstallPath!, "payload.config.js");
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));

        // Independently modified content is not HostAgent's overlay bytes any more:
        // restoration must not overwrite it.
        File.WriteAllText(payloadPath, "// operator modified");
        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// operator modified", File.ReadAllText(payloadPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.False(last.Result.Applied);
        Assert.Contains("modified", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not restored", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_PayloadOverlaidFile_UnknownPayloadInventoryLeavesFileAndWarns()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        File.WriteAllText(Path.Join(descriptor.SourceLocalPath, "payload.config.js"), "// payload");
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "payload.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var payloadPath = Path.Join(descriptor.InstallPath!, "payload.config.js");
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));

        // The payload source vanishes: a complete payload inventory can no longer prove
        // ownership, so neither deletion nor restoration is authorized.
        Directory.Delete(descriptor.SourceLocalPath, recursive: true);
        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.False(last.Result.Applied);
        Assert.Contains("payload", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
        var inventory = AppliedOverlayFileInventory.TryRead(settings.LocalArtifactCacheRoot, descriptor.InstallPath!, descriptor.AppInstanceId, out _);
        Assert.NotNull(inventory);
        Assert.Contains(inventory.Files, file => string.Equals(file.RelativePath, "payload.config.js", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("./payload.config.js")]
    [InlineData("payload.config.js")]
    public async Task DeployDesiredWebAppsAsync_PayloadAliasOverlay_RestoresPayloadBytesWhenRemoved(string overlayRelativePath)
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        File.WriteAllText(Path.Join(descriptor.SourceLocalPath, "payload.config.js"), "// payload");
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, overlayRelativePath, "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var payloadPath = Path.Join(descriptor.InstallPath!, "payload.config.js");
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// payload", File.ReadAllText(payloadPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_PayloadSubdirectoryAliasOverlay_RestoresPayloadBytesWhenRemoved()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        var sourceSubdirectory = Path.Join(descriptor.SourceLocalPath, "x");
        Directory.CreateDirectory(sourceSubdirectory);
        File.WriteAllText(Path.Join(sourceSubdirectory, "y"), "// payload");
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "x//y", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var payloadPath = Path.Join(descriptor.InstallPath!, "x", "y");
        Assert.Equal("// overlay", File.ReadAllText(payloadPath));

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(payloadPath));
        Assert.Equal("// payload", File.ReadAllText(payloadPath));
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_ReservedControlPathOverlay_IsNotApplied()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, @"App_Data\omp-deployment.lock.json", "{ \"forged\": true }"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var lockPath = Path.Join(descriptor.InstallPath!, "App_Data", "omp-deployment.lock.json");
        Assert.False(File.Exists(lockPath) && File.ReadAllText(lockPath).Contains("forged", StringComparison.Ordinal));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains("reserved", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("App_Data/./omp-deployment.lock.json")]
    [InlineData("App_Data//omp-deployment.lock.json")]
    [InlineData("./App_Data/omp-deployment.lock.json")]
    public async Task DeployDesiredWebAppsAsync_ReservedControlPathAlias_IsNotApplied(string reservedRelativePath)
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, reservedRelativePath, "{ \"forged\": true }"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        // Through the real deployment path the alias resolves to the real control file;
        // it must never carry the forged overlay content.
        var lockPath = Path.Join(descriptor.InstallPath!, "App_Data", "omp-deployment.lock.json");
        Assert.False(File.Exists(lockPath) && File.ReadAllText(lockPath).Contains("forged", StringComparison.Ordinal));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains("reserved", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_FullApply_ModifiedNonExcludedOverlayFile_IsLeftInPlace()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        const string relativePath = "odv.site.config.js"; // not in the mirror exclusion list (F5)
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, relativePath, "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, relativePath);
        File.WriteAllText(overlayPath, "// operator modified");

        repository.ArtifactConfigurationFiles.Clear();
        var next = CreateSuccessorDescriptor(descriptor, moduleInstanceKey);
        repository.DesiredWebAppDeployments.Clear();
        repository.DesiredWebAppDeployments.Add(next);

        await service.DeployDesiredWebAppsAsync(next.HostKey, CancellationToken.None);

        Assert.True(File.Exists(overlayPath));
        Assert.Equal("// operator modified", File.ReadAllText(overlayPath));
        var last = repository.PublishedWebAppResults.Last();
        Assert.Contains("modified", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task DeployDesiredWebAppsAsync_ReparsePointOverlayOwnedFile_IsLeftInPlaceAndWarns()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        File.Delete(overlayPath);
        var victim = Path.Join(descriptor.InstallPath!, "victim.txt");
        File.WriteAllText(victim, "victim");
        Skip.IfNot(TryCreateFileSymlink(overlayPath, victim),
            "This process cannot create a file symlink (symlink privilege or Developer Mode required); the reparse-point guard was not exercised.");

        repository.ArtifactConfigurationFiles.Clear();
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        Assert.True(File.Exists(victim));
        Assert.Equal("victim", File.ReadAllText(victim));
        var last = repository.PublishedWebAppResults.Last();
        Assert.False(last.Result.Applied);
        Assert.Contains("reparse point", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_OverlayReconcile_IsIdempotentAcrossCycles()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        Assert.True(File.Exists(overlayPath));

        repository.ArtifactConfigurationFiles.Clear();

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        Assert.False(File.Exists(overlayPath));
        Assert.True(repository.PublishedWebAppResults.Last().Result.Applied);

        // A restart-safe retry reports applied:false and changes nothing further.
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        Assert.False(File.Exists(overlayPath));
        Assert.False(repository.PublishedWebAppResults.Last().Result.Applied);
    }

    [Fact]
    public async Task DeploymentLease_BusyLeavesOverlayFileUntouched()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        repository.EnabledHostCount = 2;
        repository.DeploymentSettings["DeploymentLockScope"] = "app";
        var descriptor = CreateWebAppDeploymentDescriptor(out _, contentSha256: "aa11", deployedContentSha256: "AA11");
        repository.DesiredWebAppDeployments.Add(descriptor);
        repository.ArtifactConfigurationFiles.Add(
            CreateOverlayOwnedFile(descriptor.ArtifactId, "odv.site.config.js", "// overlay"));

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);
        var overlayPath = Path.Join(descriptor.InstallPath!, "odv.site.config.js");
        Assert.True(File.Exists(overlayPath));

        repository.ArtifactConfigurationFiles.Clear();
        var owner = Guid.NewGuid();
        await repository.AcquireAppDeploymentLeaseAsync("app:" + descriptor.AppKey, owner, "Other host", 600, default);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, default);

        Assert.True(File.Exists(overlayPath));
        Assert.Equal(HostDeploymentStatuses.Warning, repository.PublishedWebAppResults.Last().Result.State);
    }

    private static ArtifactConfigurationFileDescriptor CreateOverlayOwnedFile(
        int artifactId,
        string relativePath,
        string fileContent)
        => new()
        {
            ArtifactConfigurationFileId = 100,
            ArtifactId = artifactId,
            RelativePath = relativePath,
            FileContent = fileContent,
            IsOverlayOwnedWholeFile = true
        };

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void ApplyConfigurationForAlreadyAppliedDeployment(
        FakeOmpHostArtifactRepository repository,
        HostAgentSettings settings,
        WebAppDeploymentDescriptor deployment)
    {
        var configuredConnectionString = repository.GetConfiguredConnectionString();
        var configurationFiles = repository.GetArtifactConfigurationFilesAsync(
            deployment.ArtifactId,
            deployment.HostKey,
            CancellationToken.None).GetAwaiter().GetResult();
        configurationFiles = ArtifactConfigurationFileWriter.WithBuiltInWebAppConfiguration(
            configurationFiles,
            deployment,
            configuredConnectionString,
            settings);
        var configurationVariables = ArtifactConfigurationFileWriter.CreateVariables(
            deployment,
            configuredConnectionString,
            settings);
        ArtifactConfigurationFileWriter.ApplyAsync(
            deployment.InstallPath!,
            configurationFiles,
            configurationVariables,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ArtifactConfigurationFileDescriptor CreateAppSettingsWithDataProtectionKeyPath(
        int artifactId,
        string dataProtectionKeyPath)
        => new()
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = artifactId,
            RelativePath = "appsettings.json",
            FileContent = $$"""
            {
              "OmpAuth": {
                "DataProtectionKeyPath": {{JsonSerializer.Serialize(dataProtectionKeyPath)}}
              }
            }
            """
        };

    [Theory]
    [InlineData("app")]
    [InlineData("host")]
    public async Task DeploymentLease_BusyLeavesWebFilesUntouched(string scope)
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        repository.EnabledHostCount = 2;
        repository.DeploymentSettings["DeploymentLockScope"] = scope;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        var scopeKey = scope == "host" ? "host:*" : "app:" + descriptor.AppKey;
        var owner = Guid.NewGuid();
        await repository.AcquireAppDeploymentLeaseAsync(scopeKey, owner, "Other host", 600, default);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, default);

        Assert.Empty(Directory.GetFiles(descriptor.InstallPath!));
        var result = Assert.Single(repository.PublishedWebAppResults).Result;
        Assert.Equal(HostDeploymentStatuses.Warning, result.State);
        Assert.Contains(scopeKey, result.DiagnosticWarningMessage);
        Assert.Empty(repository.ReleasedAppLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentLease_IsHeldDuringWebHealthAndReleasedWithOutcome(bool failHealth)
    {
        FakeOmpHostArtifactRepository? observedRepository = null;
        var (service, repository, _) = CreateServiceWithFakeRepository((_, deployment, _, _) =>
        {
            Assert.Equal(deployment.HostId, observedRepository!.AppLeases["app:" + deployment.AppKey].HostId);
            Assert.Empty(observedRepository.ReleasedAppLeases);
            if (failHealth) throw new InvalidOperationException("Simulated readiness failure");
            return Task.CompletedTask;
        });
        observedRepository = repository;
        repository.EnabledHostCount = 2;
        repository.DesiredWebAppDeployments.Add(CreateWebAppDeploymentDescriptor(out _));
        await service.DeployDesiredWebAppsAsync("test-host", default);
        Assert.Empty(repository.AppLeases);
        var release = Assert.Single(repository.ReleasedAppLeases);
        Assert.Equal(failHealth ? "Simulated readiness failure" : null, release.Reason);
        Assert.Equal(failHealth ? HostDeploymentStatuses.Failed : HostDeploymentStatuses.Succeeded,
            repository.PublishedWebAppResults.Last().Result.State);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("host")]
    [InlineData("off")]
    public async Task DeploymentLease_SingleHostUsesOriginalWebPath(string scope)
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository((_, _, _, _) =>
            throw new InvalidOperationException("Single-host deployment must not add a readiness gate"));
        settings.DeploymentLockScope = scope;
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, default);
        Assert.Equal(0, repository.AppLeaseCalls);
        Assert.Equal(HostDeploymentStatuses.Succeeded, repository.PublishedWebAppResults.Last().Result.State);
        Assert.Equal("content", File.ReadAllText(Path.Join(descriptor.InstallPath!, "app.txt")));
    }

    [Fact]
    public async Task DeploymentLease_AlreadyAppliedHostSweepDoesNotAcquire()
    {
        var (service, repository, settings) = CreateServiceWithFakeRepository();
        settings.DeploymentLockScope = "host";
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, default);
        repository.EnabledHostCount = 2;
        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, default);
        Assert.Equal(0, repository.AppLeaseCalls);
    }

    private (WebAppDeploymentService Service, FakeOmpHostArtifactRepository Repository, HostAgentSettings Settings) CreateServiceWithFakeRepository(
        Func<HostAgentSettings, WebAppDeploymentDescriptor, string?, CancellationToken, Task>? health = null,
        ILogger<WebAppDeploymentService>? logger = null)
    {
        var repository = new FakeOmpHostArtifactRepository();
        var settings = CreateTestSettings();
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings };

        var service = new WebAppDeploymentService(
            optionsMonitor,
            repository,
            credentialStore: null!,
            logger ?? NullLogger<WebAppDeploymentService>.Instance,
            health ?? ((_, _, _, _) => Task.CompletedTask));

        return (service, repository, settings);
    }

    private HostAgentSettings CreateTestSettings()
    {
        var tempRoot = CreateTempDirectory();
        var webAppsRoot = Path.Join(tempRoot, "webapps");
        var portalPath = Path.Join(tempRoot, "portal");
        Directory.CreateDirectory(webAppsRoot);
        Directory.CreateDirectory(portalPath);

        return new HostAgentSettings
        {
            DeployWebApps = true,
            CentralArtifactRoot = Path.Join(tempRoot, "central"),
            LocalArtifactCacheRoot = Path.Join(tempRoot, "cache"),
            IisSiteName = "TestSite",
            EnsureIisSite = false,
            WebAppsRoot = webAppsRoot,
            PortalPhysicalPath = portalPath,
            UseAppOfflineForWebAppDeployment = true,
            StopIisAppPoolForWebAppDeployment = false,
            StartIisAppPoolAfterWebAppDeployment = false,
            AppOfflineShutdownDelayMilliseconds = 0,
            WebAppDataProtectionKeyPath = @"\\localhost\omp-dataprotection",
            MaxArtifactsPerCycle = 10,
            PortalHealthCheck = new HostAgentPortalHealthCheckSettings { Enabled = false }
        };
    }

    private WebAppDeploymentDescriptor CreateWebAppDeploymentDescriptor(
        out string moduleInstanceKey,
        string? contentSha256 = null,
        string? deployedContentSha256 = null,
        string version = "1.0.0")
    {
        moduleInstanceKey = "test-module-instance";
        var tempRoot = CreateTempDirectory();
        var sourcePath = Path.Join(tempRoot, "source");
        var targetPath = Path.Join(tempRoot, "target");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Join(sourcePath, "app.txt"), "content");

        return new WebAppDeploymentDescriptor
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "test-app",
            AppKey = "test-app",
            ModuleInstanceKey = moduleInstanceKey,
            DisplayName = "Test App",
            ArtifactId = 1,
            Version = version,
            SourceLocalPath = sourcePath,
            ContentSha256 = contentSha256,
            InstallPath = targetPath,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedArtifactId = 1,
            DeployedSourceLocalPath = sourcePath,
            DeployedTargetPath = targetPath,
            DeployedRuntimeName = "TestSite/",
            DeployedContentSha256 = deployedContentSha256
        };
    }

    private string CreateTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"OmpWebAppDeployTests-{Guid.NewGuid():N}");
        _tempPaths.Add(path);
        return path;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
