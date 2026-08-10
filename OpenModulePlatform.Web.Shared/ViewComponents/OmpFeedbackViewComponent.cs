using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Web.Shared.Models;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

/// <summary>
/// Shared feedback block: success message, error message and ModelState
/// summary, wrapped in a [data-omp-feedback] anchor that omp-feedback.js
/// scrolls into view after load. Place it next to the section, table or
/// form the action belongs to - not at the top of the page.
/// </summary>
public sealed class OmpFeedbackViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(string? message = null, string? error = null, bool includeValidationSummary = true)
        => View(new OmpFeedbackDisplayModel
        {
            Message = message,
            Error = error,
            IncludeValidationSummary = includeValidationSummary
        });
}
