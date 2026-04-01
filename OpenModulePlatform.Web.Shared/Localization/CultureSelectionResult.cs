using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Localization;

/// <summary>
/// Describes the user's global preferred culture and the effective culture used by the current web app.
/// </summary>
public sealed class CultureSelectionResult
{
    public required string PreferredCulture { get; init; }

    public required string EffectiveCulture { get; init; }

    public bool IsFallback => !string.Equals(PreferredCulture, EffectiveCulture, StringComparison.OrdinalIgnoreCase);

    public string PreferredCultureDisplayText => ToDisplayText(PreferredCulture);

    public string EffectiveCultureDisplayText => ToDisplayText(EffectiveCulture);

    private static string ToDisplayText(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return "English";
        }

        if (culture.StartsWith("sv", StringComparison.OrdinalIgnoreCase))
        {
            return "Swedish";
        }

        if (culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "English";
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }
}
