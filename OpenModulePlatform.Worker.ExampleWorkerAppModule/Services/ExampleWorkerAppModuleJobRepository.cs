// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/ExampleWorkerAppModuleJobRepository.cs
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

public sealed class ExampleWorkerAppModuleJobRepository
{
    private readonly SqlConnectionFactory _db;

    public ExampleWorkerAppModuleJobRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<ExampleWorkerAppModuleJobWorkItem?> TryClaimNextAsync(Guid appInstanceId, CancellationToken ct)
    {
        const string sql = @"
;WITH next_job AS
(
    SELECT TOP (1) j.JobId
    FROM omp_example_workerapp_module.Jobs j WITH (READPAST, UPDLOCK, ROWLOCK)
    WHERE j.Status = 0
    ORDER BY j.RequestedUtc, j.JobId
)
UPDATE j
SET Status = 1,
    Attempts = Attempts + 1,
    ClaimedByAppInstanceId = @appInstanceId,
    ClaimedUtc = SYSUTCDATETIME(),
    UpdatedUtc = SYSUTCDATETIME(),
    LastError = NULL
OUTPUT INSERTED.JobId,
       INSERTED.RequestType,
       INSERTED.PayloadJson,
       INSERTED.RequestedUtc,
       INSERTED.RequestedBy
FROM omp_example_workerapp_module.Jobs j
INNER JOIN next_job n ON n.JobId = j.JobId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
            return null;

        return new ExampleWorkerAppModuleJobWorkItem
        {
            JobId = rdr.GetInt64(0),
            RequestType = rdr.GetString(1),
            PayloadJson = rdr.GetString(2),
            RequestedUtc = rdr.GetDateTime(3),
            RequestedBy = rdr.IsDBNull(4) ? null : rdr.GetString(4)
        };
    }

    public async Task CompleteAsync(long jobId, Guid appInstanceId, DateTime startedUtc, string resultJson, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_example_workerapp_module.Jobs
SET Status = 2,
    CompletedUtc = SYSUTCDATETIME(),
    ResultJson = @resultJson,
    LastError = NULL,
    UpdatedUtc = SYSUTCDATETIME()
WHERE JobId = @jobId;

INSERT INTO omp_example_workerapp_module.JobExecutions(JobId, AppInstanceId, StartedUtc, FinishedUtc, Outcome, ResultJson, ErrorMessage)
VALUES(@jobId, @appInstanceId, @startedUtc, SYSUTCDATETIME(), N'Completed', @resultJson, NULL);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@startedUtc", startedUtc);
        cmd.Parameters.AddWithValue("@resultJson", resultJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(long jobId, Guid appInstanceId, DateTime startedUtc, string errorMessage, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_example_workerapp_module.Jobs
SET Status = 3,
    CompletedUtc = SYSUTCDATETIME(),
    LastError = @errorMessage,
    UpdatedUtc = SYSUTCDATETIME()
WHERE JobId = @jobId;

INSERT INTO omp_example_workerapp_module.JobExecutions(JobId, AppInstanceId, StartedUtc, FinishedUtc, Outcome, ResultJson, ErrorMessage)
VALUES(@jobId, @appInstanceId, @startedUtc, SYSUTCDATETIME(), N'Failed', NULL, @errorMessage);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@startedUtc", startedUtc);
        cmd.Parameters.AddWithValue("@errorMessage", errorMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
