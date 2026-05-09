// File: OpenModulePlatform.Auth/Services/WindowsPasswordAuthenticator.cs
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Auth.Services;

public sealed class WindowsPasswordAuthenticator
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32LogonNetworkCleartext = 8;
    private const int Logon32ProviderDefault = 0;
    private const int ErrorLogonTypeNotGranted = 1385;

    private readonly ILogger<WindowsPasswordAuthenticator> _log;

    public WindowsPasswordAuthenticator(ILogger<WindowsPasswordAuthenticator> log)
    {
        _log = log;
    }

    public WindowsPasswordAuthenticationResult Authenticate(string accountName, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsPasswordAuthenticationResult.Failed("Alternate Windows sign-in is only available on Windows.");
        }

        if (!TrySplitAccountName(accountName, out var userName, out var domain))
        {
            return WindowsPasswordAuthenticationResult.Failed("Enter a Windows account name.");
        }

        if (string.IsNullOrEmpty(password))
        {
            return WindowsPasswordAuthenticationResult.Failed("Enter a Windows password.");
        }

        if (!TryLogon(
            accountName,
            userName,
            domain,
            password,
            Logon32LogonInteractive,
            out var token,
            out var error) &&
            (error.NativeErrorCode != ErrorLogonTypeNotGranted ||
             !TryLogon(
                 accountName,
                 userName,
                 domain,
                 password,
                 Logon32LogonNetworkCleartext,
                 out token,
                 out error)))
        {
            return WindowsPasswordAuthenticationResult.Failed(error.Message);
        }

        try
        {
            var identity = new WindowsIdentity(token.DangerousGetHandle(), "WindowsPassword");
            return WindowsPasswordAuthenticationResult.Success(new WindowsPasswordAuthenticationTicket(token, identity));
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    private bool TryLogon(
        string accountName,
        string userName,
        string? domain,
        string password,
        int logonType,
        out SafeAccessTokenHandle token,
        out Win32Exception error)
    {
        if (LogonUser(
            userName,
            domain,
            password,
            logonType,
            Logon32ProviderDefault,
            out token))
        {
            error = new Win32Exception(0);
            return true;
        }

        error = new Win32Exception(Marshal.GetLastWin32Error());
        _log.LogWarning(
            "Alternate Windows sign-in validation failed for account '{AccountName}' using domain '{Domain}' and logon type {LogonType}. Win32 error {ErrorCode}: {ErrorMessage}",
            accountName,
            domain,
            logonType,
            error.NativeErrorCode,
            error.Message);

        return false;
    }

    private static bool TrySplitAccountName(
        string? accountName,
        out string userName,
        out string? domain)
    {
        userName = string.Empty;
        domain = null;

        accountName = accountName?.Trim();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        var slashIndex = accountName.IndexOf('\\');
        if (slashIndex >= 0)
        {
            domain = NormalizeLocalDomain(accountName[..slashIndex].Trim());
            userName = accountName[(slashIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(userName);
        }

        userName = accountName;
        domain = accountName.Contains('@', StringComparison.Ordinal) ? null : Environment.MachineName;
        return true;
    }

    private static string? NormalizeLocalDomain(string? domain)
        => string.Equals(domain, ".", StringComparison.Ordinal)
            ? Environment.MachineName
            : domain;

    // Password validation for the alternate AD sign-in path is intentionally delegated
    // to Windows. Keep the unmanaged boundary private and restrict DLL loading to
    // System32 so the P/Invoke cannot be redirected through the normal search path.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPasswordAuthenticationTicket : IDisposable
{
    private readonly SafeAccessTokenHandle _token;
    private readonly WindowsIdentity _identity;

    public WindowsPasswordAuthenticationTicket(SafeAccessTokenHandle token, WindowsIdentity identity)
    {
        _token = token;
        _identity = identity;
        Principal = new WindowsPrincipal(identity);
    }

    public ClaimsPrincipal Principal { get; }

    public void Dispose()
    {
        _identity.Dispose();
        _token.Dispose();
    }
}

public sealed record WindowsPasswordAuthenticationResult(
    WindowsPasswordAuthenticationTicket? Ticket,
    string? ErrorMessage)
{
    public bool Succeeded => Ticket is not null;

    public static WindowsPasswordAuthenticationResult Success(WindowsPasswordAuthenticationTicket ticket)
        => new(ticket, null);

    public static WindowsPasswordAuthenticationResult Failed(string errorMessage)
        => new(null, errorMessage);
}
