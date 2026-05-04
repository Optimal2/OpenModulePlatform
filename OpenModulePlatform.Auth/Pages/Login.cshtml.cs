// File: OpenModulePlatform.Auth/Pages/Login.cshtml.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Auth.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly OmpAuthRepository _repository;

    public LoginModel(OmpAuthRepository repository)
    {
        _repository = repository;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    [Required]
    public string UserName { get; set; } = "";

    [BindProperty]
    [Required]
    public string Password { get; set; } = "";

    public string WindowsLoginUrl { get; private set; } = "";

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? error)
    {
        if (User.Identity?.IsAuthenticated == true &&
            User.HasClaim(c => c.Type == OmpAuthDefaults.ProviderClaimType))
        {
            return RedirectToSafeReturnUrl();
        }

        ErrorMessage = error switch
        {
            "windows" => "Windows sign-in could not be resolved.",
            _ => null
        };

        BuildProviderUrls();
        return Page();
    }

    public async Task<IActionResult> OnPostLocalAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Enter a user name and password.";
            BuildProviderUrls();
            return Page();
        }

        var result = await _repository.ResolveLocalPasswordAsync(UserName, Password, ct);
        if (result.User is null)
        {
            ErrorMessage = result.Error ?? "The user name or password is incorrect.";
            BuildProviderUrls();
            return Page();
        }

        await SignInAsync(result.User);
        return RedirectToSafeReturnUrl();
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

    private void BuildProviderUrls()
    {
        var returnUrl = string.IsNullOrWhiteSpace(ReturnUrl) ? "/" : ReturnUrl;
        WindowsLoginUrl = Url.Content("~/ad") + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
    }
}
