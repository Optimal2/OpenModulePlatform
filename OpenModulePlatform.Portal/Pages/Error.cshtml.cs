using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Pages;

namespace OpenModulePlatform.Portal.Pages;

public sealed class ErrorModel(
    IOptions<WebAppOptions> webAppOptions,
    IStringLocalizer<SharedResource> localizer) : OmpErrorPageModelBase(webAppOptions, localizer)
{
}
