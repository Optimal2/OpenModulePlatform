// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages;

public sealed class IndexModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;
    private readonly NotificationService _notifications;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        ExampleWebAppModuleAdminRepository repo,
        NotificationService notifications,
        ILogger<IndexModel> logger)
        : base(options, rbac)
    {
        _repo = repo;
        _notifications = notifications;
        _logger = logger;
    }

    public OverviewRow Overview { get; private set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        _logger.LogInformation("Loading ExampleWebAppModule overview page.");

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for ExampleWebAppModule overview page.");
            return guard;
        }

        SetTitles("Overview");
        Overview = await _repo.GetOverviewAsync(ct);

        _logger.LogInformation("ExampleWebAppModule overview loaded successfully.");
        return Page();
    }

    public async Task<IActionResult> OnPostSendTestNotification(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var userId = NotificationService.TryGetOmpUserId(User);
        if (userId is null)
        {
            ErrorMessage = T("Notifications require an OMP user");
            return RedirectToPage();
        }

        await _notifications.CreateForUserAsync(
            userId.Value,
            new NotificationCreateRequest(
                Title: T("Test notification"),
                Content: T("This notification was sent from ExampleModule."),
                DestinationUrl: BuildCurrentRelativeUrl(),
                Level: "info",
                CallerKey: "ExampleModule",
                CallerDisplayName: "ExampleModule",
                CallerIcon: "/_content/OpenModulePlatform.Web.Shared/icons/notifications.svg"),
            ct);

        StatusMessage = T("Notification sent");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendTestBanner(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var userId = NotificationService.TryGetOmpUserId(User);
        if (userId is null)
        {
            ErrorMessage = T("Notifications require an OMP user");
            return RedirectToPage();
        }

        await _notifications.CreateForUserAsync(
            userId.Value,
            new NotificationCreateRequest(
                Title: T("Test banner"),
                Content: T("This banner was sent from ExampleModule."),
                DestinationUrl: BuildCurrentRelativeUrl(),
                Level: "banner",
                CallerKey: "ExampleModule",
                CallerDisplayName: "ExampleModule",
                CallerIcon: "/_content/OpenModulePlatform.Web.Shared/icons/notifications.svg"),
            ct);

        StatusMessage = T("Banner sent");
        return RedirectToPage();
    }

    private string BuildCurrentRelativeUrl()
    {
        var url = string.Concat(Request.PathBase, Request.Path);
        return string.IsNullOrWhiteSpace(url) ? "/" : url;
    }
}
