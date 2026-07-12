using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Configuration;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Services;

/// <summary>
/// Opt-in validator that checks that a selected <see cref="ModuleConfigId"/> exists in the
/// module-owned <c>omp_example_serviceapp.Configurations</c> table.
/// </summary>
public sealed class ExampleServiceAppModuleConfigIdValidator : IModuleConfigIdValidator
{
    private readonly SqlConnectionFactory _db;

    public ExampleServiceAppModuleConfigIdValidator(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<bool> ExistsAsync(ModuleConfigId configId, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
WHERE EXISTS (
    SELECT 1
    FROM omp_example_serviceapp.Configurations
    WHERE ConfigId = @id
      AND VersionNo = 0
);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", (int)configId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }
}
