// File: OpenModulePlatform.Auth/Pages/Login.cshtml.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Auth.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly OmpAuthRepository _repository;
    private readonly WindowsPasswordAuthenticator _windowsPasswordAuthenticator;

    public LoginModel(
        OmpAuthRepository repository,
        WindowsPasswordAuthenticator windowsPasswordAuthenticator)
    {
        _repository = repository;
        _windowsPasswordAuthenticator = windowsPasswordAuthenticator;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string UserName { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string AlternateWindowsUserName { get; set; } = "";

    [BindProperty]
    public string AlternateWindowsPassword { get; set; } = "";

    public string WindowsLoginUrl { get; private set; } = "";

    public string? ErrorMessage { get; private set; }

    public bool ShowAlternateWindowsPrompt { get; private set; }

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
            "windowsAlternateUnavailable" => "Alternate Windows sign-in is not available on this host.",
            _ => null
        };

        BuildProviderUrls();
        return Page();
    }

    public async Task<IActionResult> OnPostLocalAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password))
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

    public async Task<IActionResult> OnPostAlternateWindowsAsync(CancellationToken ct)
    {
        ShowAlternateWindowsPrompt = true;
        AlternateWindowsUserName = AlternateWindowsUserName?.Trim() ?? string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            ErrorMessage = "Alternate Windows sign-in is not available on this host.";
            BuildProviderUrls();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(AlternateWindowsUserName) ||
            string.IsNullOrEmpty(AlternateWindowsPassword))
        {
            ErrorMessage = "Enter a Windows account name and password.";
            BuildProviderUrls();
            return Page();
        }

        var result = _windowsPasswordAuthenticator.Authenticate(
            AlternateWindowsUserName,
            AlternateWindowsPassword);

        if (!result.Succeeded || result.Principal is null)
        {
            ErrorMessage = "Windows credentials could not be validated.";
            BuildProviderUrls();
            return Page();
        }

        var user = await _repository.ResolveWindowsAsync(result.Principal, ct);
        if (user is null)
        {
            ErrorMessage = "Windows sign-in could not be resolved.";
            BuildProviderUrls();
            return Page();
        }

        await SignInAsync(user);
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
