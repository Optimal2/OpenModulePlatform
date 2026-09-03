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

    [Fact]
    public void ForLog_ReplacesUnicodeLineAndParagraphSeparatorsAndNel()
    {
        const char lineSeparator = (char)0x2028;
        const char paragraphSeparator = (char)0x2029;
        const char nextLine = (char)0x85;
        var forged = "a" + lineSeparator + "b" + paragraphSeparator + "c" + nextLine + "d";

        Assert.Equal("a b c d", OmpLogSanitizer.ForLog(forged));
    }

    [Fact]
    public void ForLog_AtTheCspReportBudgetNeverAddsASecondMarker()
    {
        // The CSP endpoint caps a body at 64 kB + marker and hands the sanitizer the
        // same budget; an already capped body must pass through with ONE marker.
        const int maxReportBytes = 64 * 1024;
        var budget = maxReportBytes + OmpLogSanitizer.TruncationMarker.Length;
        var alreadyCapped = new string('x', maxReportBytes) + OmpLogSanitizer.TruncationMarker;

        var safe = OmpLogSanitizer.ForLog(alreadyCapped, maxLength: budget);

        Assert.Equal(alreadyCapped, safe);
        Assert.Equal(1, CountOccurrences(safe, OmpLogSanitizer.TruncationMarker));

        var oneOver = alreadyCapped + "y";
        var capped = OmpLogSanitizer.ForLog(oneOver, maxLength: budget);
        Assert.Equal(budget + OmpLogSanitizer.TruncationMarker.Length, capped.Length);
        Assert.EndsWith(OmpLogSanitizer.TruncationMarker, capped, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
