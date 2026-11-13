using OpenModulePlatform.Web.ContextGuardModule.Hosting;
using OpenModulePlatform.Web.ContextGuardModule.Localization;
using OpenModulePlatform.Web.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.AddOmpWebDefaults<ContextGuardModuleResource>(optionsSectionName: "Portal");
builder.Services.AddContextGuardModule(builder.Configuration);

var app = builder.Build();
app.UseContextGuardCsp();
app.MapContextGuardEndpoints();
app.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
app.Logger.LogInformation(
    "Context Guard v1 uses process-local stores and supports ONE instance per route. " +
    "With several instances, hand-off keys can be lost and supersede, heartbeat and end outcomes are not shared.");
app.Run();

public partial class Program;
