using System.Data.Common;

namespace OpenModulePlatform.Portal.Tests.Services;

internal static class OwnedConnectionOpening
{
    internal static async Task<T> OpenAsync<T>(T connection, CancellationToken cancellationToken = default)
        where T : DbConnection
    {
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
