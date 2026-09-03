using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// CodeQL cs/log-forging on the anonymous CSP report endpoint (alerts #11 and
/// #12, 2026-09-03): request-supplied text must not be able to fabricate log
/// lines, hide behind a carriage return, or flood the log.
/// </summary>
public sealed class OmpLogSanitizerTests
{
    private const char Bell = (char)7;
    private const char Escape = (char)27;

    [Fact]
    public void ForLog_ReplacesLineBreaksAndControlCharacters()
    {
        var forged = "text/csp-report\r\n2026-09-03 12:00:00 WARN fabricated entry\txy" + Bell + Escape + "[0m";

        var safe = OmpLogSanitizer.ForLog(forged);

        // Ordinal on purpose: culture-sensitive comparison treats BEL/ESC as
        // ignorable characters and "finds" them at position 0 in any string.
        Assert.DoesNotContain("\r", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Bell.ToString(), safe, StringComparison.Ordinal);
        Assert.DoesNotContain(Escape.ToString(), safe, StringComparison.Ordinal);
        Assert.Equal("text/csp-report  2026-09-03 12:00:00 WARN fabricated entry xy  [0m", safe);
    }

    [Fact]
    public void ForLog_CapsTheLengthAndMarksTruncation()
    {
        var safe = OmpLogSanitizer.ForLog(new string('a', 100), maxLength: 10);

        Assert.StartsWith("aaaaaaaaaa", safe, StringComparison.Ordinal);
        Assert.EndsWith(OmpLogSanitizer.TruncationMarker, safe, StringComparison.Ordinal);
        Assert.Equal(10 + OmpLogSanitizer.TruncationMarker.Length, safe.Length);
    }

    [Fact]
    public void ForLog_LeavesOrdinaryTextAlone()
    {
        const string report = "{\"csp-report\":{\"document-uri\":\"https://example.test/\",\"violated-directive\":\"script-src\"}}";

        Assert.Equal(report, OmpLogSanitizer.ForLog(report));
    }

    [Fact]
    public void ForLog_NullOrEmptyBecomesEmpty()
    {
        Assert.Equal(string.Empty, OmpLogSanitizer.ForLog(null));
        Assert.Equal(string.Empty, OmpLogSanitizer.ForLog(string.Empty));
    }
}
