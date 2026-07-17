// File: OpenModulePlatform.Auth/Services/OmpRuntimeAssemblyVersionCheck.cs
using System.Reflection;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// Reports the versions of security-critical assemblies that are actually loaded
/// in the running process, so operators can verify that a deployed host really
/// runs the patched Negotiate authentication stack (CVE-2026-47303).
/// </summary>
internal static class OmpRuntimeAssemblyVersionCheck
{
    public const string NegotiateAssemblyName = "Microsoft.AspNetCore.Authentication.Negotiate";
    public const string AuthAssemblyName = "OpenModulePlatform.Auth";

    // Patched band for CVE-2026-47303: any 10.0.10.x revision is acceptable.
    public static readonly Version ExpectedNegotiateBand = new(10, 0, 10);

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
        catch (Exception)
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
