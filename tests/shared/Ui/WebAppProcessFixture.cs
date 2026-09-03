using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// Boots a repo's built web app on a free port, mirroring the dev-server
/// recipe: Development environment (required for the _content static assets
/// that come from project references), anonymous access, and the OMP database
/// on localhost unless OMP_UITESTS_DB overrides it. If the app does not
/// answer 200 on <see cref="ReadinessPath"/> within the timeout the fixture
/// reports itself unavailable and dependent tests skip with the reason.
/// </summary>
public abstract class WebAppProcessFixture : IAsyncLifetime
{
    private Process? _process;

    /// <summary>Solution file at the repo root, e.g. "LogSearch.slnx".</summary>
    protected abstract string SolutionFileName { get; }

    /// <summary>Web project folder name, e.g. "LogSearch.Web".</summary>
    protected abstract string WebProjectName { get; }

    /// <summary>
    /// Repo-relative directory of the web project. Defaults to
    /// <see cref="WebProjectName"/>; override for layouts like "src\App" or
    /// "RazorPages" where the folder is not named after the project.
    /// </summary>
    protected virtual string WebProjectDirectory => WebProjectName;

    /// <summary>
    /// Name of the built executable (without ".exe"). Defaults to
    /// <see cref="WebProjectName"/>; override when the assembly name differs
    /// from the project folder.
    /// </summary>
    protected virtual string AssemblyName => WebProjectName;

    /// <summary>
    /// Path probed until it answers 200 during startup. Defaults to "/";
    /// override for apps without a root route (e.g. Auth uses "/login").
    /// </summary>
    protected virtual string ReadinessPath => "/";

    /// <summary>Extra environment variables for the app process.</summary>
    protected virtual IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string>();

    public string BaseUrl { get; private set; } = string.Empty;
    public string RepoRoot { get; private set; } = string.Empty;
    public bool Available { get; private set; }
    public string UnavailableReason { get; private set; } = "not initialized";

    public async Task InitializeAsync()
    {
        RepoRoot = UiTestPaths.FindRepoRoot(SolutionFileName);
        var projectDir = Path.Join(RepoRoot, WebProjectDirectory);
        var (configuration, tfm) = UiTestPaths.BuildOutputSegments();
        var exePath = Path.Join(projectDir, "bin", configuration, tfm, AssemblyName + ".exe");
        if (!File.Exists(exePath))
        {
            UnavailableReason = $"app binary not found: {exePath}";
            return;
        }

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["ASPNETCORE_URLS"] = BaseUrl;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_CONTENTROOT"] = projectDir;
        startInfo.Environment["WebApp__AllowAnonymous"] = "true";
        startInfo.Environment["ConnectionStrings__OmpDb"] =
            Environment.GetEnvironmentVariable("OMP_UITESTS_DB")
            ?? "Server=localhost;Database=OpenModulePlatform;Trusted_Connection=True;TrustServerCertificate=True";
        foreach (var (key, value) in ExtraEnvironment)
        {
            startInfo.Environment[key] = value;
        }

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UnavailableReason = $"app failed to start: {ex.Message}";
            return;
        }

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (_process is null || _process.HasExited)
            {
                UnavailableReason = $"app exited early with code {_process?.ExitCode}";
                return;
            }

            try
            {
                var response = await http.GetAsync(BaseUrl + ReadinessPath);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Available = true;
                    return;
                }

                UnavailableReason = $"GET {ReadinessPath} returned {(int)response.StatusCode} (database missing or app misconfigured)";
            }
            catch (HttpRequestException)
            {
                UnavailableReason = "app did not start listening in time";
            }

            await Task.Delay(500);
        }
    }

    public Task DisposeAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
