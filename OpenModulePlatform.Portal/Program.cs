using Microsoft.AspNetCore.Mvc.RazorPages;
// File: OpenModulePlatform.Portal/Program.cs
using Microsoft.AspNetCore.Http.Features;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<PortalResource>(optionsSectionName: "Portal");

builder.Services.AddScoped<AppCatalogService>();
builder.Services.AddSingleton<LocalPasswordHasher>();
builder.Services.AddScoped<OmpAdminRepository>();
builder.Services.AddScoped<OmpConfigSettingsAdminRepository>();
builder.Services.AddScoped<OmpUserAdminRepository>();
builder.Services.AddScoped<PortalUserSettingsAdminRepository>();
builder.Services.AddScoped<PortalEntryService>();
builder.Services.AddScoped<PortalUserSettingsService>();
builder.Services.AddScoped<RbacAdminRepository>();
builder.Services.Configure<ArtifactUploadOptions>(
    builder.Configuration.GetSection(ArtifactUploadOptions.SectionName));
builder.Services.Configure<FormOptions>(options =>
{
    var maxUploadBytes = builder.Configuration.GetValue<long?>(
        $"{ArtifactUploadOptions.SectionName}:MaxUploadBytes");
    options.MultipartBodyLengthLimit = maxUploadBytes is > 0
        ? maxUploadBytes.Value
        : ArtifactUploadOptions.DefaultMaxUploadBytes;
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

app.Run();
