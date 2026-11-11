using System.Reflection;

namespace OpenModulePlatform.Web.Shared.Runtime;

/// <summary>
/// Compares the running .NET runtime with the recommended runtime recorded at build time.
/// The advisory never requires a runtime or prevents an application from starting.
/// </summary>
public static class OmpRuntimeAdvisory
{
    /// <summary>The assembly metadata key containing the recommended .NET runtime version.</summary>
    public const string RecommendedRuntimeMetadataKey = "OmpRecommendedDotNetRuntime";

    /// <summary>
    /// Compares explicit versions without reading process or assembly state.
    /// This pure overload is the test seam for callers and tests that need to inject
    /// a recommended version higher than the currently installed runtime.
    /// Only an older patch on the same major.minor band is considered outdated.
    /// </summary>
    public static OmpRuntimeAdvisoryResult Compare(Version? running, string? recommended)
    {
        if (running is null || !Version.TryParse(recommended, out var recommendedVersion))
        {
            return new OmpRuntimeAdvisoryResult(false, running, null);
        }

        var runningBuild = Math.Max(0, running.Build);
        var recommendedBuild = Math.Max(0, recommendedVersion.Build);
        var isOutdated = running.Major == recommendedVersion.Major
            && running.Minor == recommendedVersion.Minor
            && runningBuild < recommendedBuild;

        return new OmpRuntimeAdvisoryResult(isOutdated, running, recommendedVersion);
    }

    /// <summary>
    /// Reads the recommendation from <paramref name="assembly"/> and compares it with
    /// <paramref name="running"/>. Explicit inputs make this overload suitable for tests.
    /// Missing or unreadable metadata produces a quiet, non-outdated result.
    /// </summary>
    public static OmpRuntimeAdvisoryResult Evaluate(Assembly? assembly, Version? running)
    {
        try
        {
            var recommended = assembly?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Key, RecommendedRuntimeMetadataKey, StringComparison.Ordinal))?
                .Value;

            return Compare(running, recommended);
        }
        catch (Exception)
        {
            return new OmpRuntimeAdvisoryResult(false, running, null);
        }
    }

    /// <summary>
    /// Evaluates the current process using the recommendation stamped on Web.Shared.
    /// This advisory helper never throws.
    /// </summary>
    public static OmpRuntimeAdvisoryResult EvaluateCurrentProcess()
    {
        try
        {
            return Evaluate(typeof(OmpRuntimeAdvisory).Assembly, Environment.Version);
        }
        catch (Exception)
        {
            return new OmpRuntimeAdvisoryResult(false, null, null);
        }
    }
}

/// <summary>The fail-quiet result of a recommended-runtime comparison.</summary>
/// <param name="IsOutdated">True only for an older patch on the same major.minor band.</param>
/// <param name="Running">The running version supplied by the caller, when available.</param>
/// <param name="Recommended">The parsed recommended version, when available and valid.</param>
public sealed record OmpRuntimeAdvisoryResult(
    bool IsOutdated,
    Version? Running,
    Version? Recommended);
