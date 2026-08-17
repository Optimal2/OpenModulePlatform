using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;
using System.Reflection;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F15 follow-up. The unknown-user dummy hash in OmpAuthRepository is a
/// fixed literal, while the PBKDF2 work factor lives in the default parameter
/// of LocalPasswordHasher.Hash. Nothing bound the two together: raising the
/// hasher default would leave the dummy on the old work factor, making an
/// unknown user name measurably faster than a wrong password and silently
/// reopening the timing oracle the dummy was added to close. These guards pin
/// the dummy to the hasher's actual default -- read from method metadata, not
/// from a copy of the value -- so the build fails the moment the two diverge,
/// no matter which side moved.
/// </summary>
public sealed class UnknownUserDummyHashTests
{
    [Fact]
    public void DummyHash_IterationCountMatchesHasherDefault()
    {
        var dummyIterations = ParseDummyIterations(ReadDummyHash());
        var hasherDefaultIterations = ReadHasherDefaultIterations();

        Assert.True(
            dummyIterations == hasherDefaultIterations,
            $"The unknown-user dummy hash in OmpAuthRepository runs {dummyIterations} PBKDF2 " +
            $"iterations but LocalPasswordHasher.Hash defaults to {hasherDefaultIterations}. " +
            "Rebuild the dummy hash literal (or raise it) so an unknown user name costs the " +
            "same hashing work as a wrong password (R7-F15 timing oracle).");
    }

    [Fact]
    public void DummyHash_IsStructurallyValidForTheHasher()
    {
        var dummy = ReadDummyHash();
        var parts = dummy.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("PBKDF2-SHA256", parts[0]);
        // Verify() rejects hashes below its minimum iteration count before
        // doing any PBKDF2 work; the dummy must stay above that floor or the
        // unknown-user path becomes the cheap one again.
        Assert.True(ParseDummyIterations(dummy) >= 100_000);
        Assert.Equal(32, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);

        // A structurally valid dummy runs the full PBKDF2 path and simply
        // fails the comparison; it must never throw and never match.
        Assert.False(new LocalPasswordHasher().Verify("probe-password", dummy));
    }

    private static string ReadDummyHash()
    {
        var field = typeof(OmpAuthRepository).GetField(
            "UnknownUserDummyPasswordHash",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        return Assert.IsType<string>(field.GetRawConstantValue());
    }

    private static int ParseDummyIterations(string dummy)
    {
        var parts = dummy.Split('$');
        Assert.True(parts.Length >= 2, "Dummy hash must use the PBKDF2-SHA256$iterations$salt$hash layout.");
        return int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadHasherDefaultIterations()
    {
        var parameter = typeof(LocalPasswordHasher)
            .GetMethod(nameof(LocalPasswordHasher.Hash))!
            .GetParameters()
            .Single(parameter => parameter.Name == "iterations");

        return Assert.IsType<int>(parameter.DefaultValue);
    }
}
