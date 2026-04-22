using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Pages;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Pages;

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
