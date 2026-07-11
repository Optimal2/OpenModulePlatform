using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly IConfiguration _configuration;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:OmpDb is not configured.");
        }

        return connectionString;
    }

    public SqlConnection Create()
    {
        return new SqlConnection(GetConnectionString());
    }
}
