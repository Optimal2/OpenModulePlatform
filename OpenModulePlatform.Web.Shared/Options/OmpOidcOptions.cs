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
    public OmpOidcClaimTypeOptions ClaimTypes { get; set; } = new();
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
