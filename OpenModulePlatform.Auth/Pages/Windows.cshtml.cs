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

    public WindowsModel(OmpAuthRepository repository)
    {
        _repository = repository;
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

        await SignInAsync(user);
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

    private Task SignInAsync(OmpAuthenticatedUser user)
    {
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(10)
        };

        return HttpContext.SignInAsync(
            OmpAuthDefaults.AuthenticationScheme,
            user.ToClaimsPrincipal(),
            properties);
    }

    private IActionResult RedirectToSafeReturnUrl()
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) &&
            Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return LocalRedirect("/");
    }

    private string GetWindowsChallengeScheme()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"))
            ? IISDefaults.AuthenticationScheme
            : NegotiateDefaults.AuthenticationScheme;

    private string BuildCurrentRelativeUrl()
        => string.Concat(Request.PathBase, Request.Path, Request.QueryString);
}
