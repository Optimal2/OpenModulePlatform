using System.Text.Json;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>Deterministic offline tests for the installation-wide read-only
/// overlay impact view: configured app contexts (never a host/artifact cross
/// product), per-file winner-plus-merged-content outcomes, selector and
/// minimum-version exclusions, unavailable contexts, possible artifact
/// matches and honest output bounds. In-memory only; no database, no disk.
/// </summary>
public sealed class ConfigOverlayInstallationImpactTests
{
    private static HostRow Host(string key, bool enabled = true) => new() { HostKey = key, IsEnabled = enabled };

    private static ArtifactRow Artifact(int id, string version = "1.10.0", string module = "module",
        string app = "app", string package = "web-app", bool enabled = true) => new()
    {
        ArtifactId = id, ModuleKey = module, AppKey = app, Version = version,
        PackageType = package, IsEnabled = enabled, RelativePath = "app",
        CreatedUtc = ConfigOverlayAdministrationTests.Now,
    };

    private static AppInstanceRow App(string host, string instanceKey, int? artifactId, string appKey = "app") => new()
    {
        AppInstanceId = Guid.NewGuid(), InstanceKey = "local", ModuleInstanceKey = "module-1",
        AppKey = appKey, AppInstanceKey = instanceKey, DisplayName = instanceKey,
        AppType = "web", HostKey = host, ArtifactId = artifactId, IsAllowed = true,
    };

    // Host-neutral app instance: no HostId and no target host template, which
    // the HostAgent provisions on every enabled host.
    private static AppInstanceRow NeutralApp(string instanceKey, int? artifactId, string appKey = "app") => new()
    {
        AppInstanceId = Guid.NewGuid(), InstanceKey = "local", ModuleInstanceKey = "module-1",
        AppKey = appKey, AppInstanceKey = instanceKey, DisplayName = instanceKey,
        AppType = "web", HostKey = null, TargetHostTemplateKey = null, ArtifactId = artifactId, IsAllowed = true,
    };

    // Template-targeted app instance: no direct HostId, a target host template
    // that the HostAgent resolves through active HostDeploymentAssignments.
    private static AppInstanceRow TemplateApp(string templateKey, string instanceKey, int? artifactId, string appKey = "app") => new()
    {
        AppInstanceId = Guid.NewGuid(), InstanceKey = "local", ModuleInstanceKey = "module-1",
        AppKey = appKey, AppInstanceKey = instanceKey, DisplayName = instanceKey,
        AppType = "web", HostKey = null, TargetHostTemplateKey = templateKey, ArtifactId = artifactId, IsAllowed = true,
    };

    private static HostDeploymentAssignmentRow Assignment(string hostKey, string templateKey, bool active = true) => new()
    {
        HostKey = hostKey, HostTemplateKey = templateKey, IsActive = active,
    };

    // A worker-instance ("workerinstance") or explicit host-artifact-requirement
    // ("hostrequirement") deployment row, as the shared repository query reads it.
    private static OverlayDeploymentContextRow Deployment(string hostKey, int artifactId,
        string sourceKind, string? sourceKey = null, string appKey = "app", string? appInstanceKey = null,
        string version = "1.10.0") => new(hostKey, appKey, appInstanceKey, artifactId, version, sourceKind, sourceKey);

    private static OverlayAdminSnapshot Snapshot(ConfigOverlayDocumentRow[] overlays, HostRow[] hosts,
        ArtifactRow[] artifacts, AppInstanceRow[] apps, HostDeploymentAssignmentRow[]? assignments = null,
        OverlayDeploymentContextRow[]? deploymentContexts = null, Guid[]? disabledAppInstanceIds = null)
        => new(true, hosts, artifacts, overlays, true, apps, null, assignments, deploymentContexts, disabledAppInstanceIds);

    private static OverlayAdminFakeStore Store(OverlayAdminSnapshot snapshot)
        => new() { Snapshot = snapshot };

    private static (OverlayAdminFakeStore Store, OverlayAdminSnapshot Snapshot) TwoHostFixture()
    {
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var host = ConfigOverlayAdministrationTests.Overlay(3);
        var snapshot = Snapshot([allHosts, host],
            [Host("host-a"), Host("host-b")],
            [Artifact(42)],
            [App("host-a", "web-a", 42), App("host-b", "web-b", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":"from-all-hosts"}""")]);
        store.Details[3] = new(host, "", [ConfigOverlayAdministrationTests.File(3, 3, """{"host":true}""")]);
        store.ArtifactFiles.Add(ConfigOverlayAdministrationTests.File(7, null, """{"base":true}"""));
        return (store, snapshot);
    }

