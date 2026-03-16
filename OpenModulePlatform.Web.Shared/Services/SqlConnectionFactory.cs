// File: OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Creates SQL connections for OMP web applications.
/// </summary>
/// <remarks>
/// The factory stays intentionally small so that repositories remain explicit about when
/// they open and close connections.
/// </remarks>
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
