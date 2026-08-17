// File: OpenModulePlatform.Web.Shared/Security/LocalPasswordIdentity.cs
namespace OpenModulePlatform.Web.Shared.Security;

public static class LocalPasswordIdentity
{
    public const string ProviderDisplayName = "lpwd";

    /// <summary>
    /// R7-F12: the binary collation every <c>omp.auth_provider_lpwd</c>
    /// <c>user_name</c> comparison is pinned to. Writes and reads both
    /// canonicalize with <see cref="NormalizeUserName"/> first, and the pinned
    /// collation then makes the comparison an exact ordinal match, so the
    /// database's default collation can no longer redefine the rule (a
    /// case-insensitive lookup could otherwise match a differently-cased row
    /// -- including the wrong one -- and a case-sensitive collation could make
    /// a legacy row unreachable).
    /// </summary>
    public const string UserNameBinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The single canonicalization rule for local password user names, applied
    /// on both sides of <c>omp.auth_provider_lpwd</c>: every write and every
    /// read/lookup (R7-F12).
    /// </summary>
    public static string NormalizeUserName(string userName)
        => userName.Trim().ToLowerInvariant();
}
