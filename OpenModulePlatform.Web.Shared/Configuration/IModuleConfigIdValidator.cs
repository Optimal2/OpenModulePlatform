namespace OpenModulePlatform.Web.Shared.Configuration;

/// <summary>
/// Opt-in validator for the module-owned <c>ConfigId</c> bridge.
/// </summary>
/// <remarks>
/// Modules register an implementation in their DI container when they want the Portal and
/// runtime surfaces to validate that a selected <see cref="ModuleConfigId"/> exists in the
/// module-owned <c>Configurations</c> table. If no implementation is registered, the bridge
/// behaves exactly as before: any positive integer (or <c>null</c>) is accepted.
/// </remarks>
public interface IModuleConfigIdValidator
{
    /// <summary>
    /// Returns <see langword="true"/> when the supplied config id exists in the module's
    /// configuration store.
    /// </summary>
    Task<bool> ExistsAsync(ModuleConfigId configId, CancellationToken ct);
}
