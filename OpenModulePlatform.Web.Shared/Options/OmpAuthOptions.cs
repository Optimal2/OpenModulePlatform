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
    /// Scope for the DPAPI protection of the key ring. True (default) protects
    /// to the LOCAL MACHINE so every OMP app-pool/service account on the host
    /// can decrypt the shared keys — required when pools deliberately run as
    /// different accounts (a pool on its own account cannot decrypt a
    /// current-user-protected key and loops on /auth/login; measured at the customer
    /// Test 2026-08-19). False protects to the CURRENT USER, locking the ring
    /// to the account that created each key; use only when every OMP app pool
    /// runs as the same account and per-account isolation is wanted.
    /// </summary>
    public bool DpapiProtectToLocalMachine { get; set; } = true;

    /// <summary>
    /// Enforce server-side antiforgery-token validation on the shared topbar
    /// POST endpoints (favorites, notification/message mark-read). The
    /// endpoints are CSRF-protected by SameSite=Lax on the auth cookie either
    /// way, and the topbar forms always carry a token, so this is
    /// defence-in-depth (R3-E2). Off by default: enabling it rejects POSTs
    /// from pages cached before the tokens were introduced.
    /// </summary>
    public bool ValidateTopbarAntiforgery { get; set; }

    /// <summary>
    /// Optional central OIDC/AD FS sign-in provider for OMP Auth.
    /// </summary>
    public OmpOidcOptions Oidc { get; set; } = new();
}
