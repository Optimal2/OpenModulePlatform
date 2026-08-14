using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Pure, stateless helpers for resolving service app deployment names and paths.
/// Kept separate from <see cref="ServiceAppDeploymentService"/> so the logic can be unit tested.
/// </summary>
internal static class ServiceAppDeploymentNaming
{
    private static readonly char[] InvalidServiceNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    internal static string ResolveServiceName(ServiceAppDeploymentDescriptor deployment, string executableRelativePath)
    {
        var configuredName = Clean(deployment.InstallationName);
        var serviceName = IsGenericInstallationName(configuredName)
            ? Path.GetFileNameWithoutExtension(executableRelativePath)
            : configuredName!;

        ValidateServiceName(serviceName, deployment.AppInstanceKey);
        return serviceName;
    }

    internal static string ResolveTargetPath(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string serviceName)
    {
        var installPath = Clean(deployment.InstallPath);
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            if (Path.IsPathRooted(installPath))
            {
                return Path.GetFullPath(installPath);
            }

            return DeploymentPath.CombineUnderRoot(
                settings.ServicesRoot.Trim(),
                installPath,
                $"Service app instance '{deployment.AppInstanceKey}' InstallPath");
        }

        var folderName = SanitizeFolderName(serviceName);
        return DeploymentPath.CombineUnderRoot(
            settings.ServicesRoot.Trim(),
            folderName,
            $"Service app instance '{deployment.AppInstanceKey}' folder name");
    }

    internal static bool IsGenericInstallationName(string? value)
        => string.IsNullOrWhiteSpace(value)
            || value.Equals("default", StringComparison.OrdinalIgnoreCase)
            || value.Equals("service", StringComparison.OrdinalIgnoreCase)
            || value.Equals("serviceapp", StringComparison.OrdinalIgnoreCase)
            || value.Equals("backend", StringComparison.OrdinalIgnoreCase)
            || value.Equals("worker", StringComparison.OrdinalIgnoreCase)
            || value.Equals("app", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a service name looks like the legacy/unprefixed twin of a
    /// canonical service name for the same logical app. A twin is either the canonical
    /// executable file name without extension (the service name a generic
    /// InstallationName would have produced) or the canonical name with its first
    /// prefix segment removed (for example 'iKrock2.Backend' vs 'OMP.iKrock2.Backend').
    /// Callers must additionally verify that both services point to the same
    /// executable file name before treating the candidate as a duplicate.
    /// </summary>
    internal static bool IsLegacyTwinServiceName(
        string candidateServiceName,
        string canonicalServiceName,
        string? canonicalExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(candidateServiceName)
            || string.IsNullOrWhiteSpace(canonicalServiceName)
            || string.Equals(candidateServiceName, canonicalServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var executableName = Path.GetFileNameWithoutExtension(canonicalExecutablePath);
        if (!string.IsNullOrWhiteSpace(executableName)
            && string.Equals(candidateServiceName.Trim(), executableName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return canonicalServiceName.EndsWith(
            "." + candidateServiceName.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an old service/folder should be cleaned up because the
    /// deployment's runtime name changed. Returns a reason string when cleanup is skipped.
    /// </summary>
    internal static RenameCleanupEvaluation EvaluateRenameCleanup(
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string executableRelativePath,
        string newServiceName,
        string newTargetPath,
        IReadOnlyDictionary<Guid, string> resolvedServiceNamesByAppInstanceId,
        IReadOnlyList<HostRuntimeFootprint> hostRuntimeFootprints)
    {
        var oldServiceName = Clean(deployment.DeployedRuntimeName);
        if (string.IsNullOrWhiteSpace(oldServiceName))
        {
            return RenameCleanupEvaluation.Skip(null, null, "No previously deployed runtime name is tracked.");
        }

        if (string.Equals(oldServiceName, newServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return RenameCleanupEvaluation.Skip(oldServiceName, null, "The deployed runtime name matches the new service name.");
        }

        var hostAgentServiceName = Clean(settings.ServiceName);
        if (!string.IsNullOrWhiteSpace(hostAgentServiceName)
            && string.Equals(oldServiceName, hostAgentServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return RenameCleanupEvaluation.Skip(
                oldServiceName,
                null,
                $"The old runtime name '{oldServiceName}' matches the HostAgent service name.");
        }

        // WorkerManager does not live in HostAgentSettings, but the guardrail needs a known
        // constant name. The default OMP WorkerManager service name is OMP.WorkerManager.
        if (string.Equals(oldServiceName, "OMP.WorkerManager", StringComparison.OrdinalIgnoreCase))
        {
            return RenameCleanupEvaluation.Skip(
                oldServiceName,
                null,
                $"The old runtime name '{oldServiceName}' matches the WorkerManager service name.");
        }

        // Safety: ensure no other active deployment descriptor resolves to the old service name.
        foreach (var pair in resolvedServiceNamesByAppInstanceId)
        {
            if (pair.Key == deployment.AppInstanceId)
            {
                continue;
            }

            if (string.Equals(pair.Value, oldServiceName, StringComparison.OrdinalIgnoreCase))
            {
                return RenameCleanupEvaluation.Skip(
                    oldServiceName,
                    null,
                    $"Another active app instance resolves to the old runtime name '{oldServiceName}'.");
            }
        }

        // The check above can only see the app instances that fit inside this cycle's
        // artifact cap, and it silently loses any whose name failed to resolve in the
        // pre-pass. Neither limitation is acceptable for an operation that ends in a
        // recursive directory delete, so the same question is asked again against every
        // footprint recorded on the host (R7-D4).
        foreach (var footprint in hostRuntimeFootprints)
        {
            if (footprint.AppInstanceId == deployment.AppInstanceId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(footprint.RuntimeName)
                && string.Equals(footprint.RuntimeName.Trim(), oldServiceName, StringComparison.OrdinalIgnoreCase))
            {
                return RenameCleanupEvaluation.Skip(
                    oldServiceName,
                    null,
                    $"Another app instance on this host is deployed as runtime '{oldServiceName}'.");
            }
        }

        var oldTargetPathDeployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceKey = deployment.AppInstanceKey,
            InstallPath = deployment.InstallPath,
            InstallationName = oldServiceName
        };
        var oldTargetPath = ResolveTargetPath(settings, oldTargetPathDeployment, oldServiceName);

        // A rename that does not move the files has nothing to delete. This used to be
        // checked at delete time only, which meant the log said "cleaned up" for a
        // directory that was never touched.
        if (PathsMayCollide(oldTargetPath, newTargetPath))
        {
            return RenameCleanupEvaluation.Skip(
                oldServiceName,
                oldTargetPath,
                "The old and new deployments share a target path, so only the service name changed.");
        }

        // The finding this guard exists for: cleanup compared names and nothing else, so
        // an old name whose folder happens to be another instance's live directory would
        // have taken that directory with it. Comparing the resolved paths is the check
        // that actually protects the files.
        foreach (var footprint in hostRuntimeFootprints)
        {
            if (footprint.AppInstanceId == deployment.AppInstanceId
                || string.IsNullOrWhiteSpace(footprint.TargetPath))
            {
                continue;
            }

            if (PathsMayCollide(footprint.TargetPath, oldTargetPath))
            {
                return RenameCleanupEvaluation.Skip(
                    oldServiceName,
                    oldTargetPath,
                    $"Another app instance on this host is deployed under '{oldTargetPath}'.");
            }
        }

        return RenameCleanupEvaluation.Clean(oldServiceName, oldTargetPath, ResolveExpectedExecutableFileName(executableRelativePath));
    }

    /// <summary>
    /// The executable file name the old directory must contain for it to be a previous
    /// deployment of this same app instance.
    /// </summary>
    /// <remarks>
    /// <c>executableRelativePath</c> was passed into rename evaluation and never read.
    /// It is the one piece of evidence that distinguishes "our own directory under an
    /// older name" from "a directory that merely resolved to the same place", so the
    /// caller checks the old directory's contents against it before deleting anything.
    /// </remarks>
    private static string? ResolveExpectedExecutableFileName(string? executableRelativePath)
    {
        var fileName = string.IsNullOrWhiteSpace(executableRelativePath)
            ? null
            : Path.GetFileName(executableRelativePath.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    /// <summary>
    /// True when the two paths cannot be shown to point at different directories.
    /// </summary>
    /// <remarks>
    /// Every caller uses a <c>false</c> here as permission to delete a directory tree, so
    /// a path that will not normalise answers <c>true</c>. Refusing a legitimate cleanup
    /// leaves a stale folder and a logged reason; the opposite mistake deletes a live
    /// installation.
    /// </remarks>
    internal static bool PathsMayCollide(string? left, string? right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        if (normalizedLeft is null || normalizedRight is null)
        {
            return true;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
            || normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ResolveExecutableRelativePath(ServiceAppDeploymentDescriptor deployment)
    {
        if (!Directory.Exists(deployment.SourceLocalPath))
        {
            throw new DirectoryNotFoundException($"Provisioned service app artifact path was not found: '{deployment.SourceLocalPath}'.");
        }

        var executables = Directory.EnumerateFiles(deployment.SourceLocalPath, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(deployment.SourceLocalPath, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (executables.Length == 0)
        {
            throw new InvalidOperationException(
                $"Service app artifact '{deployment.ArtifactId}' contains no executable in its root directory.");
        }

        if (executables.Length == 1)
        {
            return executables[0];
        }

        var installationName = Clean(deployment.InstallationName);
        if (!IsGenericInstallationName(installationName))
        {
            var expected = installationName + ".exe";
            var match = executables.FirstOrDefault(
                path => string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        throw new InvalidOperationException(
            $"Service app artifact '{deployment.ArtifactId}' contains more than one root executable. Set AppInstances.InstallationName to the Windows service/executable name.");
    }

    private static void ValidateServiceName(string serviceName, string appInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceName)
            || serviceName.IndexOfAny(InvalidServiceNameCharacters) >= 0
            || serviceName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"App instance '{appInstanceKey}' resolved an invalid Windows service name.");
        }
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "service-app" : sanitized;
    }

    internal static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record RenameCleanupEvaluation(
    bool ShouldCleanUp,
    string? OldServiceName,
    string? OldTargetPath,
    string? Reason,
    string? ExpectedExecutableFileName = null)
{
    public static RenameCleanupEvaluation Clean(
        string oldServiceName,
        string oldTargetPath,
        string? expectedExecutableFileName)
        => new(true, oldServiceName, oldTargetPath, null, expectedExecutableFileName);

    public static RenameCleanupEvaluation Skip(string? oldServiceName, string? oldTargetPath, string reason)
        => new(false, oldServiceName, oldTargetPath, reason);
}
