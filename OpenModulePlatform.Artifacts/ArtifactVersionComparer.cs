namespace OpenModulePlatform.Artifacts;

public static class ArtifactVersionComparer
{
    public static IComparer<string> Instance { get; } = Comparer.Instance;

    public static int Compare(string? left, string? right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }

    private sealed class Comparer : IComparer<string>
    {
        public static readonly Comparer Instance = new();

        public int Compare(string? x, string? y)
            => ArtifactVersionComparer.Compare(x, y);
    }
}
