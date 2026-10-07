using System.Net;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Install;

/// <summary>
/// The real Windows implementations of the install actions. Only reached by a
/// real install; <c>--dry-run</c> never constructs this class.
/// </summary>
public sealed class WindowsInstallActions : IInstallActions
{
    public bool InstallIisFeatures(IReadOnlyList<string> featureNames, bool isServerOs)
    {
        if (featureNames.Count == 0)
        {
            return false;
        }

        if (isServerOs)
        {
            // Install-WindowsFeature is the Server Manager path; /quiet-equivalent
            // by nature. Keep the command short and explicit. Its result object
            // carries Success and RestartNeeded: map them onto exit codes so a
            // required restart (3010) is reported instead of silently dropped.
            var names = string.Join(",", featureNames);
            var result = InstallationEngine.RunProcess(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command",
                    $"$r = Install-WindowsFeature -Name {names}; if (-not $r.Success) {{ exit 1 }}; if ($r.RestartNeeded -eq 'Yes') {{ exit 3010 }}; exit 0"],
                throwOnFailure: false,
                timeout: TimeSpan.FromMinutes(30));
            return InterpretFeatureInstallExitCode($"Install-WindowsFeature {names}", result.ExitCode, result.StdErr);
        }

        var restartRequired = false;
        foreach (var featureName in featureNames)
        {
            // dism /enable-feature answers 3010/1641 when the feature needs a
            // restart: success, not failure, so check the exit code explicitly
            // instead of letting RunProcess throw on it.
            var result = InstallationEngine.RunProcess(
                "dism.exe",
                ["/online", "/enable-feature", $"/featurename:{featureName}", "/all", "/norestart", "/quiet"],
                throwOnFailure: false,
                timeout: TimeSpan.FromMinutes(30));
            restartRequired |= InterpretFeatureInstallExitCode($"dism /enable-feature {featureName}", result.ExitCode, result.StdErr);
        }

        return restartRequired;
    }

    /// <summary>0 = done; 3010/1641 = done, restart required; anything else is a failure.</summary>
    internal static bool InterpretFeatureInstallExitCode(string description, int exitCode, string stdErr)
    {
        switch (exitCode)
        {
            case 0:
                return false;
            case 3010:
            case 1641:
                return true;
            default:
                var detail = (stdErr ?? string.Empty).Trim();
                throw new InvalidOperationException(
                    $"{description} failed with exit code {exitCode}."
                    + (detail.Length == 0 ? string.Empty : " " + detail));
        }
    }

    public int RunHostingBundle(string installerPath, bool repair)
    {
        var arguments = repair
            ? new[] { "/repair", "/quiet", "/norestart" }
            : new[] { "/install", "/quiet", "/norestart" };
        var result = InstallationEngine.RunProcess(
            installerPath,
            arguments,
            throwOnFailure: false,
            timeout: TimeSpan.FromMinutes(30));
        return result.ExitCode;
    }

    public void RestartIis()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var iisreset = Path.Join(windows, "System32", "iisreset.exe");
        InstallationEngine.RunProcess(
            File.Exists(iisreset) ? iisreset : "iisreset.exe",
            ["/noforce", "/restart"],
            timeout: TimeSpan.FromMinutes(3));
    }

    public bool EnsureServiceLogonRight(string accountName, out string detail)
        => ServiceLogonRight.EnsureGranted(accountName, out detail);

    public bool ValidateAccountPassword(string userName, string domain, string password, out string error)
        => AccountPasswordValidator.Validate(userName, domain, password, out error);

    public async Task<bool> WaitForHostAgentActiveAsync(
        string server,
        string database,
        bool trustServerCertificate,
        string hostKey,
        string serviceName,
        DateTimeOffset notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = trustServerCertificate,
            ConnectTimeout = 15
        };

        const string sql = @"
SELECT COUNT(1)
FROM omp.HostAgentRuntimeStates AS rs
JOIN omp.Hosts AS h ON h.HostId = rs.HostId
WHERE h.HostKey = @hostKey
  AND rs.ServiceName = @serviceName
  AND rs.IsActive = 1
  AND rs.LastSeenUtc >= @notBefore;";

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
                command.Parameters.AddWithValue("@hostKey", hostKey ?? string.Empty);
                command.Parameters.AddWithValue("@serviceName", serviceName ?? string.Empty);
                command.Parameters.AddWithValue("@notBefore", notBefore.UtcDateTime);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                if (count > 0)
                {
                    return true;
                }
            }
            catch (SqlException)
            {
                // The database answered the prerequisite probe; a transient
                // failure here just delays the wait.
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return false;
    }

    public async Task<(bool Ok, string Detail)> ProbePortalAsync(
        string portalUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            // The probe follows the login redirect chain itself; auto-redirect
            // through Windows authentication endpoints adds nothing here.
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var healthUrl = new Uri(new Uri(portalUrl), "health/live");
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var lastDetail = "no answer";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(healthUrl, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return (true, $"Portal answers at {healthUrl}.");
                }

                if ((int)response.StatusCode < 500)
                {
                    lastDetail = $"HTTP {(int)response.StatusCode} from {healthUrl}; waiting for the portal to become ready";
                }
                else
                {
                    lastDetail = $"HTTP {(int)response.StatusCode} from {healthUrl}";
                }
            }
            catch (HttpRequestException ex)
            {
                lastDetail = ex.Message;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastDetail = "request timed out";
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return (false, lastDetail);
    }
}
