using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    /// <summary>
    /// Adds app home entries after module initialization, preserving all existing rows.
    /// Deleting an entry is temporary; administrators must disable it to keep it hidden.
    /// </summary>
    internal async Task<int> InsertMissingPortalAppEntriesAsync(
        CancellationToken ct, Action<int>? onAttempt = null)
    {
        await using var connection = _db.Create();
        return await PortalAppEntrySync.InsertMissingAsync(connection, onAttempt, ct);
    }
}
