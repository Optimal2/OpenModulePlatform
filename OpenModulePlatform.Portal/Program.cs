// File: OpenModulePlatform.Portal/Program.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults(optionsSectionName: "Portal");

builder.Services.AddScoped<AppCatalogService>();
builder.Services.AddScoped<OmpAdminRepository>();
builder.Services.AddScoped<RbacAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
