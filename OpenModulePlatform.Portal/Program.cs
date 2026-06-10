using Microsoft.AspNetCore.Mvc.RazorPages;
// File: OpenModulePlatform.Portal/Program.cs
using Microsoft.AspNetCore.Http.Features;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

var builder = WebApplication.CreateBuilder(args);
var maxUploadBytes = builder.Configuration.GetValue<long?>(
    $"{ArtifactUploadOptions.SectionName}:MaxUploadBytes") is > 0 and var configuredMaxUploadBytes
        ? configuredMaxUploadBytes
        : ArtifactUploadOptions.DefaultMaxUploadBytes;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.AddOmpWebDefaults<PortalResource>(optionsSectionName: "Portal");

builder.Services.AddScoped<AppCatalogService>();
builder.Services.AddSingleton<LocalPasswordHasher>();
builder.Services.AddScoped<OmpAdminRepository>();
builder.Services.AddScoped<OmpConfigSettingsAdminRepository>();
builder.Services.AddScoped<OmpUserAdminRepository>();
builder.Services.AddScoped<PortalUserSettingsAdminRepository>();
builder.Services.AddScoped<PortalEntryService>();
builder.Services.AddScoped<PortalEntryIFrameStandaloneHelperService>();
builder.Services.AddScoped<IFrameAdminService>();
builder.Services.AddScoped<PortalDashboardService>();
builder.Services.AddScoped<PortalModuleDashboardService>();
builder.Services.AddScoped<PortalBlankWidgetService>();
builder.Services.AddScoped<PortalMusicPlayerService>();
builder.Services.AddScoped<PortalDashboardWidgetPackageService>();
builder.Services.AddScoped<PortalWidgetRuntimeDataPackageService>();
builder.Services.AddScoped<PortalUserSettingsService>();
builder.Services.AddScoped<UserProfileImageService>();
builder.Services.AddScoped<RbacAdminRepository>();
builder.Services.AddScoped<PortableModulePackageService>();
builder.Services.AddScoped<ConfigOverlayObjectService>();
builder.Services.Configure<ArtifactUploadOptions>(
    builder.Configuration.GetSection(ArtifactUploadOptions.SectionName));
builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = maxUploadBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.Services.Configure<RazorPagesOptions>(options =>
{
    options.Conventions.AddPageRoute("/Admin/Rbac/Index", "/admin/security");
    options.Conventions.AddPageRoute("/Admin/Rbac/Roles", "/admin/security/roles");
    options.Conventions.AddPageRoute("/Admin/Rbac/Role", "/admin/security/role");
    options.Conventions.AddPageRoute("/Admin/Rbac/Permissions", "/admin/security/permissions");
    options.Conventions.AddPageRoute("/Admin/Rbac/PermissionEdit", "/admin/security/permissionedit");
});

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.MapGet("/notifications/summary", async (
    HttpContext context,
    NotificationService notificationService,
    long? afterNotificationId,
    CancellationToken ct) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var userId = NotificationService.TryGetOmpUserId(context.User);
    if (userId is null)
    {
        return Results.Json(new
        {
            unreadCount = 0,
            latestNotificationId = 0L,
            newNotificationCount = 0,
            newNotifications = Array.Empty<object>()
        });
    }

    var summary = await notificationService.GetToastSummaryForUserAsync(
        userId.Value,
        afterNotificationId,
        limit: 5,
        ct);

    return Results.Json(new
    {
        unreadCount = summary.UnreadCount,
        latestNotificationId = summary.LatestNotificationId,
        newNotificationCount = summary.NewNotificationCount,
        newNotifications = summary.NewNotifications.Select(row => new
        {
            notificationId = row.NotificationId,
            title = row.Title,
            content = ToToastSnippet(row.Content),
            targetUrl = IsSafeLocalDestination(row.DestinationUrl) ? row.DestinationUrl : "/notifications"
        })
    });
}).AllowAnonymous();

app.Run();

static string ToToastSnippet(string value)
{
    var normalized = string.Join(
        " ",
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    return normalized.Length <= 180
        ? normalized
        : string.Concat(normalized.AsSpan(0, 177), "...");
}

static bool IsSafeLocalDestination(string? destinationUrl)
    => !string.IsNullOrWhiteSpace(destinationUrl)
       && destinationUrl.StartsWith("/", StringComparison.Ordinal)
       && !destinationUrl.StartsWith("//", StringComparison.Ordinal)
       && !destinationUrl.Contains('\\', StringComparison.Ordinal);
