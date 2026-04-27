using OpenModulePlatform.Web.Shared.Localization;
using Microsoft.AspNetCore.Authorization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Pages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWorkerAppModule.Pages;

[AllowAnonymous]
public sealed class StatusModel : OmpStatusPageModelBase
{
    public StatusModel(
        IOptions<WebAppOptions> webAppOptions,
        IStringLocalizer<SharedResource> localizer)
        : base(webAppOptions, localizer)
    {
    }
}
