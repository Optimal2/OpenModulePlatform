// File: OpenModulePlatform.Web.ContentWebAppModule/Localization/ContentWebAppTextLocalizer.cs
using Microsoft.Extensions.Localization;

namespace OpenModulePlatform.Web.ContentWebAppModule.Localization;

/// <summary>
/// Localizes human-facing text that originates from exceptions surfaced by
/// admin actions. The thrown text remains stable and machine-readable; only
/// the display text is translated. Unknown texts fall through unchanged so
/// no information is lost.
/// </summary>
public static class ContentWebAppTextLocalizer
{
    public static string Display(IStringLocalizer localizer, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var text = value.Trim();
        var exact = text switch
        {
            "A content page with the same slug already exists for this app instance." => localizer["A content page with the same slug already exists for this app instance."],
            "The content page no longer exists." => localizer["The content page no longer exists."],
            _ => null
        };

        return exact ?? text;
    }
}
