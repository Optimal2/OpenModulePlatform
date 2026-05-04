// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Localization;
using OpenModulePlatform.Web.ExampleWorkerAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleWorkerAppModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services.AddScoped<ExampleWorkerAppModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleWorkerAppModule");

app.Run();
