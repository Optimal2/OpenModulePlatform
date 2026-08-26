namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Optional OpenID Connect settings for the central OMP Auth application.
/// </summary>
public sealed class OmpOidcOptions
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = Security.OmpAuthDefaults.OidcDefaultDisplayName;
    public string ProviderName { get; set; } = Security.OmpAuthDefaults.OidcProviderDisplayName;
    public string Authority { get; set; } = "";
    public string MetadataAddress { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string CallbackPath { get; set; } = Security.OmpAuthDefaults.OidcCallbackPath;
    public string ResponseType { get; set; } = "code";
    public string[] Scopes { get; set; } = ["openid", "profile"];

    /// <summary>
    /// When enabled (the default), SID claims from the provider are translated to
    /// their DOMAIN\name account form on the domain-joined auth server, and
    /// DOMAIN\name group claims are translated back to SIDs, so role rows match
    /// regardless of which form the provider sends. Translation is fail-safe: a
    /// failed lookup is logged and skipped, never fails the sign-in.
    /// </summary>
    public bool TranslateSidClaimsToAccountNames { get; set; } = true;

    public OmpOidcDiagnosticsOptions Diagnostics { get; set; } = new();
    public OmpOidcClaimTypeOptions ClaimTypes { get; set; } = new();
}

public sealed class OmpOidcDiagnosticsOptions
{
    /// <summary>
    /// Opt-in per-sign-in diagnostics. Default OFF. When enabled, each validated
    /// OIDC sign-in logs the incoming claim types, the value count per type and
    /// the resulting role principals. Needed because the sign-in replaces the
    /// provider principal with OMP's own and SaveTokens is false, so "the IdP
    /// sent nothing", "wrong claim type" and "wrong value form" otherwise look
    /// identical in the logs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Additionally logs raw claim values. Default OFF because values can
    /// contain personal data. Only meaningful together with <see cref="Enabled"/>.
    /// </summary>
    public bool IncludeClaimValues { get; set; }
}

public sealed class OmpOidcClaimTypeOptions
{
    public string ProviderUserKeyClaimType { get; set; } = "sub";
    public string UserIdClaimType { get; set; } = "sub";
    public string NameClaimType { get; set; } = "name";
    public string DisplayNameClaimType { get; set; } = "name";
    public string UserSidClaimType { get; set; } = "";
    public string UpnClaimType { get; set; } = "upn";
    public string SamAccountNameClaimType { get; set; } = "";
    public string DomainClaimType { get; set; } = "";
    public string GroupsClaimType { get; set; } = "groups";
    public string[] GroupClaimTypes { get; set; } = [];
    public string[] GroupSidClaimTypes { get; set; } = [];
    public string[] GroupNameClaimTypes { get; set; } = [];
}
