using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using static OpenModulePlatform.HostAgent.Runtime.Services.OmpHostArtifactRepository;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ConfigOverlayAdministrationTests
{
    [Theory]
    [InlineData(null, "file.JSON ")]
    [InlineData(" MERGE ", "file.txt")]
    [InlineData("replace", "file.json")]
    [InlineData("legacy-unknown", "file.json")]
    public void EffectiveModeMatchesRuntimeIncludingLegacyRows(string? mode, string path)
        => Assert.Equal(OpenModulePlatform.HostAgent.Runtime.Services.ArtifactConfigurationFileWriter
            .ShouldMergeOverlayConfiguration(mode, path), ConfigOverlayAdministration.Mode(mode, path) == "merge");

    [Theory]
    [InlineData(OmpAdminRepository.OverlayStateSql)]
    [InlineData(OmpAdminRepository.OverlayEnableSiblingsSql)]
    [InlineData(OmpAdminRepository.OverlayUpdateStateSql)]
    [InlineData(OmpAdminRepository.OverlayDeleteSql)]
    [InlineData(OmpAdminRepository.PreviewHostSql)]
    [InlineData(OmpAdminRepository.PreviewArtifactSql)]
    [InlineData(OmpAdminRepository.PreviewArtifactFilesSql)]
    [InlineData(OmpAdminRepository.PreviewOverlayFilesSql)]
    [InlineData(OmpAdminRepository.PreviewOverlayFilesNoMergeSql)]
    public void AdministrationSqlParsesWithoutDatabaseAccess(string sql)
    {
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void AppInstancesRead_FailsSoftWhenTableIsMissing()
    {
        // Wiring guard for the schema-not-ready fallback: the AppInstances
        // read must check the table before querying, without connecting to
        // or altering any database.
        var source = OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Portal", "Services", "OmpAdminRepository.cs");
        var method = source[source.IndexOf("public async Task<IReadOnlyList<AppInstanceRow>> GetAppInstancesAsync", StringComparison.Ordinal)..];
        var guard = method.IndexOf("TableExistsAsync(conn, \"omp.AppInstances\"", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.True(guard < method.IndexOf("ExecuteReaderAsync", StringComparison.Ordinal));
    }
    internal static readonly DateTime Now = new(2026, 10, 18);
    internal static ConfigOverlayDocumentRow Overlay(int id = 1, string scope = "Host", bool enabled = true,
        string version = "1.0.0", string? minimum = null, string? module = null, string host = "host-a") => new()
    {
        ConfigOverlayDocumentId = id, OverlayKey = $"overlay-{id}", OverlayVersion = version,
        HostScope = scope, HostKey = scope == "AllHosts" ? "*" : host, IsEnabled = enabled,
        ArtifactVersion = minimum, ModuleKey = module, UpdatedUtc = Now, OverlaySha256 = "hash"
    };
    internal static OverlaySourceRow File(int id = 1, int? document = null, string content = "{}", string? mode = null,
        bool enabled = true, string path = "appsettings.json") => new(id, document, path, content, mode, enabled, Now);
    internal static OverlayAdminSnapshot Snapshot(params ConfigOverlayDocumentRow[] overlays) => new(true,
        [new() { HostKey = "host-a" }, new() { HostKey = "host-b" }],
        [new() { ArtifactId = 42, ModuleKey = "module", AppKey = "app", Version = "1.10.0", PackageType = "web-app" }], overlays);

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("C:/outside.json")]
    [InlineData("/outside.json")]
    [InlineData("folder/../../outside.json")]
    public void DraftRejectsUnsafeDeploymentPaths(string path)
    {
        var json = JsonSerializer.Serialize(new { overlayKey = "key", overlayVersion = "1.0.0",
            configurationFiles = new[] { new { relativePath = path, fileContent = "{}" } } });
        Assert.ThrowsAny<Exception>(() => ConfigOverlayAdministration.ValidateDraft(json, "Host", "host-a", Snapshot(), null));
    }

    [Fact]
    public void InlineReaderCannotResolveFilesystemOrSqlSidecars()
    {
        foreach (var array in new[] { "configurationFiles", "sqlScripts" })
        {
            var json = $$"""{"overlayKey":"key","overlayVersion":"1","hostKey":"host-a","{{array}}":[{"relativePath":"safe.json","source":"local-file.json"}]}""";
            Assert.Throws<InvalidOperationException>(() => ConfigOverlayPackageReader.ReadInlineConfigOverlay(json));
        }
    }

    [Theory]
    [InlineData("unknown", "host-a")]
    [InlineData("Host", "*")]
    [InlineData("Host", "foreign-host")]
    [InlineData("Host", "")]
    public void DraftRejectsInvalidScopeAndForeignHosts(string scope, string host)
        => Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(
            """{"overlayKey":"key","overlayVersion":"1"}""", scope, host, Snapshot(), null));

    [Fact]
    public void AllHostsIsOneCanonicalDocumentAndSelectorsSurviveEditing()
    {
        var json = """{"overlayKey":"overlay-1","overlayVersion":"1.0.0","moduleDefinitionVersion":"7.8.9","targetName":"site","configurationFiles":[]}""";
        var saved = ConfigOverlayAdministration.ValidateDraft(json, "AllHosts", "foreign-host", Snapshot(), Overlay(scope: "AllHosts"));
        Assert.Equal("*", saved.HostKey);
        Assert.Equal(ConfigOverlayHostScope.AllHosts, saved.HostScope);
        Assert.Equal("7.8.9", saved.ModuleDefinitionVersion);
        Assert.Equal("site", saved.TargetName);
    }

    [Fact]
    public void EditCannotMoveIdentityOrScopeAndOldSchemaCannotWrite()
    {
        const string json = """{"overlayKey":"overlay-1","overlayVersion":"1.0.0"}""";
        Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(json, "AllHosts", null, Snapshot(), Overlay()));
        Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(json, "Host", "host-b", Snapshot(), Overlay()));
        Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(json.Replace("1.0.0", "2.0.0"), "Host", "host-a", Snapshot(), Overlay()));
        Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(json, "Host", "host-a", Snapshot() with { SchemaReady = false }, null));
    }

    [Fact]
    public void MalformedFileCollectionCannotSilentlyEraseSavedFiles()
        => Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(
            """{"overlayKey":"key","overlayVersion":"1","configurationFiles":{}}""", "Host", "host-a", Snapshot(), null));

    [Fact]
    public async Task DuplicateHostKeysCannotBeMistakenForOneExactTarget()
    {
        var snapshot = Snapshot() with { Hosts = [new() { HostKey = "host-a" }, new() { HostKey = "HOST-A" }] };
        Assert.Throws<InvalidOperationException>(() => ConfigOverlayAdministration.ValidateDraft(
            """{"overlayKey":"key","overlayVersion":"1"}""", "Host", "host-a", snapshot, null));
        var store = new OverlayAdminFakeStore { Snapshot = snapshot };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfigOverlayAdministration(store).ReadEffectiveAsync(snapshot, "host-a", 42, default));
        Assert.Equal(0, store.SourceReads);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("package")]
    [InlineData("target")]
    public async Task EffectiveExcludesOtherAppPackageAndTargetSelectors(string selector)
    {
        var overlay = new ConfigOverlayDocumentRow
        {
            ConfigOverlayDocumentId = 1, OverlayKey = "other", HostKey = "host-a", HostScope = "Host", IsEnabled = true,
            AppKey = selector == "app" ? "other" : null, PackageType = selector == "package" ? "other" : null,
            TargetName = selector == "target" ? "other" : null
        };
        var store = new OverlayAdminFakeStore { Snapshot = Snapshot(overlay) };
        var result = await new ConfigOverlayAdministration(store).ReadEffectiveAsync(store.Snapshot, "host-a", 42, default);
        Assert.Empty(result.Files);
        Assert.Empty(store.DetailReads);
    }

    [Fact]
    public async Task NumericArtifactMinimumAllowsNewerArtifactAndDefinitionVersionIsNotRuntimeFilter()
    {
        var row = new ConfigOverlayDocumentRow
        {
            ConfigOverlayDocumentId = 1, OverlayKey = "settings", HostKey = "host-a", HostScope = "Host", IsEnabled = true,
            ArtifactVersion = "1.9.0", ModuleDefinitionVersion = "999.0.0"
        };
        var store = new OverlayAdminFakeStore { Snapshot = Snapshot(row) };
        store.Details[1] = new(row, "", [File(1, 1)]);
        var result = await new ConfigOverlayAdministration(store).ReadEffectiveAsync(store.Snapshot, "host-a", 42, default);
        Assert.Equal("settings", Assert.Single(result.Files).Winner.OverlayKey);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("merge", false)]
    [InlineData("replace", true)]
    public void ProvenanceMatchesRuntimeScopeSelectionAndLayering(string? hostMode, bool shadowsBase)
    {
        var baseFile = File(content: """{"base":true}""");
        var allHosts = Overlay(2, "AllHosts", version: "9.0.0");
        var host = Overlay(3);
        var candidates = new List<ConfigOverlayAdministration.Candidate>
        {
            new(baseFile, null), new(File(2, 2, """{"shared":true}"""), allHosts),
            new(File(3, 3, """{"host":true}""", hostMode), host),
            new(File(4, 4, "{}"), Overlay(4, "AllHosts", version: "2.0.0"))
        };
        var runtime = candidates.Select(c => new ResolvedConfigurationFile(new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = c.File.RowId, ArtifactId = 42,
            RelativePath = c.File.RelativePath, FileContent = c.File.Content
        }, c.Priority, c.Overlay?.UpdatedUtc ?? c.File.UpdatedUtc, c.Overlay?.OverlayVersion, c.File.MergeMode, null)).ToList();
        var runtimeWinner = runtime.Aggregate((a, b) => ConfigurationFileCandidateWins(b, a) ? b : a);
        Assert.Equal(3, runtimeWinner.Descriptor.ArtifactConfigurationFileId);
        var runtimeContent = ResolveLayeredConfigurationContent(runtime, runtimeWinner);
        Assert.Equal(!shadowsBase, runtimeContent.Contains("base", StringComparison.Ordinal));
        var effective = Assert.Single(ConfigOverlayAdministration.Resolve(candidates).Files);
        Assert.Equal(host.OverlayKey, effective.Winner.OverlayKey);
        Assert.Equal(shadowsBase, effective.Sources.Single(s => s.SourcePriority == 0).IsShadowed);
        Assert.True(effective.Sources.Single(s => s.OverlayKey == "overlay-4").IsShadowed);
        Assert.Single(effective.Sources, s => s.IsWinner);
    }

    [Fact]
    public void SameScopeUsesDocumentTimestampThenRowIdAndCaseInsensitivePaths()
    {
        var older = Overlay(1);
        var newer = new ConfigOverlayDocumentRow { OverlayKey = "newer", OverlayVersion = "1.0.0", HostScope = "Host", UpdatedUtc = Now.AddSeconds(1) };
        var effective = Assert.Single(ConfigOverlayAdministration.Resolve([
            new(File(99, content: "{}", path: "APPSETTINGS.JSON") with { UpdatedUtc = Now.AddDays(2) }, older),
            new(File(2), newer), new(File(3), newer)]).Files);
        Assert.Equal("newer", effective.Winner.OverlayKey);
        Assert.Single(effective.Sources, s => s.IsWinner);
        Assert.Equal(2, effective.Sources.Count(s => s.IsShadowed));
    }

    [Fact]
    public void MergeFailureDoesNotExposeContentOrClaimAnEffectiveWinner()
    {
        var result = ConfigOverlayAdministration.Resolve([
            new(File(content: "private-secret-not-json"), null), new(File(2, 1), Overlay())]);
        Assert.Empty(result.Files);
        Assert.Contains(result.Diagnostics, d => d.Contains("merge failed", StringComparison.Ordinal));
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EffectiveReadbackFiltersRealSelectorsStateAndNumericMinimum()
    {
        var store = new OverlayAdminFakeStore
        {
            Snapshot = Snapshot(Overlay(1), Overlay(2, enabled: false), Overlay(3, minimum: "1.11.0"),
                Overlay(4, module: "other-module"), Overlay(5, host: "foreign-host"), Overlay(6, "AllHosts"), Overlay(7))
        };
        store.Details[1] = new(Overlay(1), "", [File(1, 1, enabled: false)]);
        store.Details[6] = new(Overlay(6, "AllHosts"), "", [File(6, 6)]);
        store.Details[7] = new(Overlay(7), "", []);
        var result = await new ConfigOverlayAdministration(store).ReadEffectiveAsync(store.Snapshot, "host-a", 42, default);
        Assert.Equal("AllHosts", Assert.Single(result.Files).Winner.HostScope);
        Assert.Contains(result.Diagnostics, d => d.Contains("minimum 1.11.0", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("disabled file", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("no configuration files", StringComparison.Ordinal));
        Assert.Equal(new[] { 1, 6, 7 }, store.DetailReads);
        Assert.DoesNotContain("Content", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EffectiveRejectsForeignHostAndArtifactBeforeSourceReads()
    {
        var store = new OverlayAdminFakeStore();
        var service = new ConfigOverlayAdministration(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadEffectiveAsync(store.Snapshot, "foreign-host", 42, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadEffectiveAsync(store.Snapshot, "host-a", 999, default));
        Assert.Equal(0, store.SourceReads);
    }

    [Fact]
    public async Task PreScopeFallbackExcludesAllHostsAndShowsGuidance()
    {
        var store = new OverlayAdminFakeStore { Snapshot = Snapshot(Overlay(1, "AllHosts")) with { SchemaReady = false, HasHostScope = false } };
        var result = await new ConfigOverlayAdministration(store).ReadEffectiveAsync(store.Snapshot, "host-a", 42, default);
        Assert.Empty(result.Files);
        Assert.Empty(store.DetailReads);
        Assert.Contains(result.Diagnostics, d => d.Contains("exact-host fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void SetDraftFileContentAddsFilesAndPreservesExistingMetadata()
    {
        const string json = """{"overlayKey":"k","overlayVersion":"1","configurationFiles":[{"relativePath":"a.json","fileContent":"{}","mergeMode":"merge","custom":"keep"}]}""";
        var replaced = ConfigOverlayAdministration.SetDraftFileContent(json, "a.json", """{"x":1}""");
        using var doc = JsonDocument.Parse(replaced);
        var files = doc.RootElement.GetProperty("configurationFiles").EnumerateArray().ToList();
        Assert.Single(files);
        Assert.Equal("""{"x":1}""", files[0].GetProperty("fileContent").GetString());
        Assert.Equal("merge", files[0].GetProperty("mergeMode").GetString());
        Assert.Equal("keep", files[0].GetProperty("custom").GetString());

        var added = ConfigOverlayAdministration.SetDraftFileContent(json, "b.txt", "hi");
        using var doc2 = JsonDocument.Parse(added);
        var files2 = doc2.RootElement.GetProperty("configurationFiles").EnumerateArray().ToList();
        Assert.Equal(2, files2.Count);
        Assert.Equal("b.txt", files2[1].GetProperty("relativePath").GetString());
        Assert.Equal("replace", files2[1].GetProperty("mergeMode").GetString());
    }

    [Fact]
    public async Task ContentComparisonReportsSupersedingOverlayAndKeepsContentOutOfDiagnostics()
    {
        var store = new OverlayAdminFakeStore { Snapshot = Snapshot(Overlay(1)) };
        store.ArtifactFilesById[42] = [File(content: """{"base":"secret-base"}""", mode: "merge")];
        store.Details[1] = new(Overlay(1), "", [File(1, 1, """{"overlay":"secret-overlay"}""", mode: "merge")]);
        var comparison = await new ConfigOverlayAdministration(store).CompareContentAsync(store.Snapshot, "host-a", 42, default);
        var file = comparison.Files.Single(f => f.RelativePath == "appsettings.json");
        Assert.True(file.BaseAvailable);
        Assert.True(file.EffectiveAvailable);
        Assert.True(file.EffectiveDiffers);
        Assert.Contains(file.Notes, n => n.Contains("supersedes", StringComparison.Ordinal));
        // Content belongs only in the inert display fields, never in diagnostics
        // that could be surfaced to logs or status messages.
        Assert.DoesNotContain(comparison.Diagnostics, d => d.Contains("secret-", StringComparison.Ordinal));
    }
}

internal sealed class OverlayAdminFakeStore : IConfigOverlayAdminStore
{
    public OverlayAdminSnapshot Snapshot { get; set; } = ConfigOverlayAdministrationTests.Snapshot();
    public Dictionary<int, OverlayAdminDetail> Details { get; } = [];
    public List<int> DetailReads { get; } = [];
    public int Reads { get; private set; }
    public int SourceReads { get; private set; }
    public List<(int Id, string Token, bool? Enabled)> Changes { get; } = [];
    public List<string> ChangeActors { get; } = [];
    public List<OverlayDeletedHistoryRow> DeletedHistory { get; set; } = [];
    public PortableConfigOverlayDocument? Saved { get; private set; }
    public string? SavedToken { get; private set; }
    public string? SavedActor { get; private set; }
    public bool RejectSave { get; set; }
    public Task<OverlayAdminSnapshot> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Snapshot); }
    public Task<OverlayAdminDetail?> DetailAsync(int id, CancellationToken ct) { DetailReads.Add(id); return Task.FromResult(Details.GetValueOrDefault(id)); }
    public List<OverlaySourceRow> ArtifactFiles { get; } = [];
    public Dictionary<int, List<OverlaySourceRow>> ArtifactFilesById { get; } = [];
    public Task<IReadOnlyList<OverlaySourceRow>> SourcesAsync(int artifactId, CancellationToken ct)
    {
        SourceReads++;
        return Task.FromResult<IReadOnlyList<OverlaySourceRow>>(ArtifactFilesById.GetValueOrDefault(artifactId, ArtifactFiles));
    }
    public Task<IReadOnlyList<OverlayDeletedHistoryRow>> DeletedHistoryAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OverlayDeletedHistoryRow>>(DeletedHistory);
    public Task<int> SaveAsync(PortableConfigOverlayDocument document, int? id, string? expectedHash, string actor, CancellationToken ct)
    {
        if (RejectSave) throw new InvalidOperationException("private-secret SQL content");
        Saved = document; SavedToken = expectedHash; SavedActor = actor; return Task.FromResult(id ?? 1);
    }
    public Task ChangeStateAsync(int id, string expectedHash, bool? enabled, string actor, CancellationToken ct)
    { Changes.Add((id, expectedHash, enabled)); ChangeActors.Add(actor); return Task.CompletedTask; }
    public async Task ChangeStateFromPreviewAsync(int id, string expectedHash, bool? enabled, string hostKey, int artifactId,
        string previewToken, string actor, CancellationToken ct)
    {
        // In-memory equivalent of the transactional store: re-derive the
        // preview from current rows and reject a stale token before recording.
        var preview = await new ConfigOverlayAdministration(this).PreviewRemovalAsync(Snapshot, id, hostKey, artifactId, ct);
        if (preview.DetailUnavailable
            || ConfigOverlayAdministration.PreviewActionToken(preview.PreviewFingerprint, enabled) != previewToken)
            throw new InvalidOperationException("The preview is stale.");
        Changes.Add((id, expectedHash, enabled)); ChangeActors.Add(actor);
    }
}
