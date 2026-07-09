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
var uploadTempRoot = ResolveUploadTempRoot(builder.Configuration);
ConfigureAspNetCoreTempDirectory(uploadTempRoot);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.AddOmpWebDefaults<PortalResource>(optionsSectionName: "Portal");
builder.AddOmpPushEventDispatcher();

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
builder.Services.AddSingleton<PortalDeploymentLockService>();
builder.Services.AddScoped<ConfigOverlayObjectService>();
builder.Services.AddScoped<PortalHealthService>();
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

app.MapGet("/health/live", (PortalHealthService healthService) =>
{
    var result = healthService.CheckLive();
    return Results.Json(
        result,
        statusCode: StatusCodes.Status200OK);
}).AllowAnonymous();

app.MapGet("/health/ready", async (PortalHealthService healthService, CancellationToken ct) =>
{
    var result = await healthService.CheckReadyAsync(ct);
    return Results.Json(
        result,
        statusCode: result.IsHealthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.Run();

static string ResolveUploadTempRoot(ConfigurationManager configuration)
{
    var configuredPath = configuration.GetValue<string>($"{ArtifactUploadOptions.SectionName}:TempRoot");
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
    }

    return Path.Join(Path.GetTempPath(), "OpenModulePlatform", "PortalUploads");
}

static void ConfigureAspNetCoreTempDirectory(string tempRoot)
{
    Directory.CreateDirectory(tempRoot);

    var probePath = Path.Join(tempRoot, $".omp-upload-temp-probe-{Guid.NewGuid():N}.tmp");
    using (File.Create(probePath, bufferSize: 1, FileOptions.DeleteOnClose))
    {
    }

    Environment.SetEnvironmentVariable("ASPNETCORE_TEMP", tempRoot);
}

