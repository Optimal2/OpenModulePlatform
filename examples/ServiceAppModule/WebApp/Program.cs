// File: OpenModulePlatform.Web.ExampleServiceAppModule/Program.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Localization;
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.Shared.Configuration;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.OpenDocViewer;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebDefaults<ExampleServiceAppModuleResource>(optionsSectionName: "Portal");
builder.Services.Configure<OpenDocViewerExampleOptions>(
    builder.Configuration.GetSection(OpenDocViewerExampleOptions.DefaultSectionName));
builder.Services.AddScoped<ExampleServiceAppModuleAdminRepository>();
// User log: what an admin did in the web app (saved a configuration or an
// app instance, queued a job) goes to omp_example_serviceapp.ActivityLog in the
// shared envelope and shows up in the Portal user log. The service or
// worker itself is not a person and writes nothing here.
builder.Services.AddOmpActivityLog(new ActivityLogOptions
{
    SchemaName = "omp_example_serviceapp",
    ModuleKey = "example_serviceapp",
    AppKey = "example-serviceapp-web"
});
builder.Services.AddScoped<IModuleConfigIdValidator, ExampleServiceAppModuleConfigIdValidator>();

var app = builder.Build();

app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
app.MapOpenDocViewerExampleBundleEndpoint("OpenModulePlatform.Web.ExampleServiceAppModule");

app.Run();
