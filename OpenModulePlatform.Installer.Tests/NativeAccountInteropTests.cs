using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenModulePlatform.Installer.Install;

namespace OpenModulePlatform.Installer.Tests;

public class NativeAccountInteropTests
{
    private static readonly Type LsaUnicodeStringType = typeof(ServiceLogonRight).GetNestedType(
        "LsaUnicodeString", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The production LSA string type was not found.");

    [Fact]
    public void Lsa_unicode_string_marshals_a_utf16_buffer_with_byte_lengths()
    {
        const string value = "ä权利-service";
        var instance = CreateLsaUnicodeString(value);
        var native = Marshal.AllocHGlobal(Marshal.SizeOf(LsaUnicodeStringType));
        try
        {
            Marshal.StructureToPtr(instance, native, fDeleteOld: false);

            var length = (ushort)Marshal.ReadInt16(native, 0);
            var maximumLength = (ushort)Marshal.ReadInt16(native, 2);
            var buffer = Marshal.ReadIntPtr(native, Marshal.OffsetOf(LsaUnicodeStringType, "Buffer").ToInt32());
            Assert.Equal(value.Length * sizeof(char), length);
            Assert.Equal(length + sizeof(char), maximumLength);
            Assert.NotEqual(IntPtr.Zero, buffer);

            // This prefix fits both the broken ANSI allocation and the required UTF-16
            // allocation, so the RED check never reads beyond either native buffer.
            var bytesToInspect = value.Length + 1;
            var actual = new byte[bytesToInspect];
            Marshal.Copy(buffer, actual, 0, actual.Length);
            var expected = Encoding.Unicode.GetBytes(value).Take(bytesToInspect).ToArray();
            Assert.Equal(expected, actual);
        }
        finally
        {
            Marshal.DestroyStructure(native, LsaUnicodeStringType);
            Marshal.FreeHGlobal(native);
        }
    }

    [Fact]
    public void Lsa_unicode_string_rejects_lengths_that_cannot_be_represented()
    {
        var exception = Record.Exception(() => CreateLsaUnicodeString(new string('x', 32767)));
        var unwrapped = exception is TargetInvocationException invocation
            ? invocation.InnerException
            : exception;
        Assert.IsType<ArgumentOutOfRangeException>(unwrapped);
    }

    [Fact]
    public void Embedded_nul_in_account_name_never_reaches_lookup_or_rights_grant()
    {
        var lookups = 0;
        var grants = 0;
        var result = ServiceLogonRight.EnsureGranted(
            "svc-omp\0ignored",
            out var detail,
            _ =>
            {
                lookups++;
                return [1, 2, 3];
            },
            (_, _) => grants++);

        Assert.False(result);
        Assert.Equal(0, lookups);
        Assert.Equal(0, grants);
        Assert.Contains("NUL", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_unicode_account_passes_to_lookup_and_rights_grant_unchanged()
    {
        const string account = @"CONTOSO\rätt-权利";
        var sid = new byte[] { 1, 2, 3 };
        string? lookedUp = null;
        byte[]? grantedSid = null;
        string? grantedRight = null;

        var result = ServiceLogonRight.EnsureGranted(
            account,
            out _,
            value =>
            {
                lookedUp = value;
                return sid;
            },
            (value, right) =>
            {
                grantedSid = value;
                grantedRight = right;
            });

        Assert.True(result);
        Assert.Equal(account, lookedUp);
        Assert.Same(sid, grantedSid);
        Assert.Equal(ServiceLogonRight.SeServiceLogonRight, grantedRight);
    }

    private static object CreateLsaUnicodeString(string value)
        => Activator.CreateInstance(
               LsaUnicodeStringType,
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
               binder: null,
               args: [value],
               culture: null)
           ?? throw new InvalidOperationException("The production LSA string could not be constructed.");
}
