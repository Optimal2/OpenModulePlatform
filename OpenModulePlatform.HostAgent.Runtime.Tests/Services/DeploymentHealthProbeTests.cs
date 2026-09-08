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

    private sealed class Handler(int code) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
        }
    }
}
