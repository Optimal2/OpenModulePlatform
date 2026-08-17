using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F10. The OMP session cookie used to have no revocation checkpoint at all:
/// SlidingExpiration renewed the ticket on activity, so the "absolute" lifetime
/// slid forward forever and a disabled account's cookie lived as long as the
/// user kept clicking. These tests pin the two option-level halves of the fix:
/// the lifetime is truly absolute, and a validation hook exists that can reject
/// a revoked session.
/// </summary>
public sealed class OmpCookieSessionRevocationOptionsTests
{
    [Fact]
    public void CookieAuthentication_SessionLifetime_IsAbsoluteNotSliding()
    {
        var options = CreateCookieOptions();

        // With sliding renewal enabled the ticket's ExpiresUtc was pushed forward
        // on every active request, so the configured per-provider lifetime never
        // actually ended the session. The sign-in stamps ExpiresUtc once; no
        // request may move it.
        Assert.False(
            options.SlidingExpiration,
            "Sliding expiration must be off: renewal made the absolute session lifetime slide (R7-F10).");
    }

    [Fact]
    public async Task CookieAuthentication_ValidatePrincipalHook_IsInstalled()
    {
        var options = CreateCookieOptions();

        // The default CookieAuthenticationEvents.OnValidatePrincipal is a no-op.
        // A revoked session can only be denied when the hook actually does work,
        // so it must run the OMP session revocation validator.
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(OmpAuthDefaults.UserIdClaimType, "1")],
                OmpAuthDefaults.AuthenticationScheme));
        var context = new CookieValidatePrincipalContext(
            httpContext,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
                OmpAuthDefaults.AuthenticationScheme,
                null,
                typeof(Microsoft.AspNetCore.Authentication.IAuthenticationHandler)),
            options,
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
                principal,
                OmpAuthDefaults.AuthenticationScheme));

        await options.Events.OnValidatePrincipal(context);

        // User id 1 can never resolve without a database, so a real validation
        // hook must have rejected the principal; the pre-fix no-op left it
        // authenticated.
        Assert.False(
            context.Principal?.Identity?.IsAuthenticated == true,
            "OnValidatePrincipal must reject a session whose account state cannot be verified (R7-F10).");
    }

    private static CookieAuthenticationOptions CreateCookieOptions()
    {
        using var provider = CreateServices().BuildServiceProvider();
        return provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(OmpAuthDefaults.AuthenticationScheme);
    }

    private static IServiceCollection CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmpCookieAuthentication(configuration);
        return services;
    }
}
