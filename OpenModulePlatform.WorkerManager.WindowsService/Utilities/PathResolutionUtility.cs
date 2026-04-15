// File: OpenModulePlatform.WorkerManager.WindowsService/Utilities/PathResolutionUtility.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Utilities;

internal static class PathResolutionUtility
{
    public static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

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

        return string.IsNullOrWhiteSpace(relativePath)
            ? Path.GetFullPath(baseDirectory)
            : Path.GetFullPath($"{baseDirectory}{Path.DirectorySeparatorChar}{relativePath}");
    }
}
