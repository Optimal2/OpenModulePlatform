// File: OpenModulePlatform.Auth/Program.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebLogging();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRazorPages()
    .AddViewLocalization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddScoped<OmpConfigurationService>();
builder.Services.AddScoped<RbacService>();
builder.Services.AddScoped<OmpBrandingService>();
builder.Services.AddSingleton<LocalPasswordHasher>();
builder.Services.AddSingleton<WindowsPrincipalReader>();
builder.Services.AddSingleton<WindowsPasswordAuthenticator>();
builder.Services.AddScoped<OmpAuthRepository>();
builder.Services.AddOmpCookieAuthentication(builder.Configuration);
var runningUnderIis = !string.IsNullOrWhiteSpace(
    Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"));

if (!runningUnderIis)
{
    builder.Services.AddAuthentication()
        .AddNegotiate();
}

builder.Services.AddAuthorization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[]
    {
        new CultureInfo("sv-SE"),
        new CultureInfo("en-US")
    };

    options.DefaultRequestCulture = new RequestCulture("sv-SE");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});

var app = builder.Build();

app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
app.UseOmpSecurityHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapPost("/logout", async (HttpContext context, IOptions<OmpAuthOptions> authOptions) =>
{
    await context.SignOutAsync(OmpAuthDefaults.AuthenticationScheme);
    ActiveRoleCookie.Clear(context.Response);

    var loginPath = string.IsNullOrWhiteSpace(authOptions.Value.LoginPath)
        ? OmpAuthDefaults.LoginPath
        : authOptions.Value.LoginPath;

    return Results.LocalRedirect(loginPath);
});

app.Run();
