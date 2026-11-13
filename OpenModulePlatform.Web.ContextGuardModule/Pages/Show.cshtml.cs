using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.ContextGuardModule.Pages;

public sealed class ShowModel(
    ContextGuardService service,
    IOptions<ContextGuardOptions> options,
    IAntiforgery antiforgery) : PageModel
{
    public const string ClientCookieName = ".omp-context-guard-client";

    public GuardPageModel? Guard { get; private set; }
    public HandoffRedemptionStatus? Error { get; private set; }
    public string HeartbeatUrl { get; private set; } = string.Empty;
    public string AntiforgeryToken { get; private set; } = string.Empty;
    public int HeartbeatIntervalMilliseconds => options.Value.HeartbeatIntervalSeconds * 1000;
    public int HeartbeatFailureLimit => options.Value.HeartbeatFailureLimit;

    public IActionResult OnGet(string? k)
    {
        Response.Headers.CacheControl = "no-store";
        if (OmpUserIdentity.TryGetOmpUserId(User) is not int userId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(k))
        {
            Error = HandoffRedemptionStatus.Unknown;
            return Page();
        }

        var result = service.Redeem(k, userId, ResolveClientId());
        if (result.Status != HandoffRedemptionStatus.Success || result.Redeemed is null)
        {
            Error = result.Status;
            return Page();
        }

        var context = result.Redeemed.Context;
        var profile = service.GetProfile(context.ProfileKey);
        Guard = new GuardPageModel(
            context.ContextId,
            context.ProfileKey,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? context.ProfileKey : profile.DisplayName,
            ContextGuardService.GetCorrelation(profile, result.Redeemed.Fields),
            profile.TargetUrl,
            profile.Method.ToUpperInvariant(),
            profile.Delivery,
            result.Redeemed.Fields);
        HeartbeatUrl = Url.Content($"~/context/{context.ContextId}/heartbeat");
        AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
        return Page();
    }

    private string ResolveClientId()
    {
        if (!string.Equals(options.Value.Scope, ContextGuardScopes.UserAndClient, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (Request.Cookies.TryGetValue(ClientCookieName, out var existing)
            && Guid.TryParse(existing, out _))
        {
            return existing;
        }

        var clientId = Guid.NewGuid().ToString("N");
        Response.Cookies.Append(ClientCookieName, clientId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/",
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        return clientId;
    }
}
