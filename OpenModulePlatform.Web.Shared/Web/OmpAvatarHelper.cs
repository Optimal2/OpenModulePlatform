using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Web;

public static class OmpAvatarHelper
{
    public static string? BuildUserAvatarPath(int? userId, string? storageKey)
    {
        if (userId is null or <= 0 || string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"/users/avatar/{userId.Value}?v={Uri.EscapeDataString(storageKey.Trim())}");
    }

    public static string GetInitials(string? displayName)
    {
        var cleaned = DisplayName(displayName);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "?";
        }

        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
        }

        return cleaned[..Math.Min(cleaned.Length, 2)].ToUpperInvariant();
    }

    public static string DisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var separatorIndex = trimmed.LastIndexOf('\\');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }
}
