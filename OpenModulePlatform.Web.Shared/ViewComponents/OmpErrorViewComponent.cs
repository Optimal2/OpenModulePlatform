using OpenModulePlatform.Web.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

public sealed class OmpErrorViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(OmpErrorDisplayModel model)
        => View(model);
}
