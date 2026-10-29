using System.Reflection;
using Microsoft.Win32.SafeHandles;
using OpenModulePlatform.Installer.Install;

namespace OpenModulePlatform.Installer.Tests;

public class AccountPasswordValidatorTests
{
    [Theory]
    [InlineData("user\0ignored", "CONTOSO", "password")]
    [InlineData("user", "CONTOSO\0ignored", "password")]
    [InlineData("user", "CONTOSO", "password\0ignored")]
    public void Embedded_nul_never_reaches_the_native_logon_boundary(
        string userName, string domain, string password)
    {
        var calls = 0;
        var result = AccountPasswordValidator.Validate(
            userName,
            domain,
            password,
            out var error,
            FakeLogon);

        Assert.False(result);
        Assert.Equal(0, calls);
        Assert.Contains("NUL", error, StringComparison.Ordinal);

        bool FakeLogon(
            string user, string? logonDomain, string secret, int logonType, int logonProvider,
            out SafeAccessTokenHandle token)
        {
            calls++;
            token = new SafeAccessTokenHandle(IntPtr.Zero);
            return false;
        }
    }

    [Fact]
    public void Valid_unicode_credentials_and_password_spaces_pass_unchanged()
    {
        const string userName = "rätt-权利";
        const string domain = "CONTOSO";
        const string password = "  päss-权利  ";
        string? observedUserName = null;
        string? observedDomain = null;
        string? observedCredential = null;
        SafeAccessTokenHandle? returnedToken = null;

        var result = AccountPasswordValidator.Validate(
            userName,
            domain,
            password,
            out var error,
            FakeLogon);

        Assert.True(result);
        Assert.True(observedUserName == userName);
        Assert.True(observedDomain == domain);
        Assert.True(observedCredential == password);
        Assert.NotNull(returnedToken);
        Assert.True(returnedToken.IsClosed);
        Assert.Equal(string.Empty, error);

        bool FakeLogon(
            string user, string? logonDomain, string secret, int logonType, int logonProvider,
            out SafeAccessTokenHandle token)
        {
            observedUserName = user;
            observedDomain = logonDomain;
            observedCredential = secret;
            token = new SafeAccessTokenHandle(IntPtr.Zero);
            returnedToken = token;
            return true;
        }
    }

    [Fact]
    public void Failed_logon_disposes_any_returned_token_and_returns_an_error()
    {
        SafeAccessTokenHandle? returnedToken = null;
        var result = AccountPasswordValidator.Validate(
            "user",
            "CONTOSO",
            "password",
            out var error,
            FakeLogon);

        Assert.False(result);
        Assert.NotNull(returnedToken);
        Assert.True(returnedToken.IsClosed);
        Assert.NotEqual(string.Empty, error);

        bool FakeLogon(
            string user, string? logonDomain, string secret, int logonType, int logonProvider,
            out SafeAccessTokenHandle token)
        {
            token = new SafeAccessTokenHandle(IntPtr.Zero);
            returnedToken = token;
            return false;
        }
    }

    [Fact]
    public void Native_logon_contract_returns_a_safe_access_token_owner()
    {
        var method = typeof(AccountPasswordValidator).GetMethod(
            "LogonUser", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(
            typeof(SafeAccessTokenHandle),
            method.GetParameters()[^1].ParameterType.GetElementType());
    }
}
