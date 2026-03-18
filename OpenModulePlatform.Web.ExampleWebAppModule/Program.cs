// File: OpenModulePlatform.Web.ExampleWebAppModule/Program.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Shared defaults include the common web logging setup (NLog),
// so new modules can focus on services/pages and use ILogger<T> where needed.
builder.AddOmpWebDefaults(optionsSectionName: "Portal");
builder.Services.AddScoped<ExampleWebAppModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
