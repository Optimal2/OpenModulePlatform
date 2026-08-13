// File: OpenModulePlatform.Auth/Program.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var authWebAppOptions = new WebAppOptions
{
    DefaultCulture = "sv-SE",
    SupportedCultures = ["sv-SE", "en-US"]
};

// Bind the shared WebApp section so the forwarded-headers trust settings are configurable
// here too. This app hand-rolls its pipeline rather than using AddOmpWebDefaults, so it
// never picked up the R5-F6 policy -- and it is the only consumer of LoginThrottleService,
// which means the per-client-IP throttle was reading the proxy's address and the whole
// organization shared one lockout bucket (R8-INV-8).
builder.Configuration.GetSection(WebAppOptions.DefaultSectionName).Bind(authWebAppOptions);
builder.Services.AddOmpForwardedHeaders(authWebAppOptions);
var cultureSelectionService = new CultureSelectionService();

builder.AddOmpWebLogging();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
    {
        // App resource first, SharedResource as fallback - the same
        // composite-localizer pattern as OmpWebHostingExtensions so the
        // localized validation defaults stamped by
        // OmpValidationMetadataProvider resolve without duplicating them.
        options.DataAnnotationLocalizerProvider = static (_, factory) =>
            new OmpCompositeStringLocalizer(
                factory.Create(typeof(OpenModulePlatform.Auth.Localization.AuthResource)),
                factory.Create(typeof(SharedResource)));
    });

// DataAnnotations attributes without an explicit ErrorMessage (including the
// implicit Required for non-nullable reference types) get a localizable
// template stamped on, so their messages follow the request culture.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
{
    options.ModelMetadataDetailsProviders.Add(new OmpValidationMetadataProvider());

    // A stale antiforgery token reloads the page instead of surfacing the
    // framework's empty 400 (which middleboxes can re-frame into a lone "0"
    // page) - login tabs left open are the most common way to hit this.
    options.Filters.Add(new OpenModulePlatform.Web.Shared.Mvc.AntiforgeryFailureRedirectFilter());
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OpenModulePlatform.Auth.Services.LoginThrottleService>();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddScoped<OmpConfigurationService>();
builder.Services.AddScoped<RbacService>();
builder.Services.AddScoped<OmpBrandingService>();
builder.Services.AddSingleton(cultureSelectionService);
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
        new PreferredCultureRequestCultureProvider(authWebAppOptions, cultureSelectionService),
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});

var app = builder.Build();

app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
app.UseOmpSecurityHeaders();
// Login/logout round-trips are all redirects; without the explicit
// Content-Length: 0 they get chunk-framed and middleboxes can surface a
// lone "0" page.
app.UseOmpRedirectContentLength();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/localization/set-language", (
    HttpContext context,
    string culture,
    string? returnUrl,
    CultureSelectionService cultureSelection) =>
{
    var preferredCulture = cultureSelection.NormalizePreferredCulture(culture, authWebAppOptions);
    var effectiveCulture = cultureSelection.ResolveEffectiveCulture(preferredCulture, authWebAppOptions);
    cultureSelection.ApplyCookies(context.Response, preferredCulture, effectiveCulture);

    if (returnUrl is not null && IsSafeLocalReturnUrl(returnUrl))
    {
        return Results.LocalRedirect(returnUrl!);
    }

    return Results.LocalRedirect("/");
}).AllowAnonymous();

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

app.MapGet("/runtime-versions", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";

    return Results.Json(OmpRuntimeAssemblyVersionCheck.CreateReport());
    // Require authentication so the assembly version report is not exposed to
    // anonymous callers for platform fingerprinting (R3-F3).
}).RequireAuthorization();

app.MapPost("/logout", async (
    HttpContext context,
    IOptions<OmpAuthOptions> authOptions,
    OmpOidcProviderStatus currentOidcProviderStatus) =>
{
    var decision = OmpLogoutDecisionFactory.Create(
        context.User,
        authOptions.Value,
        currentOidcProviderStatus);

    await context.SignOutAsync(OmpAuthDefaults.AuthenticationScheme);

    if (decision.SignOutOidc)
    {
        return Results.SignOut(
            new AuthenticationProperties
            {
                RedirectUri = decision.RedirectUri
            },
            [OmpAuthDefaults.OidcAuthenticationScheme]);
    }

    return Results.LocalRedirect(decision.RedirectUri);
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
