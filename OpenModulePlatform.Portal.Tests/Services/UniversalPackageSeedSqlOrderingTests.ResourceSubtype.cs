using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    /// <summary>
    /// Pins the measured contract of <c>sys.dm_tran_locks.resource_subtype</c> for a
    /// session-owned application lock, the lock the module-definition SQL repair
    /// uses.
    /// </summary>
    /// <remarks>
    /// The scoped repair-lock observation compares the resource identity it read
    /// from the held lock against the rows in <c>sys.dm_tran_locks</c>, and the
    /// comparison includes <c>resource_subtype</c>. A NULL in that column would
    /// break the string read and turn the comparison into <c>= NULL</c>, which
    /// matches no row at all, so a NULL there would silently report zero waiters.
    ///
    /// Measured on SQL Server 2025 (17.x, both the development instance and the
    /// LocalDB instance the CI gate uses): the DMV declares
    /// <c>resource_subtype nvarchar(60) NOT NULL</c>, and a session-owned
    /// application lock carries the empty string in it. This test holds a real
    /// application lock against the isolated fixture database and reads the real
    /// DMV row, so the contract is proved against the engine instead of a
    /// hand-written row. It fails loudly if the column ever becomes nullable.
    /// </remarks>
    [Fact]
    public async Task TheApplicationLockResourceSubtypeIsNeverNullAndMatchesTheHeldLock()
    {
        // The engine keeps a bounded part of the resource name in the DMV
        // description, so the name stays short enough to survive there.
        var resource = "omp.test.rst" + Guid.NewGuid().ToString("N")[..8];
        await using var holder = new SqlConnection(CreateNonPoolingConnectionString());
        await holder.OpenAsync();
        await AcquireSessionApplicationLockAsync(
            holder,
            resource,
            lockTimeoutMilliseconds: 0,
            CancellationToken.None,
            "Acquiring the observed application lock");

        try
        {
            using var spidCommand = new SqlCommand("SELECT @@SPID;", holder);
            var holderSpid = Convert.ToInt32(await spidCommand.ExecuteScalarAsync());
            using var databaseIdCommand = new SqlCommand("SELECT DB_ID();", holder);
            var databaseId = Convert.ToInt32(await databaseIdCommand.ExecuteScalarAsync());

            string? measuredSubtype;
            using (var read = new SqlCommand(
                """
                SELECT resource_description, resource_subtype, resource_associated_entity_id, resource_database_id
                FROM sys.dm_tran_locks
                WHERE request_session_id = @holderSpid
                  AND resource_database_id = DB_ID()
                  AND resource_type = 'APPLICATION'
                  AND request_mode = 'X'
                  AND request_status = 'GRANT'
                  AND request_owner_type = 'SESSION';
                """,
                holder))
            {
                read.Parameters.AddWithValue("@holderSpid", holderSpid);
                await using var reader = await read.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "The held application lock was not visible in sys.dm_tran_locks.");
                Assert.False(reader.IsDBNull(1), "sys.dm_tran_locks.resource_subtype was NULL for an application lock.");
                measuredSubtype = reader.GetString(1);
                Assert.Equal(string.Empty, measuredSubtype);

                // The rest of the identity the scoped observation matches on.
                Assert.False(reader.IsDBNull(0));
                Assert.Contains(resource, reader.GetString(0), StringComparison.Ordinal);
                Assert.Equal(0L, Convert.ToInt64(reader.GetValue(2)));
                Assert.Equal(databaseId, Convert.ToInt32(reader.GetValue(3)));
                Assert.False(await reader.ReadAsync(), "The lock holder had more than one exclusive application lock.");
            }

            // A comparison against the measured value finds the real row, and a
            // comparison against NULL finds nothing. That is why the value has
            // to be read as the empty string and not as a null.
            Assert.Equal(1, await CountApplicationLockRowsAsync(holder, resource, measuredSubtype));
            Assert.Equal(0, await CountApplicationLockRowsAsync(holder, resource, null));
        }
        finally
        {
            await ReleaseSessionApplicationLockAsync(holder, resource);
        }
    }

    private static async Task<int> CountApplicationLockRowsAsync(
        SqlConnection connection, string resource, string? resourceSubtype)
    {
        using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks
            WHERE resource_database_id = DB_ID()
              AND resource_type = 'APPLICATION'
              AND resource_description LIKE @resource
              AND resource_subtype = @resourceSubtype;
            """,
            connection);
        command.Parameters.AddWithValue("@resource", "%" + resource + "%");
        command.Parameters.AddWithValue("@resourceSubtype", (object?)resourceSubtype ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
