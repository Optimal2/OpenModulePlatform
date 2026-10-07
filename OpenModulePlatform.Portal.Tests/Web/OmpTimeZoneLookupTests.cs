using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Web;

/// <summary>
/// Covers OmpTimeZoneLookup, the fallback that keeps IANA time zone ids working
/// on Windows hosts without icu.dll (before Windows 10 1903 / Server 2019),
/// where .NET runs in NLS globalization mode and the platform only knows
/// Windows zone ids.
/// </summary>
public sealed class OmpTimeZoneLookupTests
{
    // Simulates NLS mode: IANA ids are unknown to the platform lookup and the
    // platform IANA-to-Windows conversion always fails. Windows ids still
    // resolve, because every supported Windows host carries them.
    private static OmpTimeZoneLookup NlsModeLookup() => new(
        platformFind: id => id.Contains('/')
            ? throw new TimeZoneNotFoundException($"NLS mode: unknown time zone id '{id}'.")
            : TimeZoneInfo.FindSystemTimeZoneById(id),
        platformConvert: (string _, out string? windowsId) =>
        {
            windowsId = string.Empty;
            return false;
        });

    [Fact]
    public void PlatformLookupResolvesAndConvertsStockholm()
    {
        var zone = OmpTimeZoneLookup.Platform.FindSystemTimeZoneById("Europe/Stockholm");
        Assert.True(zone.HasIanaId);
        Assert.True(OmpTimeZoneLookup.Platform.TryConvertIanaIdToWindowsId("Europe/Stockholm", out var windowsId));
        Assert.Equal("W. Europe Standard Time", windowsId);
    }

