// File: OpenModulePlatform.WorkerManager.WindowsService/Utilities/PathResolutionUtility.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Utilities;

internal static class PathResolutionUtility
{
    public static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var relativePath = path.TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (relativePath.Length == 0)
        {
            throw new ArgumentException(
                "Path must contain more than just directory separator characters.",
                nameof(path));
        }

        // Do not use Path.Combine here. The analyzer cannot prove that relativePath is non-rooted,
        // even though rooted inputs are handled above and leading directory separators are trimmed.
        // Building the path explicitly keeps the intent clear and avoids a false positive about
        // silently dropping the base directory.
        return Path.GetFullPath($"{baseDirectory}{Path.DirectorySeparatorChar}{relativePath}");
    }
}
