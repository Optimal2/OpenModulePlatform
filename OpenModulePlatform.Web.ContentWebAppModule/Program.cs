// File: OpenModulePlatform.Web.ContentWebAppModule/Program.cs
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Web.ContentWebAppModule.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ContentWebAppModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<ContentWebAppModuleOptions>(
    builder.Configuration.GetSection(ContentWebAppModuleOptions.SectionName));
builder.Services.Configure<RazorPagesOptions>(options =>
{
    options.Conventions.AddPageRoute("/Admin/Edit", "/admin/create");
    options.Conventions.AddPageRoute("/Page", "{*slug}");
});
builder.Services.AddScoped<ContentPageRepository>();
builder.Services.AddScoped<ContentRenderer>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);

app.Run();
