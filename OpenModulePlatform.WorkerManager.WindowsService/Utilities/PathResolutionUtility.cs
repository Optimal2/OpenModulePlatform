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
                "Path cannot consist only of directory separator characters.",
                nameof(path));
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
    }
}
