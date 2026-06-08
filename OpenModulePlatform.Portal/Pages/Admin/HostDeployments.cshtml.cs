// File: OpenModulePlatform.Portal/Pages/Admin/HostDeployments.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class HostDeploymentsModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public HostDeploymentsModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<HostAppDeploymentStateRow> AppDeploymentStates { get; private set; } = [];

    public IReadOnlyList<HostArtifactStateRow> ArtifactStates { get; private set; } = [];

    public IReadOnlyList<HostAgentUpgradeRow> HostAgentRows { get; private set; } = [];

    public IReadOnlyList<HostAgentArtifactOption> HostAgentArtifactOptions { get; private set; } = [];

    public IReadOnlyList<HostDriftSummaryRow> HostDriftSummaries { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRequestIdentityRepair(Guid hostId, Guid appInstanceId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        var requestedBy = User.Identity?.Name ?? "PortalAdmin";
        await _repo.RequestServiceIdentityRepairAsync(hostId, appInstanceId, requestedBy, ct);
        TempData["StatusMessage"] = "Service identity repair was requested. HostAgent will apply it during its next cycle when credential automation is set to PortalAdminApproved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequestHostAgentUpgrade(Guid hostId, int artifactId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        if (hostId == Guid.Empty || artifactId <= 0)
        {
            TempData["StatusMessage"] = "Select a host and HostAgent artifact before requesting an upgrade.";
            return RedirectToPage();
        }

        var changedRows = await _repo.RequestHostAgentUpgradeAsync(hostId, artifactId, ct);
        TempData["StatusMessage"] = changedRows > 0
            ? "HostAgent upgrade was requested. The active HostAgent will install and start the selected version during its next cycle."
            : "HostAgent desired version already matched the selected artifact, or the selected host/artifact was not valid.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        SetTitles("Operations");
        HostDriftSummaries = await _repo.GetHostDriftSummariesAsync(ct);
        HostAgentRows = await _repo.GetHostAgentUpgradeRowsAsync(ct);
        var hostAgentArtifactOptions = (await _repo.GetHostAgentArtifactOptionsAsync(ct)).ToList();
        hostAgentArtifactOptions.Sort(CompareHostAgentArtifactOptions);
        HostAgentArtifactOptions = hostAgentArtifactOptions;
        AppDeploymentStates = await _repo.GetHostAppDeploymentStatesAsync(ct);
        ArtifactStates = await _repo.GetHostArtifactStatesAsync(ct);
    }

    public static string FormatHostDeploymentStatus(byte status)
        => status switch
        {
            0 => "Pending",
            1 => "Running",
            2 => "Succeeded",
            3 => "Failed",
            4 => "Warning",
            _ => "Unknown"
        };

    public static string FormatArtifactProvisioningStatus(byte status)
        => status switch
        {
            0 => "Unknown",
            1 => "Pending",
            2 => "Succeeded",
            3 => "Failed",
            4 => "Hash mismatch",
            _ => "Unknown"
        };

    public static bool IsInterestingAppDeployment(HostAppDeploymentStateRow row)
        => row.DeploymentState != 2
           || !string.IsNullOrWhiteSpace(row.LastError)
           || string.Equals(row.IdentityCheckStatus, "ManualActionRequired", StringComparison.OrdinalIgnoreCase)
           || string.Equals(row.IdentityCheckStatus, "WaitingForPortalAdminApproval", StringComparison.OrdinalIgnoreCase);

    public static bool IsInterestingArtifactState(HostArtifactStateRow row)
        => row.ProvisioningState != 2
           || !string.IsNullOrWhiteSpace(row.LastError);

    public static string FormatHostDriftStatus(HostDriftSummaryRow row)
    {
        if (row.FailedAppCount > 0)
        {
            return "Failed";
        }

        if (row.RunningAppCount > 0)
        {
            return "Running";
        }

        if (row.HostAgentUpgradePending || row.PendingAppCount > 0 || row.MaterializationPendingCount > 0)
        {
            return "Pending";
        }

        if (row.WarningAppCount > 0)
        {
            return "Warning";
        }

        if (row.DesiredAppCount == 0)
        {
            return "No desired apps";
        }

        return "In sync";
    }

    public int GetSelectedHostAgentArtifactId(HostAgentUpgradeRow row)
    {
        if (row.DesiredArtifactId is int desiredArtifactId
            && HostAgentArtifactOptions.Any(option => option.ArtifactId == desiredArtifactId))
        {
            return desiredArtifactId;
        }

        return HostAgentArtifactOptions.FirstOrDefault()?.ArtifactId ?? 0;
    }

    public static bool IsHostAgentUpgradePending(HostAgentUpgradeRow row)
        => !string.IsNullOrWhiteSpace(row.DesiredVersion)
           && !string.Equals(row.CurrentVersion, row.DesiredVersion, StringComparison.OrdinalIgnoreCase);

    public static long ToUnixTimeMilliseconds(DateTime? value)
        => value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : 0;

    private static int CompareHostAgentArtifactOptions(HostAgentArtifactOption left, HostAgentArtifactOption right)
    {
        var versionComparison = CompareVersions(left.Version, right.Version);
        if (versionComparison != 0)
        {
            return -versionComparison;
        }

        var createdComparison = left.CreatedUtc.CompareTo(right.CreatedUtc);
        if (createdComparison != 0)
        {
            return -createdComparison;
        }

        return right.ArtifactId.CompareTo(left.ArtifactId);
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion)
            && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
