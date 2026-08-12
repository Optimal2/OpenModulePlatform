// File: OpenModulePlatform.Web.Shared/Options/OmpAuthOptions.cs
namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Shared authentication settings for OMP web applications.
/// </summary>
public sealed class OmpAuthOptions
{
    public const string SectionName = "OmpAuth";

    public string CookieName { get; set; } = Security.OmpAuthDefaults.CookieName;
    public string LoginPath { get; set; } = Security.OmpAuthDefaults.LoginPath;
    public string LogoutPath { get; set; } = Security.OmpAuthDefaults.LogoutPath;
    public string AccessDeniedPath { get; set; } = Security.OmpAuthDefaults.AccessDeniedPath;
    public string ApplicationName { get; set; } = "OpenModulePlatform";

    /// <summary>
    /// Optional shared Data Protection key directory. All OMP web apps that
    /// read the shared auth cookie must use the same key ring.
    /// </summary>
    public string DataProtectionKeyPath { get; set; } = "";

    /// <summary>
    /// How the auth cookie's Secure flag is set: "always" (default; the cookie
    /// is only sent over HTTPS) or "sameAsRequest" (legacy; needed only when a
    /// TLS-terminating proxy speaks plain HTTP to the backend and forwarded
    /// headers are not trusted). Secure by default per R3-E5.
    /// </summary>
    public string CookieSecurePolicy { get; set; } = "always";

    /// <summary>
    /// On Windows, encrypt the Data Protection key ring at rest with DPAPI so a
    /// reader of the key directory cannot forge auth cookies (R3-E8). Default
    /// on; turn off only when the key ring must be portable across machines
    /// that cannot share a DPAPI context (then protect the directory by ACL).
    /// </summary>
    public bool ProtectKeysWithDpapi { get; set; } = true;

    /// <summary>
    /// Optional central OIDC/AD FS sign-in provider for OMP Auth.
    /// </summary>
    public OmpOidcOptions Oidc { get; set; } = new();
}