    [Fact]
    public async Task TwoHostsSharingAllHostsOverlayStaySeparateContexts()
    {
        var (store, snapshot) = TwoHostFixture();
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(2, impact.TotalConfiguredContexts);
        Assert.Equal(0, impact.OmittedContexts);
        var first = impact.Contexts[0];
        var second = impact.Contexts[1];
        // Stable host/app identity: host-a before host-b, same app key kept separate.
        Assert.Equal("host-a", first.HostKey);
        Assert.Equal("host-b", second.HostKey);
        Assert.Equal("app", first.AppKey);
        Assert.Equal("app", second.AppKey);
        Assert.NotEqual(first.AppInstanceKey, second.AppInstanceKey);
        Assert.Equal("Applicable", first.ContextState);
        Assert.Equal("Applicable", second.ContextState);
        // Lower AllHosts merge layer contributes below the exact-host winner.
        var merged = Assert.Single(first.Files);
        Assert.Equal("ChangesEffectiveContent", merged.Outcome);
        Assert.Equal("overlay-3", merged.CurrentWinner!.OverlayKey);
        Assert.Equal("overlay-3", merged.WinnerAfterRemoval!.OverlayKey);
        // On host-b the AllHosts document is the winner and falls back to the artifact layer.
        var fallback = Assert.Single(second.Files);
        Assert.Equal("FallsBack", fallback.Outcome);
        Assert.Equal("overlay-2", fallback.CurrentWinner!.OverlayKey);
        Assert.Equal("Artifact", fallback.WinnerAfterRemoval!.SourceKind);
        // Metadata only: merged content keys never leak into DTOs or notes.
        var serialized = JsonSerializer.Serialize(impact);
        Assert.DoesNotContain("from-all-hosts", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("fileContent", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("FileContent", serialized, StringComparison.Ordinal);
        Assert.Empty(store.Changes);
    }

    [Fact]
    public async Task HostOverlayFallsBackOnItsHostAndIsNotApplicableElsewhere()
    {
        var (store, snapshot) = TwoHostFixture();
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 3, default);

        var own = impact.Contexts.Single(c => c.HostKey == "host-a");
        Assert.Equal("Applicable", own.ContextState);
        var file = Assert.Single(own.Files);
        Assert.Equal("FallsBack", file.Outcome);
        Assert.Equal("overlay-2", file.WinnerAfterRemoval!.OverlayKey);
        Assert.Equal("AllHosts", file.WinnerAfterRemoval.HostScope);
        var foreign = impact.Contexts.Single(c => c.HostKey == "host-b");
        Assert.Equal("NotApplicable", foreign.ContextState);
        // Blocked exactly like the single host/artifact preview: owned files
        // stay listed as Unchanged with the exclusion reason, proving no effect.
        var unaffected = Assert.Single(foreign.Files);
        Assert.Equal("Unchanged", unaffected.Outcome);
        Assert.Equal("overlay-2", unaffected.CurrentWinner!.OverlayKey);
        Assert.Equal("overlay-2", unaffected.WinnerAfterRemoval!.OverlayKey);
        Assert.Contains(unaffected.Notes, n => n.Contains("another host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectorMismatchExcludesContextsButKeepsPossibleArtifactMatches()
    {
        var foreign = ConfigOverlayAdministrationTests.Overlay(9, module: "other-module");
        var snapshot = Snapshot([foreign],
            [Host("host-a")],
            [Artifact(42), Artifact(99, module: "other-module")],
            [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        store.Details[9] = new(foreign, "", [ConfigOverlayAdministrationTests.File(9, 9)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 9, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("NotApplicable", context.ContextState);
        var excluded = Assert.Single(context.Files);
        Assert.Equal("Unchanged", excluded.Outcome);
        Assert.Contains(excluded.Notes, n => n.Contains("another application", StringComparison.Ordinal));
        // Artifact 99 matches the selectors but is referenced by no configured
        // app: a possible match with its exact version, never a deployment.
        var possible = Assert.Single(impact.PossibleArtifacts);
        Assert.Equal(99, possible.ArtifactId);
        Assert.Equal("1.10.0", possible.Version);
        Assert.Equal("other-module", possible.ModuleKey);
        Assert.DoesNotContain(impact.Contexts, c => c.ArtifactId == 99);
    }

    [Fact]
    public async Task MinimumVersionExclusionIsReportedPerContext()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(3, minimum: "1.11.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("host-b")],
            [Artifact(42)],
            [App("host-a", "web-a", 42), App("host-b", "web-b", 42)]);
        var store = Store(snapshot);
        store.Details[3] = new(overlay, "", [ConfigOverlayAdministrationTests.File(3, 3)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 3, default);

        Assert.All(impact.Contexts, c => Assert.Equal("NotApplicable", c.ContextState));
        var gated = impact.Contexts.Single(c => c.HostKey == "host-a");
        Assert.Contains(gated.Files.SelectMany(f => f.Notes),
            n => n.Contains("minimum 1.11.0", StringComparison.Ordinal));
        // Host scope is gated before the version minimum on the foreign host.
        var foreign = impact.Contexts.Single(c => c.HostKey == "host-b");
        Assert.Contains(foreign.Files.SelectMany(f => f.Notes),
            n => n.Contains("another host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsentHostsAndArtifactsAreUnavailableInsteadOfOmitted()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        // Template-targeted but no active assignment: a genuinely unresolvable
        // host, reported "Unavailable" with the reason rather than omitted.
        var orphan = new AppInstanceRow
        {
            AppInstanceId = Guid.NewGuid(), InstanceKey = "local", ModuleInstanceKey = "module-1",
            AppKey = "app", AppInstanceKey = "orphan", DisplayName = "orphan", AppType = "web",
            HostKey = null, TargetHostTemplateKey = "unassigned-template", ArtifactId = 42, IsAllowed = true,
        };
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("host-c")],
            [Artifact(42)],
            [App("host-a", "web-a", 42), orphan,
                App("ghost-host", "web-ghost", 42), App("host-a", "web-stale", 777), App("host-a", "web-bare", null)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(5, impact.TotalConfiguredContexts);
        Assert.Equal(0, impact.OmittedContexts);
        Assert.All(impact.Contexts.Where(c => c.ContextState == "Unavailable"), c => Assert.Empty(c.Files));
        Assert.Equal(4, impact.Contexts.Count(c => c.ContextState == "Unavailable"));
        var configured = Assert.Single(impact.Contexts, c => c.ContextState == "Applicable");
        Assert.Equal("host-a", configured.HostKey);
        Assert.Equal(42, configured.ArtifactId);
        Assert.Equal("1.10.0", configured.ArtifactVersion);
        // The template-targeted context explains exactly why no host resolves.
        var unresolved = impact.Contexts.Single(c => c.AppInstanceKey == "orphan");
        Assert.Contains(unresolved.Notes, n => n.Contains("unassigned-template", StringComparison.Ordinal)
            && n.Contains("no enabled host", StringComparison.Ordinal));
        // host-c is registered but has no configured app: an honest diagnostic, not a silent gap.
        Assert.Contains(impact.Diagnostics, d => d.Contains("host-c", StringComparison.Ordinal));
        Assert.Contains(impact.Diagnostics, d => d.Contains("saved rows only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContextsTruncateWithHonestCountsInStableOrder()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var apps = Enumerable.Range(0, 30)
            .Select(i => App("host-a", $"web-{i:00}", 42))
            .ToArray();
        var snapshot = Snapshot([overlay], [Host("host-a")], [Artifact(42)], apps);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(30, impact.TotalConfiguredContexts);
        Assert.Equal(ConfigOverlayAdministration.MaxInstallationContexts, impact.Contexts.Count);
        Assert.Equal(30 - ConfigOverlayAdministration.MaxInstallationContexts, impact.OmittedContexts);
        Assert.Equal("web-00", impact.Contexts[0].AppInstanceKey);
        Assert.Contains(impact.Diagnostics, d => d.Contains("omitted by bound", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoundKeepsRelevantContextsBeforeNotApplicableOnes()
    {
        // Live acceptance 2026-10-19: 41 contexts where the not-applicable
        // worker contexts sorted first by host and pushed the overlay's own
        // applicable context past the bound. Relevance ranks before the bound.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0", module: "module");
        var workerHosts = Enumerable.Range(0, 30).Select(i => Host($"aaa-worker-{i:00}")).ToArray();
        var snapshot = Snapshot([overlay],
            [.. workerHosts, Host("zz-host")],
            [Artifact(42), Artifact(99, module: "other-module")],
            [App("zz-host", "web-c", 42), App("zz-host", "web-a", 42), App("zz-host", "web-b", 42),
                App("ghost-host", "web-ghost", 42)],
            deploymentContexts: workerHosts
                .Select(h => Deployment(h.HostKey, 99, "hostrequirement", sourceKey: $"req-{h.HostKey}", appKey: "worker"))
                .ToArray());
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(34, impact.TotalConfiguredContexts);
        Assert.Equal(ConfigOverlayAdministration.MaxInstallationContexts, impact.Contexts.Count);
        Assert.Equal(34 - ConfigOverlayAdministration.MaxInstallationContexts, impact.OmittedContexts);
        // Applicable first in stable host/app order, then unavailable, then not applicable.
        Assert.Equal(["web-a", "web-b", "web-c"], impact.Contexts.Take(3).Select(c => c.AppInstanceKey));
        Assert.All(impact.Contexts.Take(3), c => Assert.Equal("Applicable", c.ContextState));
        Assert.Equal("Unavailable", impact.Contexts[3].ContextState);
        Assert.Equal("ghost-host", impact.Contexts[3].HostKey);
        Assert.All(impact.Contexts.Skip(4), c => Assert.Equal("NotApplicable", c.ContextState));
        Assert.Equal("aaa-worker-00", impact.Contexts[4].HostKey);
        var note = Assert.Single(impact.Diagnostics, d => d.Contains("omitted by bound", StringComparison.Ordinal)
            && d.StartsWith("Showing", StringComparison.Ordinal));
        Assert.Contains("9 further context(s) omitted by bound: 0 applicable, 0 unavailable, 9 not applicable.", note, StringComparison.Ordinal);
        Assert.Contains("Every omitted context is not applicable", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundCountsOmittedApplicableContextsWhenRelevantExceedBound()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0", module: "module");
        var apps = Enumerable.Range(0, 28).Select(i => App("zz-host", $"web-{i:00}", 42))
            .Append(App("aaa-host", "web-foreign", 99))
            .ToArray();
        var snapshot = Snapshot([overlay], [Host("aaa-host"), Host("zz-host")],
            [Artifact(42), Artifact(99, module: "other-module")], apps);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(29, impact.TotalConfiguredContexts);
        Assert.All(impact.Contexts, c => Assert.Equal("Applicable", c.ContextState));
        Assert.DoesNotContain(impact.Contexts, c => c.AppInstanceKey == "web-foreign");
        var note = Assert.Single(impact.Diagnostics, d => d.StartsWith("Showing", StringComparison.Ordinal));
        Assert.Contains("4 further context(s) omitted by bound: 3 applicable, 0 unavailable, 1 not applicable.", note, StringComparison.Ordinal);
        Assert.Contains("Refine selectors before changing.", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownDocumentThrowsBeforeAnySavedContextReads()
    {
        var (store, snapshot) = TwoHostFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 999, default));
        Assert.Equal(0, store.SourceReads);
        Assert.Empty(store.DetailReads);
    }

    [Fact]
    public async Task MissingDetailIsPartialWithoutOwnedFiles()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay], [Host("host-a")], [Artifact(42)], [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.True(impact.DetailUnavailable);
        var context = Assert.Single(impact.Contexts);
        Assert.Empty(context.Files);
        Assert.Contains(context.Notes, n => n.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(impact.Diagnostics, d => d.Contains("partial", StringComparison.Ordinal));
        Assert.DoesNotContain("Content", JsonSerializer.Serialize(impact));
    }

    [Fact]
    public async Task MergeFailureSharedByBothResolutionsIsEmittedOncePerContext()
    {
        var selected = ConfigOverlayAdministrationTests.Overlay(1);
        var other = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([selected, other],
            [Host("host-a")],
            [Artifact(42)],
            [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        store.Details[1] = new(selected, "", [ConfigOverlayAdministrationTests.File(1, 1, "{}", path: "other.json")]);
        store.Details[2] = new(other, "", [ConfigOverlayAdministrationTests.File(2, 2, "{}", path: "appsettings.json")]);
        store.ArtifactFiles.Add(ConfigOverlayAdministrationTests.File(7, null, "not-a-json-object", path: "appsettings.json"));
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 1, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("Applicable", context.ContextState);
        // The artifact/overlay merge fails identically before and after
        // excluding the selected document: one note, never two, no content.
        Assert.Equal(1, context.Notes.Count(n => n.Contains("merge failed", StringComparison.Ordinal)));
        Assert.DoesNotContain("not-a-json-object", JsonSerializer.Serialize(impact));
    }

    [Fact]
    public async Task ZeroPossibleMatchesSayWhenContextsWereTruncated()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var apps = Enumerable.Range(0, 30).Select(i => App("host-a", $"web-{i:00}", 42)).ToArray();
        var snapshot = Snapshot([overlay], [Host("host-a")], [Artifact(42)], apps);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Empty(impact.PossibleArtifacts);
        Assert.True(impact.OmittedContexts > 0);
        var diagnostic = Assert.Single(impact.Diagnostics, d => d.Contains("No unreferenced artifact matches", StringComparison.Ordinal));
        Assert.Contains("omitted by bound", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(impact.Diagnostics, d => d.Contains("configured on an app context above", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZeroPossibleMatchesKeepOriginalWordingWhenNothingTruncated()
    {
        var (store, snapshot) = TwoHostFixture();
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Empty(impact.PossibleArtifacts);
        Assert.Equal(0, impact.OmittedContexts);
        Assert.Contains(impact.Diagnostics, d => d.Contains("configured on an app context above", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateHostRowsAreUnavailableInsteadOfResolved()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("HOST-A")],
            [Artifact(42)],
            [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("Unavailable", context.ContextState);
        Assert.Empty(context.Files);
        Assert.Contains(context.Notes, n => n.Contains("duplicate host keys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingHostScopeFallsBackToExactHostPerContext()
    {
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = new OverlayAdminSnapshot(true,
            [Host("host-a")],
            [Artifact(42)],
            [overlay],
            HasHostScope: false,
            AppInstances: [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Contains(impact.Diagnostics, d => d.Contains("exact-host fallback", StringComparison.Ordinal));
        var context = Assert.Single(impact.Contexts);
        Assert.Equal("NotApplicable", context.ContextState);
        var unaffected = Assert.Single(context.Files);
        Assert.Equal("Unchanged", unaffected.Outcome);
        Assert.Contains(unaffected.Notes, n => n.Contains("another host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostNeutralAppsResolveToEveryEnabledHost()
    {
        // Mirrors the HostAgent rule "ai.HostId IS NULL AND
        // ai.TargetHostTemplateId IS NULL" -> provisioned on every enabled
        // host. Disabled hosts are never deployment targets.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("host-b", enabled: false), Host("host-c")],
            [Artifact(42)],
            [NeutralApp("web-neutral", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(2, impact.TotalConfiguredContexts);
        Assert.Equal(0, impact.OmittedContexts);
        Assert.All(impact.Contexts, c => Assert.Equal("Applicable", c.ContextState));
        Assert.Contains(impact.Contexts, c => c.HostKey == "host-a" && c.AppInstanceKey == "web-neutral");
        Assert.Contains(impact.Contexts, c => c.HostKey == "host-c" && c.AppInstanceKey == "web-neutral");
        Assert.DoesNotContain(impact.Contexts, c => c.HostKey == "host-b");
    }

    [Fact]
    public async Task TemplateTargetedAppsResolveOnlyToHostsWithActiveAssignment()
    {
        // Mirrors the HostAgent rule "ai.HostId IS NULL AND
        // ai.TargetHostTemplateId IS NOT NULL AND EXISTS (active
        // HostDeploymentAssignment to that template)": an inactive assignment
        // or a different template never makes a host a target.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("host-b"), Host("host-c")],
            [Artifact(42)],
            [TemplateApp("web-template", "web-role", 42)],
            [
                Assignment("host-a", "web-template", active: true),
                Assignment("host-b", "web-template", active: false),
                Assignment("host-c", "other-template", active: true),
            ]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("host-a", context.HostKey);
        Assert.Equal("web-role", context.AppInstanceKey);
        Assert.Equal("Applicable", context.ContextState);
    }

    [Fact]
    public async Task TemplateTargetedAppWithNoActiveAssignmentIsUnavailable()
    {
        // A template-targeted app context whose template has no active
        // assignment on any enabled host has genuinely no resolvable host and
        // stays "Unavailable" with that reason, never silently dropped.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a")],
            [Artifact(42)],
            [TemplateApp("ghost-template", "web-ghost", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("Unavailable", context.ContextState);
        Assert.Empty(context.Files);
        Assert.Equal("(no host assigned)", context.HostKey);
        Assert.Contains(context.Notes, n => n.Contains("ghost-template", StringComparison.Ordinal)
            && n.Contains("no enabled host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledAndDisallowedInstancesAreNotFannedOut()
    {
        // F1: the HostAgent's app-instance branch filters ai.IsEnabled = 1 AND
        // ai.IsAllowed = 1 (GetDesiredArtifactsAsync lines 3977-3978). A
        // disabled or disallowed host-neutral instance must not fan out to the
        // two enabled hosts; it stays one "not deployed" context with the
        // reason.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var disabled = NeutralApp("web-disabled", 42);
        var disallowed = NeutralApp("web-disallowed", 42);
        disallowed.IsAllowed = false;
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("host-b")],
            [Artifact(42)],
            [disabled, disallowed],
            disabledAppInstanceIds: [disabled.AppInstanceId]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        // Two filtered instances yield exactly two contexts, never two hosts each.
        Assert.Equal(2, impact.TotalConfiguredContexts);
        Assert.Equal(0, impact.OmittedContexts);
        Assert.All(impact.Contexts, c => Assert.Equal("NotApplicable", c.ContextState));
        Assert.All(impact.Contexts, c => Assert.Empty(c.Files));
        Assert.Contains(impact.Contexts, c => c.AppInstanceKey == "web-disabled"
            && c.Notes.Any(n => n.Contains("disabled", StringComparison.Ordinal)));
        Assert.Contains(impact.Contexts, c => c.AppInstanceKey == "web-disallowed"
            && c.Notes.Any(n => n.Contains("not allowed", StringComparison.Ordinal)));
        Assert.DoesNotContain(impact.Contexts, c => c.HostKey == "host-a");
        Assert.DoesNotContain(impact.Contexts, c => c.HostKey == "host-b");
    }

    [Fact]
    public async Task WorkerInstanceAndHostRequirementDeploymentsAreImpactContexts()
    {
        // F2: the HostAgent's desired-state query also deploys via a
        // WorkerInstances row (COALESCE(wi.ArtifactId, ai.ArtifactId), worker
        // branch) and via an explicit HostArtifactRequirements row (har
        // branch). Hosts reached only through those branches must appear as
        // real impact contexts, not merely in the hostsWithoutContext
        // diagnostic.
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a"), Host("worker-host"), Host("requirement-host")],
            [Artifact(42)],
            [App("host-a", "web-a", 42)],
            deploymentContexts:
            [
                Deployment("worker-host", 42, "workerinstance", sourceKey: "worker-1"),
                Deployment("requirement-host", 42, "hostrequirement", sourceKey: "req-1"),
            ]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        Assert.Equal(3, impact.TotalConfiguredContexts);
        Assert.Contains(impact.Contexts, c => c.HostKey == "worker-host" && c.ArtifactId == 42
            && c.Notes.Any(n => n.Contains("worker instance 'worker-1'", StringComparison.Ordinal)));
        Assert.Contains(impact.Contexts, c => c.HostKey == "requirement-host" && c.ArtifactId == 42
            && c.Notes.Any(n => n.Contains("host artifact requirement 'req-1'", StringComparison.Ordinal)));
        // Both deployment-only hosts are real contexts, so neither falls into
        // the "registered host without a context" diagnostic.
        Assert.DoesNotContain(impact.Diagnostics, d => d.Contains("worker-host", StringComparison.Ordinal));
        Assert.DoesNotContain(impact.Diagnostics, d => d.Contains("requirement-host", StringComparison.Ordinal));
        // The deployment-only artifact is referenced by those contexts, never
        // a dangling "possible" match.
        Assert.DoesNotContain(impact.PossibleArtifacts, p => p.ArtifactId == 42);
    }

    [Fact]
    public async Task DirectlyPinnedAppOnDisabledHostIsNotDeployed()
    {
        // F3: the HostAgent resolves @hostId only from enabled hosts
        // (GetDesiredArtifactsAsync lines 3921-3924), so an app directly
        // pinned to a disabled host is never deployed and must not read
        // "Applicable".
        var overlay = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var snapshot = Snapshot([overlay],
            [Host("host-a", enabled: false)],
            [Artifact(42)],
            [App("host-a", "web-a", 42)]);
        var store = Store(snapshot);
        store.Details[2] = new(overlay, "", [ConfigOverlayAdministrationTests.File(2, 2)]);
        var impact = await new ConfigOverlayAdministration(store).PreviewInstallationImpactAsync(snapshot, 2, default);

        var context = Assert.Single(impact.Contexts);
        Assert.Equal("NotApplicable", context.ContextState);
        Assert.Empty(context.Files);
        Assert.Equal("host-a", context.HostKey);
        Assert.Contains(context.Notes, n => n.Contains("disabled host", StringComparison.Ordinal)
            && n.Contains("host-a", StringComparison.Ordinal));
    }
}
