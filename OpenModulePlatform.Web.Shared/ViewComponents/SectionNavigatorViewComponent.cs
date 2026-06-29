using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Web.Shared.Navigation;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

public sealed class SectionNavigatorViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(SectionNavigatorModel model)
        => View(model);
}
