// File: OpenModulePlatform.Web.ExampleWebAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults(optionsSectionName: "Portal");
builder.Services.AddScoped<ExampleWebAppModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
