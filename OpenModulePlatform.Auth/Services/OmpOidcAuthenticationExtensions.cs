using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Auth.Services;

public static class OmpOidcAuthenticationExtensions
{
    public static OmpOidcProviderStatus AddOmpOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var authOptions = configuration
            .GetSection(OmpAuthOptions.SectionName)
            .Get<OmpAuthOptions>() ?? new OmpAuthOptions();

        var oidcOptions = authOptions.Oidc;
        var displayName = ResolveDisplayName(oidcOptions);
        var validationError = Validate(oidcOptions);
        if (!oidcOptions.Enabled || validationError is not null)
        {
            services.AddSingleton(new OmpOidcProviderStatus
            {
                IsEnabled = false,
                DisplayName = displayName
            });

            services.AddSingleton<IStartupFilter>(
                new OmpOidcStartupLoggingFilter(oidcOptions.Enabled, validationError));

            return new OmpOidcProviderStatus
            {
                IsEnabled = false,
                DisplayName = displayName
            };
        }

        services.AddAuthentication()
            .AddOpenIdConnect(OmpAuthDefaults.OidcAuthenticationScheme, options =>
            {
                options.SignInScheme = OmpAuthDefaults.AuthenticationScheme;
                options.Authority = string.IsNullOrWhiteSpace(oidcOptions.Authority)
                    ? null
                    : oidcOptions.Authority.Trim();
                options.MetadataAddress = string.IsNullOrWhiteSpace(oidcOptions.MetadataAddress)
                    ? null
                    : oidcOptions.MetadataAddress.Trim();
                options.ClientId = oidcOptions.ClientId.Trim();
                options.ClientSecret = oidcOptions.ClientSecret;
                options.CallbackPath = NormalizeCallbackPath(oidcOptions.CallbackPath);
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = NormalizeClaimType(
                    oidcOptions.ClaimTypes.NameClaimType,
                    "name");
                options.TokenValidationParameters.RoleClaimType = NormalizeClaimType(
                    oidcOptions.ClaimTypes.GroupsClaimType,
                    "groups");

                options.Scope.Clear();
                foreach (var scope in ResolveScopes(oidcOptions.Scopes))
                {
                    options.Scope.Add(scope);
                }

                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenModulePlatform.Auth.Oidc");
                        var monitor = context.HttpContext.RequestServices
                            .GetRequiredService<IOptionsMonitor<OmpAuthOptions>>();
                        var currentOptions = monitor.CurrentValue.Oidc;
                        var resolvedClaims = context.Principal is null
                            ? null
                            : OmpOidcClaimResolver.Resolve(context.Principal, currentOptions);
                        if (resolvedClaims is null)
                        {
                            logger.LogWarning(
                                "OIDC sign-in failed because the configured user id claim was not present.");
                            RedirectToLogin(context.HttpContext, "oidc");
                            context.HandleResponse();
                            return;
                        }

                        var repository = context.HttpContext.RequestServices
                            .GetRequiredService<OmpAuthRepository>();
                        var user = await repository.ResolveOidcAsync(
                            resolvedClaims,
                            context.HttpContext.RequestAborted);
                        if (user is null)
                        {
                            logger.LogWarning(
                                "OIDC sign-in for provider user key hash {ProviderUserKeyHash} could not be resolved.",
                                CreateLogHash(resolvedClaims.ProviderUserKey));
                            RedirectToLogin(context.HttpContext, "oidc");
                            context.HandleResponse();
                            return;
                        }

                        context.Principal = user.ToClaimsPrincipal();
                        context.Properties ??= new AuthenticationProperties();
                        var propertiesFactory = context.HttpContext.RequestServices
                            .GetRequiredService<OmpAuthenticationPropertiesFactory>();
                        await propertiesFactory.ApplyAsync(
                            context.Properties,
                            user,
                            context.HttpContext.RequestAborted);
                    },
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenModulePlatform.Auth.Oidc");
                        logger.LogWarning(
                            "OIDC authentication failed with {ExceptionType}.",
                            context.Exception.GetType().Name);
                        RedirectToLogin(context.HttpContext, "oidc");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenModulePlatform.Auth.Oidc");
                        logger.LogWarning(
                            "OIDC remote authentication failed with {FailureType}.",
                            context.Failure?.GetType().Name ?? "Unknown");
                        RedirectToLogin(context.HttpContext, "oidc");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        if (!string.IsNullOrWhiteSpace(context.ProtocolMessage.IssuerAddress))
                        {
                            return Task.CompletedTask;
                        }

                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("OpenModulePlatform.Auth.Oidc");
                        var redirectUri = NormalizeLocalRedirectUri(
                            context.Properties?.RedirectUri,
                            OmpAuthDefaults.LoginPath);

                        logger.LogInformation(
                            "OIDC sign-out endpoint is not available in provider metadata; completed local OMP logout only.");
                        context.Response.Redirect(redirectUri);
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            });

        var status = new OmpOidcProviderStatus
        {
            IsEnabled = true,
            DisplayName = displayName
        };
        services.AddSingleton(status);
        return status;
    }

    private static string? Validate(OmpOidcOptions options)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.Authority) &&
            string.IsNullOrWhiteSpace(options.MetadataAddress))
        {
            return "OmpAuth:Oidc requires Authority or MetadataAddress.";
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return "OmpAuth:Oidc requires ClientId.";
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return "OmpAuth:Oidc requires ClientSecret.";
        }

        if (!string.Equals(options.ResponseType?.Trim(), OpenIdConnectResponseType.Code, StringComparison.OrdinalIgnoreCase))
        {
            return "OmpAuth:Oidc only supports authorization-code response type.";
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveScopes(IReadOnlyList<string>? configuredScopes)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "openid"
        };

        foreach (var scope in (configuredScopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim()))
        {
            scopes.Add(scope);
        }

        return scopes.ToList();
    }

    private static string ResolveDisplayName(OmpOidcOptions options)
        => string.IsNullOrWhiteSpace(options.DisplayName)
            ? OmpAuthDefaults.OidcDefaultDisplayName
            : options.DisplayName.Trim();

    private static string NormalizeCallbackPath(string? configuredPath)
        => !string.IsNullOrWhiteSpace(configuredPath) &&
           configuredPath.Trim().StartsWith("/", StringComparison.Ordinal) &&
           !configuredPath.Contains('\\', StringComparison.Ordinal)
            ? configuredPath.Trim()
            : OmpAuthDefaults.OidcCallbackPath;

    private static string NormalizeClaimType(string? claimType, string fallback)
        => string.IsNullOrWhiteSpace(claimType) ? fallback : claimType.Trim();

    private static string NormalizeLocalRedirectUri(string? redirectUri, string fallback)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) ||
            !Uri.IsWellFormedUriString(redirectUri, UriKind.Relative) ||
            !redirectUri.StartsWith("/", StringComparison.Ordinal) ||
            redirectUri.StartsWith("//", StringComparison.Ordinal) ||
            redirectUri.Contains('\\', StringComparison.Ordinal))
        {
            return fallback;
        }

        try
        {
            var unescaped = Uri.UnescapeDataString(redirectUri);
            return !unescaped.StartsWith("//", StringComparison.Ordinal) &&
                   !unescaped.Contains('\\', StringComparison.Ordinal)
                ? redirectUri
                : fallback;
        }
        catch (UriFormatException)
        {
            return fallback;
        }
    }

    private static void RedirectToLogin(HttpContext context, string error)
    {
        context.Response.Redirect(QueryHelpers.AddQueryString(OmpAuthDefaults.LoginPath, "error", error));
    }

    private static string CreateLogHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private sealed class OmpOidcStartupLoggingFilter : IStartupFilter
    {
        private readonly bool _enabled;
        private readonly string? _validationError;

        public OmpOidcStartupLoggingFilter(bool enabled, string? validationError)
        {
            _enabled = enabled;
            _validationError = validationError;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                if (_enabled && !string.IsNullOrWhiteSpace(_validationError))
                {
                    var logger = app.ApplicationServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("OpenModulePlatform.Auth.Oidc");
                    logger.LogWarning(
                        "OIDC sign-in is disabled due to invalid configuration: {ValidationError}",
                        _validationError);
                }

                next(app);
            };
    }
}
