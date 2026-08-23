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
    /// reader of the key directory cannot forge auth cookies (R3-E8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default off since 2026-08-23 (operator decision).</b> Machine-scoped DPAPI
    /// ties the ring to the host that wrote it, which repeatedly cost working
    /// installations their sign-in when app pools ran as different accounts or when a
    /// key ring moved between nodes. Until the AD security group holding the servers
    /// and service accounts exists, the key directory is protected by NTFS
    /// permissions instead: grant only the app-pool identities read access and keep
    /// the parent directory off any share.
    /// </para>
    /// <para>
    /// This is a deliberate, temporary trade: an unencrypted ring means anyone who can
    /// READ the key directory can forge auth cookies, so the file permissions are the
    /// whole control. When the AD group is ready, set
    /// <see cref="DpapiNgProtectionDescriptor"/> to <c>SID=&lt;group SID&gt;</c> — that
    /// is AD-backed, works across every domain-joined node, and takes precedence over
    /// this flag. Re-enabling <see cref="ProtectKeysWithDpapi"/> instead brings back the
    /// single-host limitation that caused the original problem.
    /// </para>
    /// <para>
    /// The value must MATCH across every OMP app and node sharing a key ring; a key
    /// written under one setting cannot be read under another.
    /// </para>
    /// </remarks>
    public bool ProtectKeysWithDpapi { get; set; } = false;

    /// <summary>
    /// Scope for the DPAPI protection of the key ring. True (default) protects
    /// to the LOCAL MACHINE so every OMP app-pool/service account on the host
    /// can decrypt the shared keys — required when pools deliberately run as
    /// different accounts (a pool on its own account cannot decrypt a
    /// current-user-protected key and loops on /auth/login; measured in a
    /// customer test environment 2026-08-19). False protects to the CURRENT USER, locking the ring
    /// to the account that created each key; use only when every OMP app pool
    /// runs as the same account and per-account isolation is wanted.
    /// </summary>
    public bool DpapiProtectToLocalMachine { get; set; } = true;

    /// <summary>
    /// Optional CNG DPAPI-NG protection descriptor for the Data Protection key
    /// ring, for example "SID=&lt;domain group SID&gt;". When set (and non-empty)
    /// it takes precedence over the DPAPI scope choice: the key ring is protected
    /// with ProtectKeysWithDpapiNG, which is backed by Active Directory and can
    /// be decrypted on every domain-joined node by the principals named in the
    /// descriptor — the supported answer for load-balanced farms and for hosts
    /// whose app pools run as different domain accounts. An invalid descriptor
    /// fails startup loudly; there is never a silent fallback to another scope.
    /// Empty (default) keeps the legacy DPAPI behavior governed by
    /// <see cref="ProtectKeysWithDpapi"/> and <see cref="DpapiProtectToLocalMachine"/>.
    /// </summary>
    public string DpapiNgProtectionDescriptor { get; set; } = "";

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
