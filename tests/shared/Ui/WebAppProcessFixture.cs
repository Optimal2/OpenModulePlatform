using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// Boots a repo's built web app on a free port, mirroring the dev-server
/// recipe: Development environment (required for the _content static assets
/// that come from project references), anonymous access, and the OMP database
/// on localhost unless OMP_UITESTS_DB overrides it. If the app is not found
/// or does not answer 200 on <see cref="ReadinessPath"/> within the timeout
/// the fixture reports itself unavailable and dependent tests skip with the
/// reason - unless the run requires the UI suite
/// (<see cref="UiTestPaths.RequiredEnvironmentVariable"/>), in which case the
/// fixture fails with that reason instead.
/// </summary>
public abstract class WebAppProcessFixture : IAsyncLifetime
{
    private Process? _process;

    /// <summary>Solution file at the repo root, e.g. "ExampleModule.slnx".</summary>
    protected abstract string SolutionFileName { get; }

    /// <summary>Web project folder name, e.g. "ExampleModule.Web".</summary>
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

    /// <summary>
    /// When true, an app that cannot be found or started fails the fixture
    /// instead of making the dependent tests skip. Defaults to
    /// <see cref="UiTestPaths.IsRequired()"/>.
    /// </summary>
    protected virtual bool AppRequired => UiTestPaths.IsRequired();

    /// <summary>Extra environment variables for the app process.</summary>
    protected virtual IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string>();

    public string BaseUrl { get; private set; } = string.Empty;
    public string RepoRoot { get; private set; } = string.Empty;
    public bool Available { get; private set; }
    public string UnavailableReason { get; private set; } = "not initialized";

    public async Task InitializeAsync()
    {
        await StartAsync();
        if (!Available && AppRequired)
        {
            StopProcess();
            throw new InvalidOperationException(
                $"{WebProjectName}: {UnavailableReason}. {UiTestPaths.RequiredEnvironmentVariable} is set, so the UI suite "
                + "must run instead of skipping every test; build the app or fix its output layout.");
        }
    }

    private async Task StartAsync()
    {
        RepoRoot = UiTestPaths.FindRepoRoot(SolutionFileName);
        var projectDir = Path.Join(RepoRoot, WebProjectDirectory);
        var candidates = UiTestPaths.AppExecutableCandidates(projectDir, WebProjectName, AssemblyName, AppContext.BaseDirectory);
        var exePath = candidates.FirstOrDefault(File.Exists);
        if (exePath is null)
        {
            UnavailableReason = "app binary not found; looked for " + string.Join(", ", candidates);
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
        // xUnit initializes a collection's fixtures concurrently, so every app of the
        // collection cold-starts at the same time; on a loaded machine 40 s was not enough.
        var deadline = DateTime.UtcNow.AddSeconds(120);
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
        StopProcess();
        return Task.CompletedTask;
    }

    private void StopProcess()
    {
        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        _process?.Dispose();
        _process = null;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
