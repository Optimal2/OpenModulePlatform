using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    /// <summary>Adds missing app home entries, preserving administrator customisations.</summary>
    internal async Task<int> InsertMissingPortalAppEntriesAsync(
        CancellationToken ct, Action<int>? onAttempt = null)
    {
        await using var connection = _db.Create();
        return await PortalAppEntrySync.InsertMissingAsync(connection, onAttempt, ct);
    }
}
