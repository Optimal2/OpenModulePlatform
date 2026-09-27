using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Pages;

public sealed class SystemLogCalendarInputTests
{
    [Theory]
    [InlineData("2026-03-29T00:30:00Z", "2026-03-29T00:30:00Z")]
    [InlineData("2026-03-29T02:30:00+02:00", "2026-03-29T00:30:00Z")]
    [InlineData("2026-03-29T01:30", "2026-03-29T00:30:00Z")]
    [InlineData("2026-03-29T03:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-10-25T02:30:00+01:00", "2026-10-25T01:30:00Z")]
    [InlineData("2026-10-25T02:30:00+02:00", "2026-10-25T00:30:00Z")]
    [InlineData("2026-03-28T17:30:12.1234567-07:00", "2026-03-29T00:30:12.1234567Z")]
    [InlineData("2026-03-29T00:30Z", "2026-03-29T00:30:00Z")]
    public async Task QueryBindingPreservesInstantsAndConvertsCalendarValues(string input, string utc)
    {
        using var services = CreateServices();
        var reader = new RecordingReader();
        var model = await BindAsync(services, reader, $"?from={Uri.EscapeDataString(input)}&to={Uri.EscapeDataString(input)}");
        Assert.IsType<PageResult>(await model.OnGetAsync(CancellationToken.None));
        Assert.True(model.ModelState.IsValid);
        var filter = Assert.Single(reader.Filters);
        var expected = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).UtcDateTime;
        Assert.Equal(expected, filter.FromUtc);
        Assert.Equal(expected.AddMinutes(1), filter.ToUtc);
        Assert.Equal(DateTimeKind.Utc, filter.FromUtc!.Value.Kind);
        Assert.Equal(new OmpTime("Europe/Stockholm").ToDisplayTime(expected).DateTime, model.From);
        Assert.Equal(DateTimeKind.Unspecified, model.From!.Value.Kind);
    }

    [Theory]
    [InlineData("2026-03-29", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [InlineData("2026-10-25", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    [InlineData("2026-10-25T02:30", "2026-10-25T00:30:00Z", "2026-10-25T01:31:00Z")]
    public async Task CalendarBoundariesFollowConfiguredDstRules(string input, string from, string to)
    {
        using var services = CreateServices();
        var reader = new RecordingReader();
        var model = await BindAsync(services, reader, $"?from={Uri.EscapeDataString(input)}&to={Uri.EscapeDataString(input)}");
        await model.OnGetAsync(CancellationToken.None);
        Assert.True(model.ModelState.IsValid);
        var filter = Assert.Single(reader.Filters);
        Assert.Equal(DateTimeOffset.Parse(from).UtcDateTime, filter.FromUtc);
        Assert.Equal(DateTimeOffset.Parse(to).UtcDateTime, filter.ToUtc);
    }

    [Theory]
    [InlineData("from", "not-a-date", "en", "Enter a valid date and time.")]
    [InlineData("to", "not-a-date", "sv", "Ange ett giltigt datum och klockslag.")]
    [InlineData("from", "2026-03-29T02:30", "sv-SE", "Ange ett giltigt datum och klockslag.")]
    [InlineData("to", "2026-03-29T02:30", "en-US", "Enter a valid date and time.")]
    [InlineData("from", "2026-03-29T02:30:00+25:00", "en", "Enter a valid date and time.")]
    [InlineData("to", "9999-12-31", "en", "Enter a valid date and time.")]
    public async Task InvalidInputKeepsProcessesAndSkipsLogQuery(string field, string input, string culture, string message)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = CreateServices();
            var reader = new RecordingReader();
            var model = await BindAsync(services, reader, $"?{field}={Uri.EscapeDataString(input)}");
            Assert.IsType<PageResult>(await model.OnGetAsync(CancellationToken.None));
            Assert.False(model.ModelState.IsValid);
            Assert.Contains(model.ModelState.Values.SelectMany(v => v.Errors), error => error.ErrorMessage == message);
            Assert.Equal(new[] { "ExampleProcess" }, model.AvailableProcesses);
            Assert.Empty(reader.Filters);
            Assert.Empty(model.Entries);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task EmptyBoundsDoNotRestrictTheQuery()
    {
        using var services = CreateServices();
        var reader = new RecordingReader();
        var model = await BindAsync(services, reader, "?from=&to=");
        await model.OnGetAsync(CancellationToken.None);
        var filter = Assert.Single(reader.Filters);
        Assert.Null(filter.FromUtc);
        Assert.Null(filter.ToUtc);
    }

    [Fact]
    public async Task KnownPresetUsesWholeConfiguredDays()
    {
        using var services = CreateServices();
        var reader = new RecordingReader();
        var model = await BindAsync(services, reader, "?range=7d&from=2026-03-29T00:30:00Z&to=2026-03-29T00:30:00Z");
        await model.OnGetAsync(CancellationToken.None);
        var filter = Assert.Single(reader.Filters);
        var time = new OmpTime("Europe/Stockholm");
        Assert.Equal(time.StartOfDayUtc(DateOnly.FromDateTime(model.From!.Value)), filter.FromUtc);
        Assert.Equal(time.StartOfDayUtc(DateOnly.FromDateTime(model.To!.Value).AddDays(1)), filter.ToUtc);
        Assert.Equal(6, (model.To.Value.Date - model.From.Value.Date).Days);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection().AddLogging().AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddMvc();
        return services.BuildServiceProvider();
    }

    private static async Task<SystemLogModel> BindAsync(IServiceProvider services, RecordingReader reader, string query)
    {
        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString(query);
        var model = new SystemLogModel(Microsoft.Extensions.Options.Options.Create(new WebAppOptions { AllowAnonymous = true }),
            null!, reader, new OmpTime("Europe/Stockholm"))
        {
            PageContext = new PageContext
            {
                HttpContext = context,
                ViewData = new ViewDataDictionary(metadata, new ModelStateDictionary())
            }
        };
        // Exercise MVC's actual binder providers and property metadata on a decoded query.
        var factory = services.GetRequiredService<IModelBinderFactory>();
        var values = new QueryStringValueProvider(BindingSource.Query, context.Request.Query, CultureInfo.InvariantCulture);
        foreach (var property in metadata.GetMetadataForType(typeof(SystemLogModel)).Properties
                     .Where(p => typeof(SystemLogModel).GetProperty(p.PropertyName!)!
                         .GetCustomAttributes(typeof(BindPropertyAttribute), true).OfType<BindPropertyAttribute>().Any(a => a.SupportsGet)))
        {
            var binding = new BindingInfo { BindingSource = BindingSource.Query, BinderModelName = property.BinderModelName };
            var binder = factory.CreateBinder(new ModelBinderFactoryContext { Metadata = property, BindingInfo = binding });
            var bindingContext = DefaultModelBindingContext.CreateBindingContext(model.PageContext, values, property, binding,
                property.BinderModelName ?? property.PropertyName!);
            bindingContext.IsTopLevelObject = false;
            await binder.BindModelAsync(bindingContext);
            if (bindingContext.Result.IsModelSet)
            {
                property.PropertySetter!(model, bindingContext.Result.Model);
                services.GetRequiredService<IObjectModelValidator>().Validate(model.PageContext, null,
                    bindingContext.ModelName, bindingContext.Result.Model);
            }
        }
        return model;
    }

    private sealed class RecordingReader : ISystemLogReader
    {
        public List<SystemLogFilter> Filters { get; } = [];
        public Task<IReadOnlyList<string>> GetSystemLogProcessesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["ExampleProcess"]);
        public Task<IReadOnlyList<SystemLogRow>> SearchSystemLogAsync(SystemLogFilter filter, CancellationToken ct)
        {
            Filters.Add(filter);
            return Task.FromResult<IReadOnlyList<SystemLogRow>>([]);
        }
    }
}
