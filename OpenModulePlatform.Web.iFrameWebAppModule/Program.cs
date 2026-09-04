using OpenModulePlatform.Web.iFrameWebAppModule.Localization;
using OpenModulePlatform.Web.iFrameWebAppModule.Security;
using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<IFrameWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<IFrameWebAppModuleRepository>();

var app = builder.Build();

// Sets the module's CSP with the DB-derived frame-src allowlist before the shared
// security headers run; their set-if-missing pattern keeps this value.
app.UseIFrameFrameSourceCsp(optionsSectionName: "Portal");

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
