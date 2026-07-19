using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Web.Shared.Navigation;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

public sealed class LinkBoxViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(LinkBoxModel model)
        => View(model);
}
