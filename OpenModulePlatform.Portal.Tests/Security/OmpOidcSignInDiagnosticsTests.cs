using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 1 steps 4-5:
/// opt-in sign-in diagnostics (default OFF, claim values only when explicitly
/// enabled) and a warn-once report for configured claim types that were absent
/// from the validated sign-in.
/// </summary>
public sealed class OmpOidcSignInDiagnosticsTests
{
    [Fact]
    public void LogSignIn_DiagnosticsDisabled_LogsNothing()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("unique_name", @"CONTOSO\anna"));
        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        OmpOidcSignInDiagnostics.LogSignIn(
            logger,
            principal,
            resolved!,
            ["ADUser|CONTOSO\\anna"],
            new OmpOidcDiagnosticsOptions());

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void LogSignIn_Enabled_LogsClaimTypesAndCountsButNotValues()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("groups", "S-1-5-21-11-22-33-2001"),
            new Claim("groups", "S-1-5-21-11-22-33-2002"),
            new Claim("unique_name", @"CONTOSO\anna"));
        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        OmpOidcSignInDiagnostics.LogSignIn(
            logger,
            principal,
            resolved!,
            [@"ADUser|FABRIKAM\bo"],
            new OmpOidcDiagnosticsOptions { Enabled = true });

        Assert.NotEmpty(logger.Entries);
        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains("sub", text);
        Assert.Contains("groups", text);
        Assert.Contains("unique_name", text);
        // Raw claim values must not be logged unless IncludeClaimValues is set.
        // Since 2026-09-03 (CodeQL cs/cleartext-storage-of-sensitive-information,
        // alert #10) the same holds for the resolved principal VALUES: the default
        // line carries the principal types with counts, never the account name.
        Assert.DoesNotContain("subject-1", text);
        Assert.DoesNotContain(@"CONTOSO\anna", text);
        Assert.DoesNotContain("S-1-5-21-11-22-33-2001", text);
        Assert.DoesNotContain(@"FABRIKAM\bo", text);
        Assert.Contains("ADUser (1)", text);
    }

    [Fact]
    public void LogSignIn_Enabled_SummarisesPrincipalTypesWithCounts()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(new Claim("sub", "subject-1"));
        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        OmpOidcSignInDiagnostics.LogSignIn(
            logger,
            principal,
            resolved!,
            [
                "User|lindu5@example.test",
                @"ADUser|EXAMPLE\lindu5",
                "ADUser|S-1-5-21-11-22-33-6091592",
                "OIDCSubject|subject-1",
                "OmpUser|4"
            ],
            new OmpOidcDiagnosticsOptions { Enabled = true });

        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains("ADUser (2), OIDCSubject (1), OmpUser (1), User (1)", text);
        Assert.DoesNotContain("lindu5", text);
        Assert.DoesNotContain("S-1-5-21-11-22-33-6091592", text);
    }

    [Fact]
    public void LogSignIn_IncludeClaimValues_LogsValues()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("unique_name", @"CONTOSO\anna"));
        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        OmpOidcSignInDiagnostics.LogSignIn(
            logger,
            principal,
            resolved!,
            ["ADUser|CONTOSO\\anna", "ADUser|S-1-5-21-11-22-33-1001"],
            new OmpOidcDiagnosticsOptions { Enabled = true, IncludeClaimValues = true });

        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains(@"CONTOSO\anna", text);
        // The full principal list (account names, translated SIDs) is opt-in
        // together with the claim values.
        Assert.Contains("resolved role principals: ADUser|CONTOSO\\anna, ADUser|S-1-5-21-11-22-33-1001", text);
    }

    [Fact]
    public void LogFailedSignIn_DiagnosticsDisabled_LogsNothing()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("unique_name", @"CONTOSO\anna"));

        OmpOidcSignInDiagnostics.LogFailedSignIn(logger, principal, new OmpOidcDiagnosticsOptions());

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void LogFailedSignIn_Enabled_LogsClaimTypesAndCountsButNotValues()
    {
        // Follow-up phase 2, finding 4: a sign-in that fails completely (no
        // provider user key, or a matched but disabled user) is exactly the
        // case incident diagnostics must distinguish, so the failure branch
        // logs the same claim types with value counts as a successful sign-in.
        var logger = new ListLogger();
        var principal = CreatePrincipal(
            new Claim("groups", "S-1-5-21-11-22-33-2001"),
            new Claim("groups", "S-1-5-21-11-22-33-2002"),
            new Claim("unique_name", @"CONTOSO\anna"));

        OmpOidcSignInDiagnostics.LogFailedSignIn(
            logger, principal, new OmpOidcDiagnosticsOptions { Enabled = true });

        Assert.NotEmpty(logger.Entries);
        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains("unique_name", text);
        Assert.Contains("groups", text);
        Assert.DoesNotContain(@"CONTOSO\anna", text);
        Assert.DoesNotContain("S-1-5-21-11-22-33-2001", text);
    }

    [Fact]
    public void LogFailedSignIn_IncludeClaimValues_LogsValues()
    {
        var logger = new ListLogger();
        var principal = CreatePrincipal(new Claim("unique_name", @"CONTOSO\anna"));

        OmpOidcSignInDiagnostics.LogFailedSignIn(
            logger,
            principal,
            new OmpOidcDiagnosticsOptions { Enabled = true, IncludeClaimValues = true });

        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains(@"CONTOSO\anna", text);
    }

    [Fact]
    public void LogFailedSignIn_NullPrincipal_LogsNothing()
    {
        var logger = new ListLogger();

        OmpOidcSignInDiagnostics.LogFailedSignIn(
            logger, null, new OmpOidcDiagnosticsOptions { Enabled = true });

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ConfiguredClaimReporter_MissingMapping_WarnsOncePerClaimType()
    {
        var logger = new ListLogger();
        var reporter = new OmpOidcConfiguredClaimReporter();
        var principal = CreatePrincipal(new Claim("sub", "subject-1"));
        var claimTypes = new OmpOidcClaimTypeOptions
        {
            SamAccountNameClaimType = "samaccountname",
            DomainClaimType = "netbiosname"
        };

        reporter.ReportMissingConfiguredClaimTypes(logger, principal, claimTypes);
        reporter.ReportMissingConfiguredClaimTypes(logger, principal, claimTypes);

        var warnings = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, entry => entry.Message.Contains("samaccountname"));
        Assert.Contains(warnings, entry => entry.Message.Contains("netbiosname"));
    }

    [Fact]
    public void ConfiguredClaimReporter_PresentMapping_DoesNotWarn()
    {
        var logger = new ListLogger();
        var reporter = new OmpOidcConfiguredClaimReporter();
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("samaccountname", "anna"));
        var claimTypes = new OmpOidcClaimTypeOptions
        {
            SamAccountNameClaimType = "samaccountname"
        };

        reporter.ReportMissingConfiguredClaimTypes(logger, principal, claimTypes);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ConfiguredClaimReporter_DefaultOptions_DoNotWarn()
    {
        // Default claim-type options must not produce noise for providers that
        // legitimately omit the AD-mapping claims.
        var logger = new ListLogger();
        var reporter = new OmpOidcConfiguredClaimReporter();
        var principal = CreatePrincipal(new Claim("sub", "subject-1"));

        reporter.ReportMissingConfiguredClaimTypes(logger, principal, new OmpOidcClaimTypeOptions());

        Assert.Empty(logger.Entries);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
