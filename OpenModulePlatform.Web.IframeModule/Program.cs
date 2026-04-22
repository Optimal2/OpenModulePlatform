using OpenModulePlatform.Web.IframeModule.Localization;
using OpenModulePlatform.Web.IframeModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<IframeModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<IframeModuleRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
