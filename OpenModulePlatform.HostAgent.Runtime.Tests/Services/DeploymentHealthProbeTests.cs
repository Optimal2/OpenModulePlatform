using System.Net;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeploymentHealthProbeTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(503, false)]
    [InlineData(302, false)]
    public async Task ReadinessRequiresSuccessFromConfiguredApp(int code, bool healthy)
    {
        using var handler = new Handler(code);
        var probe = WebAppHealthMonitor.ProbeDeploymentAsync(new Uri("http://localhost/app/health/ready"), 1, default, handler);
        if (healthy) await probe;
        else await Assert.ThrowsAsync<InvalidOperationException>(() => probe);
        Assert.Equal("/app/health/ready", handler.Path);
    }

    [Fact]
    public async Task Probe_DisposesProbeResponse()
    {
        using var handler = new Handler(200);
        Assert.Empty(handler.Responses);

        await WebAppHealthMonitor.ProbeDeploymentAsync(new Uri("http://localhost/app/health/ready"), 1, default, handler);

        var response = Assert.Single(handler.Responses);
        Assert.True(response.IsDisposed);
    }

    private sealed class Handler(int code) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public List<TrackedResponse> Responses { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            // CodeQL cs/local-not-disposed: kept - returned to the HttpClient caller, which
            // owns and disposes the response (ProbeDeploymentAsync uses using var response).
            var response = new TrackedResponse((HttpStatusCode)code);
            Responses.Add(response);
            return Task.FromResult<HttpResponseMessage>(response);
        }
    }

    private sealed class TrackedResponse(HttpStatusCode code) : HttpResponseMessage(code)
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
