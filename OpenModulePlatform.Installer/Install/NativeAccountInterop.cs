using Microsoft.Win32.SafeHandles;
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
        => EnsureGranted(accountName, out detail, LookupAccountSid, AddAccountRight);

    internal static bool EnsureGranted(
        string accountName,
        out string detail,
        Func<string, byte[]> lookupAccountSid,
        Action<byte[], string> addAccountRight)
    {
        if (ContainsEmbeddedNul(accountName))
        {
            detail = "The account name contains an embedded NUL character.";
            return false;
        }

        try
        {
            var lookupName = NormalizeAccountNameForLookup(accountName, Environment.MachineName);
            var sid = lookupAccountSid(lookupName);
            addAccountRight(sid, SeServiceLogonRight);
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
    /// Account lookup historically did not resolve the <c>.\name</c> shorthand;
    /// retain the machine-qualified normalization for managed translation too.
    /// Other forms (bare name, DOMAIN\name, UPN) pass through.
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
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier));
            var binarySid = new byte[sid.BinaryLength];
            sid.GetBinaryForm(binarySid, 0);
            return binarySid;
        }
        catch (IdentityNotMappedException ex)
        {
            throw new InvalidOperationException(
                $"The account '{accountName}' could not be found ({ex.Message}).", ex);
        }
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
        using (policyHandle)
        {
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"LsaOpenPolicy failed (error {LsaNtStatusToWinError(status)}).");
            }

            var rights = new[] { new LsaUnicodeString(rightName) };
            status = LsaAddAccountRights(policyHandle, sid, rights, 1u);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"LsaAddAccountRights failed (error {LsaNtStatusToWinError(status)}).");
            }
        }
    }

    private const uint PolicyLookupNames = 0x00000800;
    private const uint PolicyCreateAccount = 0x00000010;

    private static bool ContainsEmbeddedNul(string? value) => value?.IndexOf('\0') >= 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Buffer;

        public LsaUnicodeString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 32766)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

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

    private sealed class LsaPolicyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private LsaPolicyHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => LsaClose(handle) == 0;
    }

    // CodeQL cs/unmanaged-code: kept - LSA policy access is required to grant the service logon right.
    [DllImport("advapi32.dll", EntryPoint = "LsaOpenPolicy", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint LsaOpenPolicy(
        [In] ref LsaUnicodeString systemName,
        [In] ref LsaObjectAttributes objectAttributes,
        uint desiredAccess,
        out LsaPolicyHandle policyHandle);

    // CodeQL cs/unmanaged-code: kept - granting SeServiceLogonRight requires the LSA account-rights API.
    [DllImport("advapi32.dll", EntryPoint = "LsaAddAccountRights", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint LsaAddAccountRights(
        LsaPolicyHandle policyHandle,
        [In] byte[] accountSid,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] LsaUnicodeString[] userRights,
        uint countOfRights);

    // CodeQL cs/unmanaged-code: kept - LSA NTSTATUS values require their designated Win32 error conversion.
    [DllImport("advapi32.dll", EntryPoint = "LsaNtStatusToWinError", ExactSpelling = true, CharSet = CharSet.None)]
    private static extern uint LsaNtStatusToWinError(uint status);

    // CodeQL cs/unmanaged-code: kept - an LSA policy handle must be released by its matching LSA close API.
    [DllImport("advapi32.dll", EntryPoint = "LsaClose", ExactSpelling = true, CharSet = CharSet.None)]
    private static extern uint LsaClose(IntPtr objectHandle);
}

/// <summary>Validates an account password with the Win32 LogonUser API.</summary>
public static class AccountPasswordValidator
{
    internal delegate bool LogonUserCall(
        string userName,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);

    public static bool Validate(string userName, string domain, string password, out string error)
        => Validate(userName, domain, password, out error, LogonUser);

    internal static bool Validate(
        string userName,
        string domain,
        string password,
        out string error,
        LogonUserCall logonUser)
    {
        if (ContainsEmbeddedNul(userName))
        {
            error = "The user name contains an embedded NUL character.";
            return false;
        }
        if (ContainsEmbeddedNul(domain))
        {
            error = "The domain contains an embedded NUL character.";
            return false;
        }
        if (ContainsEmbeddedNul(password))
        {
            error = "The password contains an embedded NUL character.";
            return false;
        }

        const int logon32LogonNetwork = 3;
        const int logon32ProviderDefault = 0;

        var ok = logonUser(
            userName,
            string.IsNullOrWhiteSpace(domain) ? null : domain,
            password,
            logon32LogonNetwork,
            logon32ProviderDefault,
            out var token);
        var win32Error = Marshal.GetLastWin32Error();
        using (token)
        {
            if (!ok)
            {
                error = new System.ComponentModel.Win32Exception(win32Error).Message;
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    private static bool ContainsEmbeddedNul(string? value) => value?.IndexOf('\0') >= 0;

    // CodeQL cs/unmanaged-code: kept - password validation requires LogonUser's Windows network-logon semantics.
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        [MarshalAs(UnmanagedType.LPWStr)] string userName,
        [MarshalAs(UnmanagedType.LPWStr)] string? domain,
        [MarshalAs(UnmanagedType.LPWStr)] string password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);
}
