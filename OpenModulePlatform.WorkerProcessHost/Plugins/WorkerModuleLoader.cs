// File: OpenModulePlatform.WorkerProcessHost/Plugins/WorkerModuleLoader.cs
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.WorkerProcessHost.Plugins;

/// <summary>
/// Loads a worker module factory from a dedicated plugin assembly.
/// </summary>
public sealed class WorkerModuleLoader
{
    private readonly ILogger<WorkerModuleLoader> _logger;

    public WorkerModuleLoader(ILogger<WorkerModuleLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<WorkerModuleLoader>.Instance;
    }

    public IWorkerModuleFactory LoadFactory(
        string pluginAssemblyPath,
        string workerTypeKey,
        string? pluginArtifactRootPath,
        string? workerHostComponentKey,
        string? workerHostVersion)
    {
        ValidateWorkerHostCompatibility(
            pluginAssemblyPath,
            pluginArtifactRootPath,
            workerHostComponentKey,
            workerHostVersion);
        return LoadFactory(pluginAssemblyPath, workerTypeKey);
    }

    public IWorkerModuleFactory LoadFactory(string pluginAssemblyPath, string workerTypeKey)
    {
        if (string.IsNullOrWhiteSpace(pluginAssemblyPath))
        {
            throw new ArgumentException("Plugin assembly path must be provided.", nameof(pluginAssemblyPath));
        }

        if (string.IsNullOrWhiteSpace(workerTypeKey))
        {
            throw new ArgumentException("Worker type key must be provided.", nameof(workerTypeKey));
        }

        var fullPluginAssemblyPath = Path.GetFullPath(pluginAssemblyPath);
        if (!File.Exists(fullPluginAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Worker plugin assembly was not found: '{fullPluginAssemblyPath}'.",
                fullPluginAssemblyPath);
        }

        var loadContext = new WorkerPluginLoadContext(fullPluginAssemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(fullPluginAssemblyPath);

        var factories = GetFactoryTypes(assembly)
            .Select(CreateFactory)
            .ToList();

        if (factories.Count == 0)
        {
            throw new InvalidOperationException(
                $"No public {nameof(IWorkerModuleFactory)} implementations were found in '{fullPluginAssemblyPath}'.");
        }

        var matches = factories
            .Where(factory => string.Equals(factory.WorkerTypeKey, workerTypeKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var availableKeys = string.Join(", ", factories.Select(factory => factory.WorkerTypeKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple worker factories matched WorkerTypeKey '{workerTypeKey}' in '{fullPluginAssemblyPath}'. Available keys: {availableKeys}.");
        }

        throw new InvalidOperationException(
            $"No worker factory matched WorkerTypeKey '{workerTypeKey}' in '{fullPluginAssemblyPath}'. Available keys: {availableKeys}.");
    }

    private static IEnumerable<Type> GetFactoryTypes(Assembly assembly)
    {
        try
        {
            return assembly
                .GetExportedTypes()
                .Where(type =>
                    type is { IsAbstract: false, IsInterface: false }
                    && typeof(IWorkerModuleFactory).IsAssignableFrom(type));
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderDetails = string.Join(
                Environment.NewLine,
                ex.LoaderExceptions
                    .Where(loaderException => loaderException is not null)
                    .Select(loaderException => $" - {loaderException!.Message}"));

            throw new InvalidOperationException(
                $"Failed to inspect worker plugin assembly '{assembly.Location}'.{Environment.NewLine}{loaderDetails}",
                ex);
        }
    }

    private static IWorkerModuleFactory CreateFactory(Type factoryType)
    {
        var constructor = factoryType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                $"Worker factory type '{factoryType.FullName}' must expose a public parameterless constructor.");
        }

        if (constructor.Invoke(null) is not IWorkerModuleFactory factory)
        {
            throw new InvalidOperationException(
                $"Worker factory type '{factoryType.FullName}' could not be instantiated.");
        }

        if (string.IsNullOrWhiteSpace(factory.WorkerTypeKey))
        {
            throw new InvalidOperationException(
                $"Worker factory type '{factoryType.FullName}' returned an empty WorkerTypeKey.");
        }

        return factory;
    }

    private void ValidateWorkerHostCompatibility(
        string pluginAssemblyPath,
        string? pluginArtifactRootPath,
        string? workerHostComponentKey,
        string? workerHostVersion)
    {
        string? manifestPath;
        if (string.IsNullOrWhiteSpace(pluginArtifactRootPath))
        {
            // Managers older than the compatibility contract do not send the artifact-root
            // or host-version arguments. Preserve marker-free legacy plugins, but walk from
            // the assembly directory to its ancestors so nested plugin layouts cannot hide a
            // payload-root marker from the fail-closed version check.
            manifestPath = FindCompatibilityManifestInAssemblyAncestors(pluginAssemblyPath);
            _logger.LogWarning(
                "WorkerProcess:PluginArtifactRootPath was not supplied. Compatibility metadata is searched from the plugin assembly directory through its ancestors; upgrade WorkerManager to pass the complete worker-host identity.");
        }
        else
        {
            manifestPath = Path.Join(
                Path.GetFullPath(pluginArtifactRootPath),
                WorkerPluginCompatibilityManifest.FileName);
        }

        if (manifestPath is null || !File.Exists(manifestPath))
        {
            return;
        }

        WorkerPluginCompatibilityManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorkerPluginCompatibilityManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidOperationException("Compatibility metadata is empty.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Worker plugin compatibility metadata could not be read: '{manifestPath}'.",
                ex);
        }

        if (manifest.FormatVersion != 1 || manifest.WorkerHost is null
            || string.IsNullOrWhiteSpace(manifest.WorkerHost.ComponentKey)
            || string.IsNullOrWhiteSpace(manifest.WorkerHost.MinVersion))
        {
            throw new InvalidOperationException(
                $"Worker plugin compatibility metadata is invalid: '{manifestPath}'.");
        }

        var requiredComponentKey = manifest.WorkerHost.ComponentKey.Trim();
        var requiredVersion = manifest.WorkerHost.MinVersion.Trim();
        var currentComponentKey = workerHostComponentKey?.Trim();
        var currentVersion = workerHostVersion?.Trim();

        if (!string.Equals(requiredComponentKey, currentComponentKey, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(currentVersion)
            || ArtifactVersionComparer.Compare(currentVersion, requiredVersion) < 0)
        {
            var action = string.IsNullOrWhiteSpace(currentVersion)
                ? $"Select a versioned '{requiredComponentKey}' artifact instead of an unverifiable manual worker-host path."
                : $"Upgrade '{requiredComponentKey}' to {requiredVersion} or later before starting this worker plugin.";
            throw new InvalidOperationException(
                $"Worker plugin '{Path.GetFileName(pluginAssemblyPath)}' requires component '{requiredComponentKey}' version {requiredVersion} or later, " +
                $"but the running worker host component is '{currentComponentKey ?? "<unknown>"}' version {currentVersion ?? "<unknown>"}. " +
                action);
        }
    }

    private static string? FindCompatibilityManifestInAssemblyAncestors(string pluginAssemblyPath)
    {
        var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(pluginAssemblyPath))
            ?? throw new InvalidOperationException(
                $"Worker plugin assembly has no directory: '{pluginAssemblyPath}'.");
        for (var directory = new DirectoryInfo(assemblyDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Join(directory.FullName, WorkerPluginCompatibilityManifest.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
