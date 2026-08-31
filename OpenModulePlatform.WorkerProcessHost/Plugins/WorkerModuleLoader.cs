// File: OpenModulePlatform.WorkerProcessHost/Plugins/WorkerModuleLoader.cs
using System.Reflection;
using System.Text.Json;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.WorkerProcessHost.Plugins;

/// <summary>
/// Loads a worker module factory from a dedicated plugin assembly.
/// </summary>
public sealed class WorkerModuleLoader
{
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

    private static void ValidateWorkerHostCompatibility(
        string pluginAssemblyPath,
        string? pluginArtifactRootPath,
        string? workerHostComponentKey,
        string? workerHostVersion)
    {
        if (string.IsNullOrWhiteSpace(pluginArtifactRootPath))
        {
            return;
        }

        var manifestPath = Path.Join(
            Path.GetFullPath(pluginArtifactRootPath),
            WorkerPluginCompatibilityManifest.FileName);
        if (!File.Exists(manifestPath))
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
}
