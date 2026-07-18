using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests;

/// <summary>
/// Resolves the SQL Server instance used by database-backed tests. Set the
/// OMP_TEST_CONNECTION_STRING environment variable to override the server
/// (for example SQL Server LocalDB on CI runners); the requested test
/// database name is always applied on top of the resolved server settings.
/// </summary>
internal static class TestSqlConnection
{
    public static string ForDatabase(string databaseName)
    {
        var overrideString = Environment.GetEnvironmentVariable("OMP_TEST_CONNECTION_STRING");
        var builder = string.IsNullOrWhiteSpace(overrideString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }
            : new SqlConnectionStringBuilder(overrideString);

        builder.InitialCatalog = databaseName;
        return builder.ConnectionString;
    }
}
