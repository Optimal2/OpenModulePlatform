using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ModuleFragmentTlsTests
{
    [Fact]
    public async Task Https_ConfiguredPortalOrigin_UsesCertificateNameInsteadOfIncomingHost()
    {
        await using var server = await TlsServer.StartAsync();
        using var handler = server.CreateHandler();
        using var services = CreateServices(handler, $"https://portal.example:{server.Port}/portal");
        var context = Context(server.Port, "untrusted.example");

        var result = await FetchAsync(services, context);

        Assert.True(result.IsLoaded);
        Assert.Equal("portal.example", server.ServerName);
        Assert.Equal($"portal.example:{server.Port}", server.Host);
        Assert.Equal("/sample/widgets/overview", server.Path);
        Assert.Equal(".OpenModulePlatform.Auth=test-ticket", server.Cookie);
        Assert.Equal(SslPolicyErrors.None, server.CertificateErrors);
    }

    [Fact]
    public async Task Https_LoopbackWithPublicHost_AlreadyUsesPublicNameForSni()
    {
        await using var server = await TlsServer.StartAsync();
        using var handler = server.CreateHandler();
        using var services = CreateServices(handler, "/");

        var result = await FetchAsync(services, Context(server.Port, "portal.example"));

        Assert.True(result.IsLoaded);
        Assert.Equal("portal.example", server.ServerName);
        Assert.Equal(SslPolicyErrors.None, server.CertificateErrors);
    }

    [Theory]
    [InlineData("localhost", true, SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData("portal.example", false, SslPolicyErrors.RemoteCertificateChainErrors)]
    public async Task Https_InvalidCertificate_IsRejectedAndLogged(
        string host, bool trustCertificate, SslPolicyErrors expectedError)
    {
        await using var server = await TlsServer.StartAsync();
        using var handler = server.CreateHandler(trustCertificate);
        var logger = new CaptureLogger();
        using var services = CreateServices(handler, "/", logger);

        var result = await FetchAsync(services, Context(server.Port, host));

        Assert.False(result.IsLoaded);
        Assert.Empty(result.Html);
        Assert.True(server.CertificateErrors.HasFlag(expectedError));
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("TLS/certificate", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Reason:", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception?.InnerException);
        Assert.DoesNotContain("test-ticket", entry.Message, StringComparison.Ordinal);
    }

    private static DefaultHttpContext Context(int port, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host, port);
        context.Connection.LocalPort = port;
        context.Request.Headers.Cookie = ".OpenModulePlatform.Auth=test-ticket; unrelated=secret";
        return context;
    }

    private static Task<ModuleFragmentResult> FetchAsync(IServiceProvider services, HttpContext context)
        => ActivatorUtilities.CreateInstance<PortalModuleFragmentService>(services).GetFragmentAsync(
            context, 7,
            ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig("sample-web", "/widgets/overview")),
            new HashSet<int> { 7 },
            [new PortalAppEntry { AppKey = "sample-web", RoutePath = "/sample" }],
            CancellationToken.None);

    private static ServiceProvider CreateServices(HttpMessageHandler handler, string portalBaseUrl, CaptureLogger? logger = null)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddOptions<ModuleFragmentWidgetOptions>().Configure(options =>
        {
            options.CacheSeconds = 0;
            // Exercise certificate validation even under parallel suite load.
            // The production request budget is not the behavior under test here.
            options.TimeoutMilliseconds = 10000;
        });
        services.AddOptions<OmpAuthOptions>();
        services.AddOptions<WebAppOptions>().Configure(options => options.PortalTopBar.PortalBaseUrl = portalBaseUrl);
        services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler));
        services.AddSingleton<ILogger<PortalModuleFragmentService>>(logger ?? new CaptureLogger());
        services.AddSingleton(new ModuleFragmentEndpointDiagnostics(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleFragmentEndpointDiagnostics>.Instance));
        return services.BuildServiceProvider();
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureLogger : ILogger<PortalModuleFragmentService>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((formatter(state, exception), exception));
    }

    private sealed class TlsServer(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public int Port { get; private set; }
        public string? ServerName { get; private set; }
        public string? Host { get; private set; }
        public string? Path { get; private set; }
        public string? Cookie { get; private set; }
        public SslPolicyErrors CertificateErrors { get; private set; }

        public static async Task<TlsServer> StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=portal.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("portal.example");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Reimport for Schannel on Windows; trust remains in this client's memory only.
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            TlsServer? server = null;
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificateSelector = (_, name) =>
                {
                    server!.ServerName = name;
                    return certificate;
                };
            })));
            var app = builder.Build();
            server = new TlsServer(app, certificate);
            app.Run(async context =>
            {
                server.Host = context.Request.Host.Value;
                server.Path = context.Request.Path.Value;
                server.Cookie = context.Request.Headers.Cookie;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<p>Secure fragment</p>");
            });
            await app.StartAsync();
            server.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            return server;
        }

        public SocketsHttpHandler CreateHandler(bool trustCertificate = true)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            if (trustCertificate)
                policy.CustomTrustStore.Add(certificate);
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateChainPolicy = policy,
                    RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    {
                        CertificateErrors = errors;
                        return errors == SslPolicyErrors.None;
                    }
                },
                ConnectCallback = async (context, ct) =>
                {
                    // Only test DNS is substituted. TLS, SNI and hostname checks use
                    // the real handler; no system trust store or DNS changes are needed.
                    Assert.Contains(context.DnsEndPoint.Host, new[] { "localhost", "portal.example" });
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
