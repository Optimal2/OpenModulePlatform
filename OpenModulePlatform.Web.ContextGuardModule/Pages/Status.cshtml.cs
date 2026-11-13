using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Pages;

namespace OpenModulePlatform.Web.ContextGuardModule.Pages;

[AllowAnonymous]
public sealed class StatusModel(
    IOptions<WebAppOptions> webAppOptions,
    IStringLocalizer<SharedResource> localizer) : OmpStatusPageModelBase(webAppOptions, localizer);
