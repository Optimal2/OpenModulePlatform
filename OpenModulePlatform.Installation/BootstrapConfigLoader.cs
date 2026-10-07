using System.Text.Json;

namespace OpenModulePlatform.Installation;

/// <summary>
/// Loads bootstrap.json host profiles into the shared configuration model
/// (<see cref="BootstrapConfig"/> and related types).
/// </summary>
public static class BootstrapConfigLoader
{
    /// <summary>
    /// The JSON serializer options used for every bootstrap configuration read and
    /// write (web defaults, trailing commas and comments allowed, indented output).
    /// </summary>
    public static JsonSerializerOptions SerializerOptions => InstallationEngine.JsonOptions;

    /// <summary>
    /// Reads a bootstrap.json file into <see cref="BootstrapConfig"/>. Throws
    /// <see cref="FileNotFoundException"/> when the file does not exist, exactly as
    /// the Bootstrapper always has.
    /// </summary>
    public static Task<BootstrapConfig> LoadAsync(string path)
        => InstallationEngine.ReadJsonAsync<BootstrapConfig>(path);
}
