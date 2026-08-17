using Microsoft.Extensions.Caching.Memory;
using OpenModulePlatform.Auth.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F13. Sign-in had a throttle (per user name plus per client address);
/// account registration had none at all -- not even the IP key -- so account
/// creation and user-name probing through the register handler were unbounded.
/// The fix reuses the sign-in throttle family (LoginThrottleService) on the
/// register handler. These tests pin both the mechanism and the wiring, so
/// removing the throttle from the register handler fails the build.
/// </summary>
public sealed class AuthRegistrationThrottleTests
{
    [Fact]
    public void RegisterKey_LocksOutAfterRepeatedFailures()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new LoginThrottleService(cache);
        var key = "register:lock-me-out";

        for (var i = 0; i < 9; i++)
        {
            throttle.RecordFailure(key);
            Assert.False(throttle.IsLockedOut(key));
        }

        throttle.RecordFailure(key);
        Assert.True(throttle.IsLockedOut(key));
    }

    [Fact]
    public void ClientAddress_LocksOutAfterRepeatedFailuresFromSameIp()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new LoginThrottleService(cache);
        const string clientAddress = "203.0.113.10";

        for (var i = 0; i < 49; i++)
        {
            throttle.RecordClientFailure(clientAddress);
            Assert.False(throttle.IsClientLockedOut(clientAddress));
        }

        throttle.RecordClientFailure(clientAddress);
        Assert.True(throttle.IsClientLockedOut(clientAddress));
    }

    [Fact]
    public void ClientAddress_LockoutDoesNotImplicateOtherAddresses()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new LoginThrottleService(cache);

        for (var i = 0; i < 50; i++)
        {
            throttle.RecordClientFailure("203.0.113.10");
        }

        Assert.True(throttle.IsClientLockedOut("203.0.113.10"));
        Assert.False(throttle.IsClientLockedOut("203.0.113.11"));
    }

    [Fact]
    public void LoginPage_RegisterHandler_UsesSignInThrottleFamily()
    {
        var page = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Pages", "Login.cshtml.cs");
        var registerHandler = ExtractBetween(
            page,
            "public async Task<IActionResult> OnPostRegisterAsync",
            "public async Task<IActionResult> OnPostAlternateWindowsAsync");

        // The register handler must consult the same throttle service as sign-in,
        // keyed per registration name AND per client address, and must record
        // failures on both buckets so repeated attempts from one IP are throttled.
        Assert.Contains("_loginThrottle.IsLockedOut(", registerHandler);
        Assert.Contains("_loginThrottle.IsClientLockedOut(clientAddress)", registerHandler);
        Assert.Contains("_loginThrottle.RecordFailure(", registerHandler);
        Assert.Contains("_loginThrottle.RecordClientFailure(clientAddress)", registerHandler);
        Assert.Contains("_loginThrottle.RecordSuccess(", registerHandler);
    }

    private static string ExtractBetween(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find start marker '{start}'.");

        startIndex += start.Length;
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Could not find end marker '{end}'.");

        return value[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
