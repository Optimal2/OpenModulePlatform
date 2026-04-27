// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/AppInstanceRepository.cs
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

/// <summary>
/// Reads runtime values for a concrete <c>omp.AppInstance</c>.
/// </summary>
public sealed class AppInstanceRepository
{
    private readonly SqlConnectionFactory _db;

    public AppInstanceRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>
    /// Runtime values consumed by the sample worker loop.
    /// </summary>
    public sealed record AppInstanceRuntime(
        Guid AppInstanceId,
        int AppId,
        string AppKey,
        bool IsAllowed,
        byte DesiredState,
        int? ConfigId);

    public async Task<AppInstanceRuntime?> GetRuntimeAsync(
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       ai.AppId,
       a.AppKey,
       ai.IsAllowed,
       ai.DesiredState,
       ai.ConfigId
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
WHERE ai.AppInstanceId = @appInstanceId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppInstanceRuntime(
            rdr.GetGuid(0),
            rdr.GetInt32(1),
            rdr.GetString(2),
            rdr.GetBoolean(3),
            rdr.GetByte(4),
            rdr.IsDBNull(5) ? null : rdr.GetInt32(5));
    }
}
