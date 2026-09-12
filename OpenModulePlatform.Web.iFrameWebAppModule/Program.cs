using OpenModulePlatform.Web.iFrameWebAppModule.Localization;
using OpenModulePlatform.Web.iFrameWebAppModule.Security;
using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<IFrameWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<IFrameWebAppModuleRepository>();
// User log: which configured target a user opened goes to omp_iframe.ActivityLog
// in the shared envelope and shows up in the Portal user log.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_iframe",
    ModuleKey = "iframe_webapp",
    AppKey = "iframe-webapp"
});

var app = builder.Build();

// Sets the module's CSP with the DB-derived frame-src allowlist before the shared
// security headers run; their set-if-missing pattern keeps this value.
app.UseIFrameFrameSourceCsp(optionsSectionName: "Portal");

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
