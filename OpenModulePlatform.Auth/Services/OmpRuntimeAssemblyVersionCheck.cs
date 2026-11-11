// File: OpenModulePlatform.Auth/Services/OmpRuntimeAssemblyVersionCheck.cs
using System.Reflection;
using OpenModulePlatform.Web.Shared.Runtime;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// Reports the versions of security-critical assemblies that are actually loaded
/// in the running process, so operators can verify that a deployed host really
/// runs the patched Negotiate authentication stack (CVE-2026-47303, CVE-2026-70179).
/// </summary>
internal static class OmpRuntimeAssemblyVersionCheck
{
    public const string NegotiateAssemblyName = "Microsoft.AspNetCore.Authentication.Negotiate";
    public const string AuthAssemblyName = "OpenModulePlatform.Auth";

    // Patched band: any 10.0.15.x revision is acceptable. It must follow the
    // Negotiate PackageVersion in Directory.Packages.props; a Portal test
    // fails when the two drift apart.
    public static readonly Version ExpectedNegotiateBand = new(10, 0, 15);

    public static bool IsOnBand(Version? version, Version band)
    {
        return version is not null
            && version.Major == band.Major
            && version.Minor == band.Minor
            && version.Build == band.Build;
    }

    public static OmpRuntimeAssemblyVersionReport CreateReport()
    {
        var entries = new[]
        {
            Describe(NegotiateAssemblyName, ExpectedNegotiateBand),
            Describe(AuthAssemblyName, expectedBand: null)
        };

        return new OmpRuntimeAssemblyVersionReport(
            entries,
            entries.Any(entry => entry.Warning is not null));
    }

    public static OmpDotNetRuntimeVersionReport CreateDotNetRuntimeReport()
    {
        try
        {
            return CreateDotNetRuntimeReport(OmpRuntimeAdvisory.EvaluateCurrentProcess());
        }
        catch (Exception)
        {
            return new OmpDotNetRuntimeVersionReport(null, null, false);
        }
    }

    public static OmpDotNetRuntimeVersionReport CreateDotNetRuntimeReport(Version? running, string? recommended)
    {
        return CreateDotNetRuntimeReport(OmpRuntimeAdvisory.Compare(running, recommended));
    }

    private static OmpDotNetRuntimeVersionReport CreateDotNetRuntimeReport(OmpRuntimeAdvisoryResult advisory)
    {
        return new OmpDotNetRuntimeVersionReport(
            FormatRuntimeVersion(advisory.Running),
            FormatRuntimeVersion(advisory.Recommended),
            advisory.IsOutdated);
    }

    private static string? FormatRuntimeVersion(Version? version)
    {
        if (version is null)
        {
            return null;
        }

        return version.Build < 0
            ? $"{version.Major}.{version.Minor}.0"
            : version.ToString(3);
    }

    private static OmpLoadedAssemblyVersion Describe(string assemblyName, Version? expectedBand)
    {
        var version = ResolveLoadedVersion(assemblyName);

        string? warning = null;
        if (expectedBand is not null)
        {
            if (version is null)
            {
                warning = $"{assemblyName} could not be resolved in this process.";
            }
            else if (!IsOnBand(version, expectedBand))
            {
                warning = $"{assemblyName} {version} is not on the expected {expectedBand}.x band.";
            }
        }

        return new OmpLoadedAssemblyVersion(
            assemblyName,
            version?.ToString(),
            expectedBand?.ToString(),
            warning);
    }

    private static Version? ResolveLoadedVersion(string assemblyName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));

        if (loaded is not null)
        {
            return loaded.GetName().Version;
        }

        // Not loaded yet (for example Negotiate under IIS in-process hosting):
        // load it now so the report reflects the version this host would resolve.
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName)).GetName().Version;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or System.IO.FileNotFoundException
                or System.IO.FileLoadException
                or BadImageFormatException)
        {
            return null;
        }
    }
}

internal sealed record OmpLoadedAssemblyVersion(
    string AssemblyName,
    string? LoadedVersion,
    string? ExpectedBand,
    string? Warning);

internal sealed record OmpRuntimeAssemblyVersionReport(
    IReadOnlyList<OmpLoadedAssemblyVersion> Assemblies,
    bool HasWarnings);

internal sealed record OmpDotNetRuntimeVersionReport(
    string? Running,
    string? Recommended,
    bool IsOutdated);
