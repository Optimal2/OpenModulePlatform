using Microsoft.Extensions.Caching.Memory;
using OpenModulePlatform.Auth.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F16. R5-F8 -- an infrastructure fault (domain controller unreachable,
/// auth provider disabled) is not a bad-credential attempt and must not count
/// toward the login lockout -- used to live only in the alternate-Windows
/// handler as a copied condition. The rule now lives in ONE place
/// (LoginThrottleService.RecordFailedAttempt) and every sign-in path routes
/// through it. These tests pin both the mechanism and the wiring, so
/// reintroducing a per-path copy or bypassing the central method fails the
/// build.
/// </summary>
public sealed class LoginThrottleInfrastructureErrorTests
{
    [Fact]
    public void RecordFailedAttempt_WhenInfrastructureError_NeverCountsTowardLockout()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new LoginThrottleService(cache);
        const string key = "adfs:example\\operator";
        const string clientAddress = "203.0.113.20";

        for (var i = 0; i < 20; i++)
        {
            throttle.RecordFailedAttempt(key, clientAddress, isInfrastructureError: true);
        }

        Assert.False(throttle.IsLockedOut(key));
        Assert.False(throttle.IsClientLockedOut(clientAddress));
    }

    [Fact]
    public void RecordFailedAttempt_WhenCredentialFailure_CountsTowardLockout()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new LoginThrottleService(cache);
        const string key = "local-operator";
        const string clientAddress = "203.0.113.21";

        for (var i = 0; i < 10; i++)
        {
            throttle.RecordFailedAttempt(key, clientAddress, isInfrastructureError: false);
        }

        Assert.True(throttle.IsLockedOut(key));
    }

    [Fact]
    public void LoginPage_AllSignInHandlers_RouteFailuresThroughTheCentralThrottleRule()
    {
        var page = ReadRepositoryTextFile("OpenModulePlatform.Auth", "Pages", "Login.cshtml.cs");

        // No handler may keep its own copy of the failure-counting logic or
        // bypass the central method: the local handler, the register handler
        // (validation failure and creation failure) and the alternate Windows
        // handler all route through RecordFailedAttempt.
        Assert.DoesNotContain("_loginThrottle.RecordFailure(", page);
        Assert.DoesNotContain("_loginThrottle.RecordClientFailure(", page);
        Assert.Equal(4, CountOccurrences(page, "_loginThrottle.RecordFailedAttempt("));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
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
