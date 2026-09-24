// File: OpenModulePlatform.WorkerManager.WindowsService/Services/SystemLogNLogTarget.cs
using Microsoft.Extensions.Configuration;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

/// <summary>
/// Attaches the platform's system log to NLog in code: an async database
/// target that writes everything at Warn and above to omp.SystemLog.
/// The WorkerManager runs on an appsettings.json the HostAgent generates and
/// re-stamps (ArtifactConfigurationFileWriter), so a target declared in the
/// repository's JSON never reaches an installed WorkerManager. Done here it
/// follows the binary instead. A target named "systemlog" already in the
/// configuration (a JSON section that declares it, as in development) is left
/// alone, and no connection string means no target: the files stay as they
/// are. The HostAgent carries a twin of this class, since the two services
/// share no project.
/// </summary>
internal static class SystemLogNLogTarget
{
    public const string TargetName = "systemlog";

    public static void Attach(IConfiguration configuration, string appName)
    {
        var connectionString = configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var config = LogManager.Configuration ?? new LoggingConfiguration();
        if (config.FindTargetByName(TargetName) is not null)
        {
            return;
        }

        var database = new DatabaseTarget(TargetName + "-db")
        {
            DBProvider = "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
            ConnectionString = connectionString,
            KeepConnection = false,
            CommandText = "INSERT INTO omp.SystemLog (LoggedUtc, HostName, ProcessName, Level, Logger, Message, Exception, CorrelationId) " +
                          "VALUES (@LoggedUtc, @HostName, @ProcessName, @Level, @Logger, @Message, @Exception, @CorrelationId)"
        };
        database.Parameters.Add(new DatabaseParameterInfo("@LoggedUtc", "${date:universalTime=true:format=yyyy-MM-dd HH\\:mm\\:ss.fff}") { DbType = "DbType.DateTime2" });
        database.Parameters.Add(new DatabaseParameterInfo("@HostName", "${machinename}") { DbType = "DbType.String", Size = 128 });
        database.Parameters.Add(new DatabaseParameterInfo("@ProcessName", appName) { DbType = "DbType.String", Size = 128 });
        database.Parameters.Add(new DatabaseParameterInfo("@Level", "${level}") { DbType = "DbType.String", Size = 16 });
        database.Parameters.Add(new DatabaseParameterInfo("@Logger", "${logger}") { DbType = "DbType.String", Size = 256 });
        database.Parameters.Add(new DatabaseParameterInfo("@Message", "${message}") { DbType = "DbType.String" });
        database.Parameters.Add(new DatabaseParameterInfo("@Exception", "${exception:format=tostring}") { DbType = "DbType.String", AllowDbNull = true });
        database.Parameters.Add(new DatabaseParameterInfo("@CorrelationId", "${scopeproperty:item=CorrelationId}") { DbType = "DbType.String", Size = 64, AllowDbNull = true });

        // Async with discard on overflow: logging must never slow or stop
        // the service when the database is unreachable; the file keeps it.
        var wrapper = new AsyncTargetWrapper(TargetName, database)
        {
            OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
            QueueLimit = 2000
        };
        config.AddTarget(wrapper);
        // First among the rules, so a "final" rule below cannot keep a warning
        // from the table.
        config.LoggingRules.Insert(0, new LoggingRule("*", NLog.LogLevel.Warn, wrapper));
        LogManager.Configuration = config;
    }
}
