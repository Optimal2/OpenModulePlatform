using Microsoft.AspNetCore.Mvc.RazorPages;
// File: OpenModulePlatform.Portal/Program.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<PortalResource>(optionsSectionName: "Portal");

builder.Services.AddScoped<AppCatalogService>();
builder.Services.AddScoped<OmpAdminRepository>();
builder.Services.AddScoped<OmpUserAdminRepository>();
builder.Services.AddScoped<PortalUserSettingsService>();
builder.Services.AddScoped<RbacAdminRepository>();

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
