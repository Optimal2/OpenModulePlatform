using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.ModuleDefinitions;

// Source-linked by Portal and HostAgent: one evidence contract and one lock scope.
internal static class ModuleDefinitionSqlCompletion
{
    internal static async Task<IReadOnlyList<string>> GetPendingAsync(
        SqlConnection connection,
        int documentId,
        IEnumerable<(string Key, string? Text)> scripts,
        CancellationToken ct)
    {
        var pending = new List<string>();
        foreach (var (key, text) in scripts)
        {
            // Path-only and empty payloads cannot be executed by either runtime.
            if (string.IsNullOrWhiteSpace(text))
                continue;

            await using var command = new SqlCommand(@"
SELECT COUNT(1) FROM omp.ModuleDefinitionSqlExecutions
WHERE ModuleDefinitionDocumentId = @documentId AND ScriptKey = @key
  AND ScriptSha256 = @hash AND ExecutionStatus = N'Succeeded';", connection);
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0)
                pending.Add(key);
        }
        return pending;
    }

    internal static async Task VerifyAsync(SqlConnection connection, int documentId,
        string moduleKey, string definitionVersion, IReadOnlyList<string> pending, ILogger logger, CancellationToken ct)
    {
        if (pending.Count == 0)
            return;
        await using var command = new SqlCommand(
            "SELECT IsApplied FROM omp.ModuleDefinitionDocuments WHERE ModuleDefinitionDocumentId = @id;", connection);
        command.Parameters.AddWithValue("@id", documentId);
        if (await command.ExecuteScalarAsync(ct) is not true)
            return;

        logger.LogError(
            "Module definition '{ModuleKey}' {DefinitionVersion} is marked applied, but {Count} declared script(s) have no successful execution for their current content: {Scripts}.",
            moduleKey, definitionVersion, pending.Count, string.Join(", ", pending));
        throw new InvalidOperationException(
            $"Module definition '{moduleKey}' {definitionVersion} is marked applied, "
            + $"but {pending.Count} declared script(s) have no successful execution for their current content: {string.Join(", ", pending)}. "
            + "The import is marked failed; re-import once the scripts can run.");
    }
}

internal sealed class ModuleDefinitionSqlRepairScope(SqlConnection connection) : IAsyncDisposable
{
    // Session ownership permits independently committed DDL batches. Explicit
    // release is essential: disposing a pooled connection does not end its session.
    private const string Resource = "omp.module-definition-sql-repair";
    internal SqlConnection Connection => connection;

    internal static async Task<ModuleDefinitionSqlRepairScope> AcquireAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(@"
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
    @LockOwner = N'Session', @LockTimeout = 60000;
SELECT @result;", connection) { CommandTimeout = 65 };
            command.Parameters.AddWithValue("@resource", Resource);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0)
                throw new InvalidOperationException("Another module definition SQL repair is already running.");
            return new(connection);
        }
        catch
        {
            // Cancellation may race acquisition. Never pool a possibly locked session.
            SqlConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = new SqlCommand(
                    "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';", connection);
                command.Parameters.AddWithValue("@resource", Resource);
                await command.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            SqlConnection.ClearPool(connection);
            throw;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
