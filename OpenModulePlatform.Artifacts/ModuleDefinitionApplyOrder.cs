namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Decides the order module definitions must be applied in.
/// </summary>
/// <remarks>
/// Module definitions carry no dependency declaration, so both import paths applied
/// them in the order they happened to arrive - alphabetically by module key on the
/// portal path, and in package order on the HostAgent path. Both put omp_auth before
/// omp_core.
///
/// That is backwards. omp_core's setup script owns the platform-wide tables and adds
/// columns to them, and other modules' initialize scripts write to those columns. The
/// code already knew: RequiresPreApplySqlRepairs singles out omp_core with the comment
/// "Platform core schema changes may be required by the apply step itself ... so old
/// installations can bridge schema gaps such as newly introduced columns." It applied
/// that only to omp_core's own pre-apply step, never to the order between modules.
///
/// It stayed hidden because it only breaks on an installation whose schema predates the
/// columns. Measured in customer production 2026-08-23: omp_auth failed with "Invalid
/// column name 'ValidationRegex'. Invalid column name 'ExampleValues'." while omp_core -
/// the definition that adds exactly those two columns - was applied successfully
/// immediately afterwards. The same package imported cleanly in the test environment,
/// where core had been applied long ago, and cleanly on a second run in production for
/// the same reason. An import that succeeds on the second attempt is not a fixed import.
/// </remarks>
public static class ModuleDefinitionApplyOrder
{
    /// <summary>The module whose schema the others build on.</summary>
    public const string PlatformCoreModuleKey = "omp_core";

    /// <summary>
    /// Sort key for applying module definitions: platform core first, then by module key.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general dependency graph. There is exactly one known ordering
    /// constraint and no way to express others, so inventing a graph would suggest a
    /// guarantee that does not exist. If a second constraint ever appears, the definition
    /// format needs a dependency field - and this method should then be replaced rather
    /// than extended with another special case.
    /// </remarks>
    public static int GetApplyRank(string? moduleKey)
        => string.Equals(moduleKey, PlatformCoreModuleKey, StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;

    /// <summary>
    /// Same ranking, for a package entry path such as
    /// <c>module-definitions/omp_core.module-definition.json</c>.
    /// </summary>
    /// <remarks>
    /// The HostAgent path carries package entries, which do not expose the module key -
    /// only the path the definition was stored under. The file name is the module key by
    /// construction, so it is derived rather than guessed at with a substring match:
    /// a match on "omp_core" anywhere in the path would also rank a module named
    /// omp_core_something as core.
    /// </remarks>
    public static int GetApplyRankForPath(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return 1;
        }

        var fileName = System.IO.Path.GetFileName(packagePath.Replace('\\', '/'));
        const string suffix = ".module-definition.json";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return GetApplyRank(fileName[..^suffix.Length]);
    }
}
