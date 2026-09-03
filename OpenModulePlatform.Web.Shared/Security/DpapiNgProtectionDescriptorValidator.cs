// File: OpenModulePlatform.Web.Shared/Security/DpapiNgProtectionDescriptorValidator.cs
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Validates a CNG DPAPI-NG protection descriptor eagerly, at startup, so a
/// typo in <c>OmpAuth:DpapiNgProtectionDescriptor</c> stops the application
/// with a clear message instead of surfacing later as an opaque key-ring
/// failure — or worse, being "helpfully" ignored while the ring falls back to
/// another protection scope.
/// </summary>
internal static class DpapiNgProtectionDescriptorValidator
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the descriptor is
    /// not usable. Validation is a local parse (it does not contact a domain
    /// controller): first a rule-grammar check that runs on every platform,
    /// then — on Windows — the authoritative NCryptCreateProtectionDescriptor
    /// call. NCrypt alone is too lenient (it accepts "SID=not-a-sid" at
    /// creation time and only fails later, at protect time), so the grammar
    /// check must come first.
    /// </summary>
    public static void ThrowIfInvalid(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            throw InvalidDescriptor(
                descriptor,
                "the descriptor is empty. Remove the setting to keep the legacy DPAPI scope " +
                "behavior, or set a rule such as \"SID=<domain group SID>\"");
        }

        ThrowIfGrammarInvalid(descriptor);

        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI-NG does not exist off Windows; the caller guards that too.
        }

        var status = NativeMethods.NCryptCreateProtectionDescriptor(descriptor, 0, out var handle);
        using (handle)
        {
            if (status != 0)
            {
                throw InvalidDescriptor(
                    descriptor,
                    $"NCryptCreateProtectionDescriptor rejected it with HRESULT 0x{status:X8}");
            }
        }
    }

    private static void ThrowIfGrammarInvalid(string descriptor)
    {
        foreach (var rule in SplitRules(descriptor))
        {
            var separatorIndex = rule.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= rule.Length - 1)
            {
                throw InvalidDescriptor(descriptor, $"rule \"{rule}\" is not in NAME=value form");
            }

            var name = rule[..separatorIndex].Trim().ToUpperInvariant();
            var value = rule[(separatorIndex + 1)..].Trim();

            switch (name)
            {
                case "SID":
                    try
                    {
                        _ = new SecurityIdentifier(value);
                    }
                    catch (ArgumentException)
                    {
                        throw InvalidDescriptor(descriptor, $"\"{value}\" is not a valid SID string");
                    }

                    break;

                case "CERTIFICATE":
                    if (!value.StartsWith("HashId:", StringComparison.OrdinalIgnoreCase) ||
                        !IsHex(value["HashId:".Length..]))
                    {
                        throw InvalidDescriptor(
                            descriptor,
                            $"certificate rule \"{value}\" must be HashId:<hex thumbprint>");
                    }

                    break;

                case "LOCAL":
                    if (!string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "machine", StringComparison.OrdinalIgnoreCase))
                    {
                        throw InvalidDescriptor(descriptor, "LOCAL must be \"user\" or \"machine\"");
                    }

                    break;

                default:
                    throw InvalidDescriptor(
                        descriptor,
                        $"unknown rule \"{name}\"; supported rules are SID, CERTIFICATE, and LOCAL");
            }
        }
    }

    private static IEnumerable<string> SplitRules(string descriptor)
    {
        // CNG protection descriptors join rules with " OR " / " AND ".
        return Regex.Split(
            descriptor,
            @"\s+(?:OR|AND)\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsHex(string value)
    {
        if (value.Length < 40)
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    private static InvalidOperationException InvalidDescriptor(string? descriptor, string reason)
    {
        return new InvalidOperationException(
            $"OmpAuth:DpapiNgProtectionDescriptor \"{descriptor}\" is not a valid CNG DPAPI-NG " +
            $"protection descriptor: {reason}. Expected rules such as \"SID=<domain group SID>\" " +
            "or \"CERTIFICATE=HashId:<thumbprint>\", optionally combined with AND/OR. The key " +
            "ring does NOT silently fall back to another protection scope: fix or remove the " +
            "setting and restart the application.");
    }

    private static class NativeMethods
    {
        // Bound once through NativeLibrary and Marshal.GetDelegateForFunctionPointer rather than
        // declared as extern P/Invoke methods: the validator runs on Windows only, at startup,
        // and binding by hand keeps the interop surface two plain delegates plus the SafeHandle.
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        internal delegate int NCryptCreateProtectionDescriptorFn(
            string pwszDescriptorString,
            uint dwFlags,
            out IntPtr phDescriptor);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int NCryptCloseProtectionDescriptorFn(IntPtr hDescriptor);

        private static readonly Lazy<(NCryptCreateProtectionDescriptorFn Create, NCryptCloseProtectionDescriptorFn Close)> Bindings =
            new(Bind, LazyThreadSafetyMode.ExecutionAndPublication);

        internal static int NCryptCreateProtectionDescriptor(
            string descriptor,
            uint flags,
            out SafeNCryptProtectionDescriptorHandle handle)
        {
            var status = Bindings.Value.Create(descriptor, flags, out var rawHandle);
            handle = new SafeNCryptProtectionDescriptorHandle(rawHandle);
            return status;
        }

        internal static int NCryptCloseProtectionDescriptor(IntPtr hDescriptor)
            => Bindings.Value.Close(hDescriptor);

        private static (NCryptCreateProtectionDescriptorFn Create, NCryptCloseProtectionDescriptorFn Close) Bind()
        {
            var ncrypt = NativeLibrary.Load("ncrypt.dll");
            return (
                Marshal.GetDelegateForFunctionPointer<NCryptCreateProtectionDescriptorFn>(
                    NativeLibrary.GetExport(ncrypt, "NCryptCreateProtectionDescriptor")),
                Marshal.GetDelegateForFunctionPointer<NCryptCloseProtectionDescriptorFn>(
                    NativeLibrary.GetExport(ncrypt, "NCryptCloseProtectionDescriptor")));
        }
    }

    private sealed class SafeNCryptProtectionDescriptorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeNCryptProtectionDescriptorHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.NCryptCloseProtectionDescriptor(handle) == 0;
        }
    }
}
