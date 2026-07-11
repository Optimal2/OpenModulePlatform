using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Factory for creating scoped <see cref="SqlConnection" /> instances.
/// Extracted as an interface to enable deterministic testing of repository consumers.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Returns the configured OMP database connection string.
    /// </summary>
    string GetConnectionString();

    /// <summary>
    /// Creates a new <see cref="SqlConnection" /> using the configured connection string.
    /// The caller owns the connection and must dispose it.
    /// </summary>
    SqlConnection Create();
}
