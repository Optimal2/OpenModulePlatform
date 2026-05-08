// File: OpenModulePlatform.Auth/Services/WindowsPasswordAuthenticator.cs
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
    private const int Logon32ProviderDefault = 0;

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

        if (!LogonUser(
            userName,
            domain,
            password,
            Logon32LogonInteractive,
            Logon32ProviderDefault,
            out var token))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
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
            domain = accountName[..slashIndex].Trim();
            userName = accountName[(slashIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(userName);
        }

        userName = accountName;
        domain = accountName.Contains('@', StringComparison.Ordinal) ? null : ".";
        return true;
    }

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
