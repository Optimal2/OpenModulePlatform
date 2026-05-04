using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Components;
using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Localization;
using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleWebAppBlazorModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ExampleWebAppBlazorModuleAdminRepository>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: false);
app.UseAntiforgery();
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleWebAppBlazorModule");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
