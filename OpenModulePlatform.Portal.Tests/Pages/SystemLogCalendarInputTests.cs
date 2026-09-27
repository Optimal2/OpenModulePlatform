using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Pages;

public sealed class SystemLogCalendarInputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalBoundReturnsValidationErrorBeforeQueryingRepository(bool from)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var model = CreateModel();
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services },
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        var local = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Local);
        model.From = from ? local : null;
        model.To = from ? null : local;

        // The null repository also proves invalid input cannot execute a log query.
        Assert.IsType<PageResult>(await model.OnGetAsync(CancellationToken.None));
        Assert.False(model.ModelState.IsValid);
        Assert.Single(model.ModelState[string.Empty]!.Errors);
        Assert.Empty(model.Entries);
    }

    [Fact]
    public void UtcConvertsToConfiguredCalendarWhileBareInputAndNullStayUnchanged()
    {
        var model = CreateModel();
        var convert = typeof(SystemLogModel).GetMethod("AsCalendarInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var utc = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Utc);
        var calendar = (DateTime)convert.Invoke(model, [utc])!;
        Assert.Equal(new DateTime(2026, 9, 25, 0, 30, 0), calendar);
        Assert.Equal(DateTimeKind.Unspecified, calendar.Kind);
        var bare = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        Assert.Equal(bare, convert.Invoke(model, [bare]));
        Assert.Null(convert.Invoke(model, [null]));
    }

    private static SystemLogModel CreateModel()
        => new(Microsoft.Extensions.Options.Options.Create(new WebAppOptions { AllowAnonymous = true }), null!, null!,
            new OmpTime("Europe/Stockholm"));
}
