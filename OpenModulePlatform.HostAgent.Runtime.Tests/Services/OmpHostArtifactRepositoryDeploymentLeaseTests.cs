using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OmpHostArtifactRepositoryDeploymentLeaseTests
{
    [Fact]
    public async Task SetupLeaseTableIsIdempotentAndConcurrentAcquisitionHasOneWinner()
    {
        using var database = new OmpHostArtifactRepositoryTestDatabase();
        var firstHost = database.InsertHost("lease-host-one");
        var secondHost = database.InsertHost("lease-host-two");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "sql", "1-setup-openmoduleplatform.sql")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var setup = File.ReadAllText(Path.Join(directory.FullName, "sql", "1-setup-openmoduleplatform.sql"));
        var batch = Assert.Single(Regex.Split(setup, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase),
            text => text.Contains("CREATE TABLE omp.AppDeploymentLeases", StringComparison.Ordinal));
        await using var conn = new SqlConnection(database.ConnectionString);
        await conn.OpenAsync();
        await using (var create = new SqlCommand(batch, conn))
        {
            await create.ExecuteNonQueryAsync();
            await create.ExecuteNonQueryAsync();
        }
        var repository = new OmpHostArtifactRepository(database.CreateFactory());
        var results = await Task.WhenAll(
            repository.AcquireAppDeploymentLeaseAsync("app:portal", firstHost, "Deploy", 600, default),
            repository.AcquireAppDeploymentLeaseAsync("app:portal", secondHost, "Deploy", 600, default));
        var winner = Assert.Single(results, result => result.Acquired);
        var loser = Assert.Single(results, result => !result.Acquired);
        Assert.Equal(winner.HostId, loser.HostId);
        Assert.Equal(winner.LeaseToken, loser.LeaseToken);

        Assert.False((await repository.AcquireAppDeploymentLeaseAsync("host:*",
            winner.HostId == firstHost ? secondHost : firstHost, "Mode transition", 600, default)).Acquired);

        await using (var expire = new SqlCommand("UPDATE omp.AppDeploymentLeases SET LeaseUntilUtc = DATEADD(second, -1, SYSUTCDATETIME()) WHERE LeaseScopeKey = N'app:portal';", conn))
            await expire.ExecuteNonQueryAsync();
        var successor = await repository.AcquireAppDeploymentLeaseAsync("app:portal",
            winner.HostId == firstHost ? secondHost : firstHost, "Retry", 600, default);
        Assert.True(successor.Acquired);
        Assert.True(successor.TookOverExpiredLease);
        await repository.ReleaseAppDeploymentLeaseAsync("app:portal", winner.LeaseToken, "Stale release", default);
        Assert.False((await repository.AcquireAppDeploymentLeaseAsync("app:portal", winner.HostId, "Retry", 600, default)).Acquired);
        await repository.ReleaseAppDeploymentLeaseAsync("app:portal", successor.LeaseToken, "Readiness failed", default);
        await using var reason = new SqlCommand("SELECT Reason FROM omp.AppDeploymentLeases WHERE LeaseScopeKey = N'app:portal';", conn);
        Assert.Equal("Readiness failed", await reason.ExecuteScalarAsync());
        Assert.True((await repository.AcquireAppDeploymentLeaseAsync("app:portal", winner.HostId, "Retry", 600, default)).Acquired);

        var batches = Regex.Split(setup, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var marker in new[] { "CREATE TABLE omp.config_setting_definitions", "CREATE TABLE omp.config_settings", "N'DeploymentLockScope', N'Cross-host" })
        {
            var sql = Assert.Single(batches, text => text.Contains(marker, StringComparison.Ordinal));
            await using var command = new SqlCommand(sql, conn);
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }
        var missing = await repository.ReadDeploymentLockSettingsAsync(default);
        Assert.Null(missing["DeploymentLockScope"]);
        Assert.Null(missing["DeploymentLeaseSeconds"]);
        await using (var setting = new SqlCommand("""
            INSERT omp.config_settings(ConfigSettingId, ConfigValue)
            SELECT ConfigSettingId, N'host' FROM omp.config_setting_definitions WHERE ConfigSetting = N'DeploymentLockScope';
            INSERT omp.config_settings(ConfigSettingId, ConfigValue, ConfigUsr, ConfigPriority)
            SELECT ConfigSettingId, N'off', 123, 100 FROM omp.config_setting_definitions WHERE ConfigSetting = N'DeploymentLockScope';
            """, conn))
            await setting.ExecuteNonQueryAsync();
        Assert.Equal("host", (await repository.ReadDeploymentLockSettingsAsync(default))["DeploymentLockScope"]);

        // Ephemeral lease history must not break existing host-removal workflows.
        await using var removeHost = new SqlCommand("DELETE FROM omp.Hosts WHERE HostId = @hostId; SELECT COUNT(*) FROM omp.AppDeploymentLeases;", conn);
        removeHost.Parameters.AddWithValue("@hostId", winner.HostId);
        Assert.Equal(0, await removeHost.ExecuteScalarAsync());
    }
}
