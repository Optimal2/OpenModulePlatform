namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Optional OpenID Connect settings for the central OMP Auth application.
/// </summary>
public sealed class OmpOidcOptions
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = Security.OmpAuthDefaults.OidcDefaultDisplayName;
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
    public string UserIdClaimType { get; set; } = "sub";
    public string NameClaimType { get; set; } = "name";
    public string DisplayNameClaimType { get; set; } = "name";
    public string GroupsClaimType { get; set; } = "groups";
}
