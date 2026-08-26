using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Principal;

namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Translates between SID and DOMAIN\name account forms for OIDC sign-ins.
/// </summary>
/// <remarks>
/// The ADFS/OIDC path receives whatever claim form the provider happens to send,
/// while role rows in <c>omp.RolePrincipals</c> can be stored in either form.
/// The Windows/Negotiate path already stores both forms for every account and
/// group (RbacService translates both ways); this translator gives the OIDC
/// path the same reach. Implementations must never throw and must cache per
/// sign-in so one lookup per distinct value is made at most.
/// </remarks>
public interface IOmpSidAccountTranslator
{
    /// <summary>Returns the DOMAIN\name form of the SID, or null when it cannot be resolved.</summary>
    string? TryTranslateSidToAccountName(string sid);

    /// <summary>Returns the SID form of a DOMAIN\name account, or null when it cannot be resolved.</summary>
    string? TryTranslateAccountNameToSid(string accountName);
}

/// <summary>
/// <see cref="IOmpSidAccountTranslator"/> backed by Windows account translation
/// on the (domain-joined) auth server. Fail-safe by contract: every lookup
/// failure is logged and returned as null so a directory problem can never
/// break a sign-in. Results are cached per instance; create one instance per
/// sign-in so a single sign-in does at most one directory lookup per claim.
/// </summary>
public sealed class WindowsOmpSidAccountTranslator : IOmpSidAccountTranslator
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string?> _sidToName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _nameToSid = new(StringComparer.OrdinalIgnoreCase);

    public WindowsOmpSidAccountTranslator(ILogger logger)
    {
        _logger = logger;
    }

    public string? TryTranslateSidToAccountName(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        return _sidToName.GetOrAdd(sid.Trim(), TranslateSidCore);
    }

    public string? TryTranslateAccountNameToSid(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        return _nameToSid.GetOrAdd(accountName.Trim(), TranslateNameCore);
    }

    private string? TranslateSidCore(string sid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return (new SecurityIdentifier(sid).Translate(typeof(NTAccount)) as NTAccount)?.Value;
        }
        catch (SystemException ex)
        {
            _logger.LogDebug(
                ex,
                "OIDC sign-in: SID {Sid} could not be translated to an account name; continuing without it.",
                sid);
            return null;
        }
    }

    private string? TranslateNameCore(string accountName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return (new NTAccount(accountName).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        }
        catch (SystemException ex)
        {
            _logger.LogDebug(
                ex,
                "OIDC sign-in: account name {AccountName} could not be translated to a SID; continuing without it.",
                accountName);
            return null;
        }
    }
}
