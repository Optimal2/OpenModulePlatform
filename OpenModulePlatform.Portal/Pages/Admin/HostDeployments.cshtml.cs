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

    private async Task LoadAsync(CancellationToken ct)
    {
        SetTitles("Operations");
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

    public static long ToUnixTimeMilliseconds(DateTime? value)
        => value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : 0;
}
