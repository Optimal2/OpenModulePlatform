using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Server.IISIntegration;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Pages;

[AllowAnonymous]
public sealed class WindowsModel : PageModel
{
    private readonly OmpAuthRepository _repository;
    private readonly OmpAuthenticationPropertiesFactory _authenticationPropertiesFactory;

    public WindowsModel(
        OmpAuthRepository repository,
        OmpAuthenticationPropertiesFactory authenticationPropertiesFactory)
    {
        _repository = repository;
        _authenticationPropertiesFactory = authenticationPropertiesFactory;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var windowsPrincipal = await TryAuthenticateWindowsAsync();
        if (windowsPrincipal?.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(windowsPrincipal.Identity.Name))
        {
            return Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = BuildCurrentRelativeUrl()
                },
                GetWindowsChallengeScheme());
        }

        var user = await _repository.ResolveWindowsAsync(windowsPrincipal, ct);
        if (user is null)
        {
            return LocalRedirect("/auth/login?error=windows");
        }

        await SignInAsync(user, ct);
        return RedirectToSafeReturnUrl();
    }

    private async Task<ClaimsPrincipal?> TryAuthenticateWindowsAsync()
    {
        foreach (var scheme in new[] { IISDefaults.AuthenticationScheme, NegotiateDefaults.AuthenticationScheme })
        {
            try
            {
                var result = await HttpContext.AuthenticateAsync(scheme);
                if (result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true)
                {
                    return result.Principal;
                }
            }
            catch (InvalidOperationException)
            {
                // The scheme is not available for this hosting mode.
            }
        }

        if (User.Identity?.IsAuthenticated == true &&
            !User.HasClaim(c => c.Type == OmpAuthDefaults.ProviderClaimType))
        {
            return User;
        }

        return null;
    }

    private async Task SignInAsync(OmpAuthenticatedUser user, CancellationToken ct)
    {
        var properties = await _authenticationPropertiesFactory.CreateAsync(user, ct);

        await HttpContext.SignInAsync(
            OmpAuthDefaults.AuthenticationScheme,
            user.ToClaimsPrincipal(),
            properties);
    }

    private IActionResult RedirectToSafeReturnUrl()
        => LocalRedirect(ResolveSafeReturnUrl());

    private string ResolveSafeReturnUrl()
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) &&
            Url.IsLocalUrl(ReturnUrl) &&
            !IsCurrentLoginUrl(ReturnUrl))
        {
            return ReturnUrl;
        }

        return "/";
    }

    private bool IsCurrentLoginUrl(string returnUrl)
    {
        var returnPath = ExtractPath(returnUrl);
        var currentLoginPath = string.Concat(Request.PathBase.Value, "/login");

        return string.Equals(returnPath, currentLoginPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(returnPath, OmpAuthDefaults.LoginPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPath(string returnUrl)
    {
        var queryIndex = returnUrl.IndexOfAny(new[] { '?', '#' });
        return queryIndex >= 0 ? returnUrl[..queryIndex] : returnUrl;
    }

    private string GetWindowsChallengeScheme()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"))
            ? IISDefaults.AuthenticationScheme
            : NegotiateDefaults.AuthenticationScheme;

    private string BuildCurrentRelativeUrl()
        => string.Concat(Request.PathBase, Request.Path, Request.QueryString);
}
