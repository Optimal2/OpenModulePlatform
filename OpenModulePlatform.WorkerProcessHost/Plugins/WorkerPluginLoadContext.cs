// File: OpenModulePlatform.WorkerProcessHost/Plugins/WorkerPluginLoadContext.cs
using System.Reflection;
using System.Runtime.Loader;

namespace OpenModulePlatform.WorkerProcessHost.Plugins;

/// <summary>
/// Resolves managed and native dependencies relative to a specific worker plugin assembly.
/// </summary>
internal sealed class WorkerPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public WorkerPluginLoadContext(string pluginAssemblyPath)
        : base($"WorkerPlugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null
            ? null
            : LoadFromAssemblyPath(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null
            ? nint.Zero
            : LoadUnmanagedDllFromPath(libraryPath);
    }
}
