// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/SqlConnectionFactory.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

/// <summary>
/// Creates SQL connections for the example service worker.
/// </summary>
public sealed class SqlConnectionFactory
{
    private readonly IConfiguration _configuration;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SqlConnection Create()
    {
        var connectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing connection string: ConnectionStrings:OmpDb");
        }

        return new SqlConnection(connectionString);
    }
}
