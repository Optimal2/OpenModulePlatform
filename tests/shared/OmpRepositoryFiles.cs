namespace OpenModulePlatform.TestSupport;

/// <summary>
/// Locates the repository root from a test host's base directory and reads files under it.
/// </summary>
/// <remarks>
/// Guard tests read production source and SQL files straight from the repository (a grep
/// over a file is how they prove a wiring or a statement is still present). Until
/// 2026-09-21 every such test carried its own private copy of these two methods -- fifteen
/// of them across three test projects -- so a change to the root marker or the argument
/// check would have had to be applied fifteen times. The projects link this file instead.
/// </remarks>
public static class OmpRepositoryFiles
{
    private const string RootMarker = "OpenModulePlatform.slnx";

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> to the directory that holds the
    /// solution file.
    /// </summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    /// <summary>
    /// Resolves a path given as segments relative to the repository root.
    /// </summary>
    public static string GetRepositoryPath(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return Path.Join(segments);
    }

    /// <summary>
    /// Reads a text file given as segments relative to the repository root.
    /// </summary>
    public static string ReadRepositoryTextFile(params string[] relativePathSegments)
        => File.ReadAllText(GetRepositoryPath(relativePathSegments));
}