    [Theory]
    [InlineData("Europe/Stockholm", "W. Europe Standard Time")]
    [InlineData("Europe/Oslo", "W. Europe Standard Time")]
    [InlineData("Europe/Copenhagen", "Romance Standard Time")]
    [InlineData("Europe/Helsinki", "FLE Standard Time")]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("Europe/Amsterdam", "W. Europe Standard Time")]
    [InlineData("Europe/Paris", "Romance Standard Time")]
    [InlineData("Europe/London", "GMT Standard Time")]
    [InlineData("Etc/UTC", "UTC")]
    [InlineData("UTC", "UTC")]
    public void NlsModeFallsBackToTheBuiltInTable(string ianaId, string expectedWindowsId)
    {
        var lookup = NlsModeLookup();

        Assert.True(lookup.TryConvertIanaIdToWindowsId(ianaId, out var windowsId));
        Assert.Equal(expectedWindowsId, windowsId);

        var zone = lookup.FindSystemTimeZoneById(ianaId);
        Assert.Equal(expectedWindowsId, zone.Id);
    }

    // The fallback must reproduce the platform zone's DST behaviour exactly;
    // a wrong mapping would move calendar boundaries twice a year.
    [Theory]
    [InlineData(2026, 1, 15, 12, 0)]  // winter, CET (+1)
    [InlineData(2026, 7, 15, 12, 0)]  // summer, CEST (+2)
    [InlineData(2026, 3, 29, 0, 30)]  // spring transition, just before the gap
    [InlineData(2026, 3, 29, 2, 30)]  // spring transition, just after the gap
    [InlineData(2026, 10, 25, 0, 30)] // autumn transition, first 02:30 (CEST)
    [InlineData(2026, 10, 25, 2, 30)] // autumn transition, after the fallback (CET)
    public void NlsModeFallbackHasTheSameDstTransitionsAsThePlatformZone(
        int year, int month, int day, int hour, int minute)
    {
        var instant = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        var platformZone = OmpTimeZoneLookup.Platform.FindSystemTimeZoneById("Europe/Stockholm");
        var nlsZone = NlsModeLookup().FindSystemTimeZoneById("Europe/Stockholm");

        Assert.Equal(platformZone.GetUtcOffset(instant), nlsZone.GetUtcOffset(instant));
        Assert.Equal(
            TimeZoneInfo.ConvertTime(instant, platformZone),
            TimeZoneInfo.ConvertTime(instant, nlsZone));
    }

    [Fact]
    public void UnknownIdsThrowAClearTimeZoneNotFoundException()
    {
        foreach (var lookup in new[] { OmpTimeZoneLookup.Platform, NlsModeLookup() })
        {
            var error = Assert.Throws<TimeZoneNotFoundException>(() => lookup.FindSystemTimeZoneById("Invalid/Zone"));
            Assert.Contains("Invalid/Zone", error.Message);
            Assert.False(lookup.TryConvertIanaIdToWindowsId("Invalid/Zone", out _));
        }
    }

    // The platform accepts IANA ids case-insensitively under ICU; the built-in
    // table must not be stricter than the path it replaces.
    [Theory]
    [InlineData("europe/stockholm", "W. Europe Standard Time")]
    [InlineData("EUROPE/STOCKHOLM", "W. Europe Standard Time")]
    [InlineData("etc/utc", "UTC")]
    public void NlsModeTableLookupIsCaseInsensitive(string ianaId, string expectedWindowsId)
    {
        var lookup = NlsModeLookup();

        Assert.True(lookup.TryConvertIanaIdToWindowsId(ianaId, out var windowsId));
        Assert.Equal(expectedWindowsId, windowsId);

        var zone = lookup.FindSystemTimeZoneById(ianaId);
        Assert.Equal(expectedWindowsId, zone.Id);
    }

    [Fact]
    public void NlsModeUnknownIanaIdNamesTheMissingIcuAndTheTable()
    {
        var error = Assert.Throws<TimeZoneNotFoundException>(
            () => NlsModeLookup().FindSystemTimeZoneById("Invalid/Zone"));

        Assert.Contains("Invalid/Zone", error.Message);
        Assert.Contains("ICU", error.Message);
        Assert.Contains("Europe/Stockholm", error.Message);
    }

    // Simulates an ICU host that simply does not know the id: the message must
    // stay the plain "unknown id" one, not the no-ICU guidance.
    private static OmpTimeZoneLookup IcuModeLookupWithUnknownZone() => new(
        platformFind: id => id.Contains('/')
            ? throw new TimeZoneNotFoundException($"unknown time zone id '{id}'.")
            : TimeZoneInfo.FindSystemTimeZoneById(id),
        platformConvert: (string ianaId, out string? windowsId) =>
        {
            // The probe id converts; everything else is unknown.
            windowsId = ianaId == "Europe/Stockholm" ? "W. Europe Standard Time" : string.Empty;
            return ianaId == "Europe/Stockholm";
        });

    [Fact]
    public void IcuModeUnknownIanaIdKeepsTheUnknownIdMessage()
    {
        var error = Assert.Throws<TimeZoneNotFoundException>(
            () => IcuModeLookupWithUnknownZone().FindSystemTimeZoneById("Invalid/Zone"));

        Assert.Contains("Unknown time zone id 'Invalid/Zone'", error.Message);
        Assert.DoesNotContain("no ICU", error.Message);
    }

    [Fact]
    public void OmpTimeWorksInNlsModeAndKeepsTheIanaId()
    {
        var time = new OmpTime("Europe/Stockholm", null, null, rejectLocalPresentation: true, NlsModeLookup());

        Assert.Equal("Europe/Stockholm", time.TimeZoneId);
        var summer = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Utc);
        var winter = new DateTime(2026, 1, 14, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2026-09-25 00:30:00 CEST", time.Format(summer));
        Assert.Equal("2026-01-15 00:30:00 CET", time.Format(winter));
        // Repeated autumn minute: the two occurrences stay one hour apart.
        var repeated = new DateTime(2026, 10, 25, 2, 30, 0);
        Assert.Equal(TimeSpan.FromHours(1), time.ToUtc(repeated, upperBound: true) - time.ToUtc(repeated));
    }

    [Fact]
    public void OmpTimeStillRejectsWindowsIdentifiersInNlsMode()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new OmpTime("W. Europe Standard Time", null, null, rejectLocalPresentation: true, NlsModeLookup()));
        Assert.Contains(OmpTime.ConfigurationKey, error.Message);
    }
}
