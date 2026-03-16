using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Components;
using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults(optionsSectionName: "Portal");
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ExampleWebAppBlazorModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: false);
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
