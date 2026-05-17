namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class DeploymentPath
{
    public static string CombineUnderRoot(string rootDirectory, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new InvalidOperationException($"{description} root path is not configured.");
        }

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
        {
            return Path.GetFullPath(rootDirectory);
        }

        // Path.Combine discards earlier arguments when a later part is rooted.
        // HostAgent deployment paths that are meant to be relative are therefore
        // validated before joining and checked again after normalization.
        if (Path.IsPathRooted(relativePath) || ContainsParentDirectorySegment(relativePath))
        {
            throw new InvalidOperationException($"{description} must be a relative path below its configured root.");
        }

        var root = Path.GetFullPath(rootDirectory);
        var combined = Path.GetFullPath(Path.Join(root, relativePath));
        if (!IsUnderRoot(root, combined))
        {
            throw new InvalidOperationException($"{description} resolved outside its configured root.");
        }

        return combined;
    }

    public static bool IsUnderRoot(string rootDirectory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsParentDirectorySegment(string relativePath)
    {
        return relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
    }
}
