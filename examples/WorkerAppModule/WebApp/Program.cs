// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Localization;
using OpenModulePlatform.Web.ExampleWorkerAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleWorkerAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<ExampleWorkerAppModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
