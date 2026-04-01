// File: OpenModulePlatform.Web.ExampleServiceAppModule/Program.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Localization;
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleServiceAppModuleResource>(optionsSectionName: "Portal");
builder.Services.AddScoped<ExampleServiceAppModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
