// File: OpenModulePlatform.Auth/Pages/Login.cshtml.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using OpenModulePlatform.Auth.Localization;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Auth.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private static readonly char[] ReturnUrlPathTerminators = ['?', '#'];

    private readonly OmpAuthRepository _repository;
    private readonly OmpBrandingService _brandingService;
    private readonly WindowsPasswordAuthenticator _windowsPasswordAuthenticator;
    private readonly IStringLocalizer<AuthResource> _localizer;

    public LoginModel(
        OmpAuthRepository repository,
        OmpBrandingService brandingService,
        WindowsPasswordAuthenticator windowsPasswordAuthenticator,
        IStringLocalizer<AuthResource> localizer)
    {
        _repository = repository;
        _brandingService = brandingService;
        _windowsPasswordAuthenticator = windowsPasswordAuthenticator;
        _localizer = localizer;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string UserName { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string RegisterUserName { get; set; } = "";

    [BindProperty]
    public string RegisterPassword { get; set; } = "";

    [BindProperty]
    public string RegisterConfirmPassword { get; set; } = "";

    [BindProperty]
    public string AlternateWindowsUserName { get; set; } = "";

    [BindProperty]
    public string AlternateWindowsPassword { get; set; } = "";

    public string WindowsLoginUrl { get; private set; } = "";

    public string? ErrorMessage { get; private set; }

    public bool ShowAlternateWindowsPrompt { get; private set; }

    public bool ShowOtherSignInOptions { get; private set; }

    public IActionResult OnGet(string? error)
    {
        if (User.Identity?.IsAuthenticated == true &&
            User.HasClaim(c => c.Type == OmpAuthDefaults.ProviderClaimType))
        {
            return RedirectToSafeReturnUrl();
        }

        ErrorMessage = error switch
        {
            "windows" => T("Windows sign-in could not be resolved."),
            "windowsAlternateUnavailable" => T("Alternate Windows sign-in is not available on this host."),
            _ => null
        };
        ShowOtherSignInOptions = error == "windowsAlternateUnavailable";

        BuildProviderUrls();
        return Page();
    }

    public async Task<IActionResult> OnPostLocalAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = T("Enter a user name and password.");
            ShowOtherSignInOptions = true;
            BuildProviderUrls();
            return Page();
        }

        var result = await _repository.ResolveLocalPasswordAsync(UserName, Password, ct);
        if (result.User is null)
        {
            ErrorMessage = await TWithBrandingAsync(result.Error ?? "The user name or password is incorrect.", ct);
            ShowOtherSignInOptions = true;
            BuildProviderUrls();
            return Page();
        }

        await SignInAsync(result.User);
        return RedirectToSafeReturnUrl();
    }

    public async Task<IActionResult> OnPostRegisterAsync(CancellationToken ct)
    {
        RegisterUserName = RegisterUserName?.Trim() ?? string.Empty;

        var validationError = ValidateRegistrationInput();
        if (validationError is not null)
        {
            ErrorMessage = T(validationError);
            ShowOtherSignInOptions = true;
            ClearRegistrationPasswordFields();
            BuildProviderUrls();
            return Page();
        }

        var result = await _repository.CreateLocalPasswordUserAsync(RegisterUserName, RegisterPassword, ct);
        if (result.User is null)
        {
            ErrorMessage = await TWithBrandingAsync(result.Error ?? "The OMP user account could not be created.", ct);
            ShowOtherSignInOptions = true;
            ClearRegistrationPasswordFields();
            BuildProviderUrls();
            return Page();
        }

        await SignInAsync(result.User);
        return RedirectToSafeReturnUrl();
    }

    public async Task<IActionResult> OnPostAlternateWindowsAsync(CancellationToken ct)
    {
        ShowAlternateWindowsPrompt = true;
        ShowOtherSignInOptions = true;
        AlternateWindowsUserName = AlternateWindowsUserName?.Trim() ?? string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            ErrorMessage = T("Alternate Windows sign-in is not available on this host.");
            BuildProviderUrls();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(AlternateWindowsUserName) ||
            string.IsNullOrEmpty(AlternateWindowsPassword))
        {
            ErrorMessage = T("Enter a Windows account name and password.");
            BuildProviderUrls();
            return Page();
        }

        var result = _windowsPasswordAuthenticator.Authenticate(
            AlternateWindowsUserName,
            AlternateWindowsPassword);

        if (!result.Succeeded || result.Principal is null)
        {
            ErrorMessage = T("Windows credentials could not be validated.");
            BuildProviderUrls();
            return Page();
        }

        var user = await _repository.ResolveWindowsAsync(result.Principal, ct);
        if (user is null)
        {
            ErrorMessage = T("Windows sign-in could not be resolved.");
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

    private void BuildProviderUrls()
    {
        var returnUrl = ResolveSafeReturnUrl();
        WindowsLoginUrl = Url.Content("~/ad") + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
    }

    private string? ValidateRegistrationInput()
    {
        if (string.IsNullOrWhiteSpace(RegisterUserName))
        {
            return "Enter a user name.";
        }

        if (RegisterUserName.Length > 256)
        {
            return "User name must be 256 characters or fewer.";
        }

        if (string.IsNullOrEmpty(RegisterPassword))
        {
            return "Password is required.";
        }

        if (RegisterPassword.Length < 8)
        {
            return "Password must be at least 8 characters.";
        }

        if (!string.Equals(RegisterPassword, RegisterConfirmPassword, StringComparison.Ordinal))
        {
            return "Password and confirmation password do not match.";
        }

        return null;
    }

    private void ClearRegistrationPasswordFields()
    {
        RegisterPassword = string.Empty;
        RegisterConfirmPassword = string.Empty;
    }

    private bool IsCurrentLoginUrl(string returnUrl)
    {
        var returnPath = ExtractPath(returnUrl);
        var currentLoginPath = string.Concat(Request.PathBase.Value, Request.Path.Value);

        return string.Equals(returnPath, currentLoginPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(returnPath, OmpAuthDefaults.LoginPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPath(string returnUrl)
    {
        var queryIndex = returnUrl.IndexOfAny(ReturnUrlPathTerminators);
        return queryIndex >= 0 ? returnUrl[..queryIndex] : returnUrl;
    }

    private string T(string key)
        => _localizer[key].Value;

    private async Task<string> TWithBrandingAsync(string key, CancellationToken ct)
    {
        var branding = await _brandingService.GetBrandingAsync(ct);
        return branding.ApplyTerms(T(key));
    }
}
