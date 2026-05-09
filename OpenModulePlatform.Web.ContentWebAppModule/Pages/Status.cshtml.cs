// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Status.cshtml.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Pages;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages;

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
