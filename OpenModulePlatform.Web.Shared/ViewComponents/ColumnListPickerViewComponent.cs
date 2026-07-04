using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Web.Shared.Models;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

public sealed class ColumnListPickerViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(ColumnListPickerModel model)
        => View(model);
}
