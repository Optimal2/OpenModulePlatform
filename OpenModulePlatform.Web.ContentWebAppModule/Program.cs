// File: OpenModulePlatform.Web.ContentWebAppModule/Program.cs
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Web.ContentWebAppModule.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ContentWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddOptions<ContentWebAppModuleOptions>()
    .Bind(builder.Configuration.GetSection(ContentWebAppModuleOptions.SectionName))
    .Validate(
        options => options.AppInstanceId != Guid.Empty,
        "ContentWebAppModule:AppInstanceId must be configured.")
    .ValidateOnStart();
builder.Services.Configure<RazorPagesOptions>(options =>
{
    options.Conventions.AddPageRoute("/Admin/Edit", "/admin/create");
    options.Conventions.AddPageRoute("/Page", "{*slug:nonfile}");
});
builder.Services.AddScoped<ContentPageRepository>();
// User log: admin actions on content pages go to omp_content.ActivityLog in the
// shared envelope and show up in the Portal user log next to every module's entries.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_content",
    ModuleKey = "content_webapp",
    AppKey = "content-webapp"
});
builder.Services.AddScoped<ContentRenderer>();
builder.Services.AddScoped<HtmlContentFileLoader>();
builder.Services.AddScoped<ServerReportDefinitionLoader>();
builder.Services.AddScoped<ServerReportQueryRunner>();
builder.Services.AddScoped<ServerReportRenderer>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
