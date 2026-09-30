using Microsoft.AspNetCore.Mvc;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

/// <summary>
/// Renders the System / Light / Dark theme menu of the OMP theme contract
/// (docs/THEME_CONTRACT.md). The markup carries no state: omp-theme.js reads the
/// stored preference and marks the current option when the menu opens, so the
/// same markup works for every user and survives Blazor re-renders. The Blazor
/// component Components/Layout/OmpThemeSwitch.razor renders the identical markup;
/// keep the two in step.
/// </summary>
public sealed class OmpThemeSwitchViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
        => View();
}
