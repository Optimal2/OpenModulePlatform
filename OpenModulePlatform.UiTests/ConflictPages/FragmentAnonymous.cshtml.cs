using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenModulePlatform.Web.ModuleFragments;

namespace OpenModulePlatform.UiTests.ConflictPages;

// Deliberately conflicting model: the fragment policy and AllowAnonymous on the
// same endpoint. The startup guard must reject this combination before the app
// serves a single request. Lives outside /Pages so only the conflict test, which
// narrows RootDirectory to /ConflictPages, ever discovers it.
[OmpModuleFragment("example.view")]
[AllowAnonymous]
public sealed class FragmentAnonymousModel : PageModel
{
    public void OnGet() { }
}
