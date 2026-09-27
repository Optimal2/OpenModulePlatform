using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Options;
using System.Reflection;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Web;

public sealed class PresentationTimeTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void HostedPresentationMarksLocalValuesAndLogsWarnings(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        var logger = new WarningLogger();
        builder.Services.AddSingleton<ILogger<OmpTime>>(logger);
        builder.Services.AddOmpTime(builder.Configuration);
        using var services = builder.Services.BuildServiceProvider();
        var time = services.GetRequiredService<OmpTime>();
        var local = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Local);
        Assert.Equal("[Invalid time: Local]", time.Format(local));
        Assert.Equal("[Invalid time: Local]", time.Format((DateTime?)local));
        Assert.Equal(string.Empty, time.UtcIso(local));
        Assert.Equal(3, logger.Warnings.Count);
        Assert.All(logger.Warnings, message => Assert.Contains("Local", message));
        Assert.Throws<ArgumentException>(() => time.ToDisplayTime(local));
        Assert.Equal("2026-09-24 22:30:00 UTC", time.Format(DateTime.SpecifyKind(local, DateTimeKind.Utc)));
        Assert.Equal(string.Empty, time.Format((DateTime?)null));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentAndDirectConstructionStillRejectLocalPresentation(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Services.AddOmpTime(builder.Configuration);
        using var services = builder.Services.BuildServiceProvider();
        var local = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Local);
        foreach (var time in new[] { new OmpTime(), services.GetRequiredService<OmpTime>() })
        {
            Assert.Throws<ArgumentException>(() => time.Format(local));
            Assert.Throws<ArgumentException>(() => time.Format((DateTime?)local));
            Assert.Throws<ArgumentException>(() => time.UtcIso(local));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BannerMissingMinuteProducesAFieldError(bool start)
    {
        var model = CreateBannerModel();
        var missing = new DateTime(2026, 3, 29, 2, 30, 0);
        model.Input.StartsAt = start ? missing : null;
        model.Input.ExpiresAt = start ? null : missing;
        typeof(BannersModel).GetMethod("ValidateInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
        var field = start ? "Input.StartsAt" : "Input.ExpiresAt";
        Assert.False(model.ModelState.IsValid);
        Assert.Single(model.ModelState[field]!.Errors);
        Assert.Contains("Europe/Stockholm", model.ModelState[field]!.Errors[0].ErrorMessage);
    }

    [Fact]
    public void BannerRepeatedMinuteUsesFirstOccurrenceForBothFields()
    {
        var model = CreateBannerModel();
        var convert = typeof(BannersModel).GetMethod("ToUtcOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var minute in new[] { 30, 45 })
        {
            var result = (DateTimeOffset)convert.Invoke(model, [new DateTime(2026, 10, 25, 2, minute, 0)])!;
            Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, minute, 0, TimeSpan.Zero), result);
        }
        Assert.Null(convert.Invoke(model, [null]));
    }

    private static BannersModel CreateBannerModel()
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new BannersModel(Microsoft.Extensions.Options.Options.Create(new WebAppOptions()), null!, null!,
            new OmpTime("Europe/Stockholm"),
            services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<PortalResource>>());
    }

    private sealed class WarningLogger : ILogger<OmpTime>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, error));
        }
    }

    [Theory]
    [InlineData(2026, 9, 24, 22, "2026-09-25 00:30:00 CEST")]
    [InlineData(2026, 1, 14, 23, "2026-01-15 00:30:00 CET")]
    public void StockholmPresentationCrossesLocalMidnight(int year, int month, int day, int hour, string expected)
    {
        var utc = new DateTime(year, month, day, hour, 30, 0, DateTimeKind.Utc);
        var time = new OmpTime("Europe/Stockholm", new FixedClock(new DateTimeOffset(utc)));
        Assert.Equal(expected, time.Format(utc));
        var tomorrow = DateOnly.FromDateTime(utc).AddDays(1);
        Assert.Equal(tomorrow, time.Today);
        Assert.Equal((tomorrow, tomorrow), PeriodPresets.Apply("today", null, null, time.Today));
        Assert.True(time.StartOfDayUtc(tomorrow) <= utc);
        Assert.True(time.StartOfDayUtc(tomorrow.AddDays(1)) > utc);
        Assert.Equal(utc.AddMinutes(-30), time.StartOfDayUtc(tomorrow));
    }

    [Fact]
    public void InvalidTimeZoneStopsStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OmpTime:TimeZoneId"] = "Invalid/Zone"
        });
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddOmpWebDefaults<PresentationTimeTests>());
        Assert.Contains("OmpTime:TimeZoneId", error.Message);
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    public void CalendarDayBoundariesFollowDst(int year, int month, int day, int hours)
    {
        var time = new OmpTime("Europe/Stockholm");
        var date = new DateOnly(year, month, day);
        Assert.Equal(hours, (time.StartOfDayUtc(date.AddDays(1)) - time.StartOfDayUtc(date)).TotalHours);
    }

    [Fact]
    public void MissingAndRepeatedMinutesAreExplicit()
    {
        var time = new OmpTime("Europe/Stockholm");
        Assert.Throws<ArgumentException>(() => time.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0)));
        var repeated = new DateTime(2026, 10, 25, 2, 30, 0);
        Assert.Equal(TimeSpan.FromHours(1), time.ToUtc(repeated, upperBound: true) - time.ToUtc(repeated));
    }

    [Fact]
    public void CalendarBoundaryUsesFirstExistingMinuteWhenMidnightIsSkipped()
    {
        var time = new OmpTime("America/Sao_Paulo");
        var start = time.StartOfDayUtc(new DateOnly(2018, 11, 4));
        Assert.Equal(new DateTime(2018, 11, 4, 3, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(1, time.ToDisplayTime(start).Hour);
    }

    [Fact]
    public void UtcDefaultAndSqlUnspecifiedDoNotUseMachineZone()
    {
        var utc = new DateTime(2026, 9, 24, 22, 30, 0);
        Assert.Equal("2026-09-24 22:30:00 UTC", new OmpTime().Format(utc));
        Assert.Equal("2026-09-25 00:30:00 CEST", new OmpTime("Europe/Stockholm").Format(utc));
        Assert.Equal(string.Empty, new OmpTime().Format((DateTime?)null));
        Assert.Throws<ArgumentException>(() => new OmpTime().ToDisplayTime(DateTime.SpecifyKind(utc, DateTimeKind.Local)));
        Assert.Equal("2026-09-24T22:30:00.0000000Z", new OmpTime("Europe/Stockholm").UtcIso(utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("W. Europe Standard Time")]
    public void RejectsEmptyAndWindowsIdentifiers(string id)
        => Assert.Throws<InvalidOperationException>(() => new OmpTime(id));

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
