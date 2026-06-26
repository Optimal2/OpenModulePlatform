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
builder.Services.AddScoped<OmpAuthenticationPropertiesFactory>();
builder.Services.AddOmpCookieAuthentication(builder.Configuration);
var oidcProviderStatus = builder.Services.AddOmpOidcAuthentication(builder.Configuration);
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

if (oidcProviderStatus.IsEnabled)
{
    app.MapGet(OmpAuthDefaults.OidcLoginPath, (HttpContext context, string? returnUrl) =>
    {
        var safeReturnUrl = ResolveSafeReturnUrl(context, returnUrl);
        return Results.Challenge(
            new AuthenticationProperties
            {
                RedirectUri = safeReturnUrl
            },
            [OmpAuthDefaults.OidcAuthenticationScheme]);
    }).AllowAnonymous();
}

app.MapGet("/session-status", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";

    return context.User.Identity?.IsAuthenticated == true
        ? Results.Json(new { authenticated = true })
        : Results.Unauthorized();
});

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

static string ResolveSafeReturnUrl(HttpContext context, string? returnUrl)
{
    if (!string.IsNullOrWhiteSpace(returnUrl) &&
        IsSafeLocalReturnUrl(returnUrl) &&
        !IsCurrentLoginUrl(context, returnUrl))
    {
        return returnUrl;
    }

    return "/";
}

static bool IsCurrentLoginUrl(HttpContext context, string returnUrl)
{
    var returnPath = ExtractPath(returnUrl);
    var currentLoginPath = string.Concat(context.Request.PathBase.Value, "/login");

    return string.Equals(returnPath, currentLoginPath, StringComparison.OrdinalIgnoreCase)
        || string.Equals(returnPath, OmpAuthDefaults.LoginPath, StringComparison.OrdinalIgnoreCase);
}

static bool IsSafeLocalReturnUrl(string returnUrl)
{
    if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) ||
        !returnUrl.StartsWith("/", StringComparison.Ordinal) ||
        returnUrl.StartsWith("//", StringComparison.Ordinal) ||
        returnUrl.Contains('\\', StringComparison.Ordinal))
    {
        return false;
    }

    try
    {
        var unescaped = Uri.UnescapeDataString(returnUrl);
        return !unescaped.StartsWith("//", StringComparison.Ordinal)
            && !unescaped.Contains('\\', StringComparison.Ordinal);
    }
    catch (UriFormatException)
    {
        return false;
    }
}

static string ExtractPath(string returnUrl)
{
    var queryIndex = returnUrl.IndexOfAny(['?', '#']);
    return queryIndex >= 0 ? returnUrl[..queryIndex] : returnUrl;
}
