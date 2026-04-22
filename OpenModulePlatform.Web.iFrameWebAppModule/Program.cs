using OpenModulePlatform.Web.iFrameWebAppModule.Localization;
using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<IFrameWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<IFrameWebAppModuleRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
