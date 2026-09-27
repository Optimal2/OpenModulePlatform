using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Web.ModuleFragments;

namespace OpenModulePlatform.UiTests.Pages;

// Intentionally no base class or handler guard: the attribute must protect the page.
[OmpModuleFragment("example.view", "example.admin")]
public sealed class FragmentModel : PageModel
{
    public void OnGet() { }
}
