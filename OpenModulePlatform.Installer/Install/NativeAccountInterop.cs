using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenModulePlatform.Installer.Install;

/// <summary>
/// Grants a Windows account the "Log on as a service" user right
/// (SeServiceLogonRight) through the LSA policy API. sc.exe does not grant
/// this right, and without it a service configured with a domain or local
/// account cannot start.
/// </summary>
public static class ServiceLogonRight
{
    public const string SeServiceLogonRight = "SeServiceLogonRight";

    /// <summary>Returns true when the right is in place for the account after the call.</summary>
    public static bool EnsureGranted(string accountName, out string detail)
    {
        try
        {
            var lookupName = NormalizeAccountNameForLookup(accountName, Environment.MachineName);
            var sid = LookupAccountSid(lookupName);
            AddAccountRight(sid, SeServiceLogonRight);
            detail = $"{accountName} now has the 'Log on as a service' right.";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// LookupAccountName does not resolve the <c>.\name</c> shorthand (it fails
    /// with error 1332, "no mapping between account names and security IDs");
    /// the machine-qualified form <c>MACHINE\name</c> names the same account and
    /// resolves. Other forms (bare name, DOMAIN\name, UPN) pass through.
    /// </summary>
    internal static string NormalizeAccountNameForLookup(string accountName, string machineName)
    {
        var trimmed = (accountName ?? string.Empty).Trim();
        if (!trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var machine = string.IsNullOrWhiteSpace(machineName)
            ? Environment.MachineName
            : machineName.Trim();
        return machine + trimmed[1..];
    }

    internal static byte[] LookupAccountSid(string accountName)
    {
        var sid = new byte[68];
        var sidLength = sid.Length;
        var domainName = new System.Text.StringBuilder(256);
        var domainLength = domainName.Capacity;
        if (!LookupAccountName(
                null,
                accountName,
                sid,
                ref sidLength,
                domainName,
                ref domainLength,
                out _))
        {
            throw new InvalidOperationException(
                $"The account '{accountName}' could not be found (error {Marshal.GetLastWin32Error()}).");
        }

        Array.Resize(ref sid, sidLength);
        return sid;
    }

    internal static void AddAccountRight(byte[] sid, string rightName)
    {
        var systemName = new LsaUnicodeString(string.Empty);
        var objectAttributes = new LsaObjectAttributes
        {
            Length = Marshal.SizeOf<LsaObjectAttributes>()
        };

        var status = LsaOpenPolicy(
            ref systemName,
            ref objectAttributes,
            PolicyLookupNames | PolicyCreateAccount,
            out var policyHandle);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"LsaOpenPolicy failed (error {LsaNtStatusToWinError(status)}).");
        }

        try
        {
            var rights = new[] { new LsaUnicodeString(rightName) };
            status = LsaAddAccountRights(policyHandle, sid, rights, 1);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"LsaAddAccountRights failed (error {LsaNtStatusToWinError(status)}).");
            }
        }
        finally
        {
            LsaClose(policyHandle);
        }
    }

    private const uint PolicyLookupNames = 0x00000800;
    private const uint PolicyCreateAccount = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public string Buffer;

        public LsaUnicodeString(string value)
        {
            Buffer = value;
            Length = (ushort)(value.Length * sizeof(char));
            MaximumLength = (ushort)(Length + sizeof(char));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        byte[] sid,
        ref int sidSize,
        System.Text.StringBuilder referencedDomainName,
        ref int referencedDomainNameSize,
        out int use);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint LsaOpenPolicy(
        ref LsaUnicodeString systemName,
        ref LsaObjectAttributes objectAttributes,
        uint desiredAccess,
        out IntPtr policyHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint LsaAddAccountRights(
        IntPtr policyHandle,
        byte[] accountSid,
        LsaUnicodeString[] userRights,
        int countOfRights);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    private static extern int LsaClose(IntPtr objectHandle);
}

/// <summary>Validates an account password with the Win32 LogonUser API.</summary>
public static class AccountPasswordValidator
{
    public static bool Validate(string userName, string domain, string password, out string error)
    {
        const int logon32LogonNetwork = 3;
        const int logon32ProviderDefault = 0;

        var ok = LogonUser(
            userName,
            string.IsNullOrWhiteSpace(domain) ? null : domain,
            password,
            logon32LogonNetwork,
            logon32ProviderDefault,
            out var token);
        if (!ok)
        {
            var win32Error = Marshal.GetLastWin32Error();
            error = new System.ComponentModel.Win32Exception(win32Error).Message;
            return false;
        }

        error = string.Empty;
        CloseHandle(token);
        return true;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string userName,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
