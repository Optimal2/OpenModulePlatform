// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/ExampleWorkerAppModuleConfigurationRepository.cs
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

public sealed class ExampleWorkerAppModuleConfigurationRepository
{
    private readonly SqlConnectionFactory _db;

    public ExampleWorkerAppModuleConfigurationRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<string?> GetConfigurationJsonAsync(int configId, CancellationToken ct)
    {
        const string sql = @"
SELECT c.ConfigJson
FROM omp_example_workerapp.Configurations c
WHERE c.ConfigId = @configId AND c.VersionNo = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@configId", configId);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null || value is DBNull)
            return null;

        return (string)value;
    }
}
