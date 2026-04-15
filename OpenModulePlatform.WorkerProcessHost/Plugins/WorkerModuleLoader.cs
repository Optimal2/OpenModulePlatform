// File: OpenModulePlatform.WorkerProcessHost/Plugins/WorkerModuleLoader.cs
using System.Reflection;
using OpenModulePlatform.Worker.Abstractions.Contracts;

namespace OpenModulePlatform.WorkerProcessHost.Plugins;

/// <summary>
/// Loads a worker module factory from a dedicated plugin assembly.
/// </summary>
public sealed class WorkerModuleLoader
{
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
}
