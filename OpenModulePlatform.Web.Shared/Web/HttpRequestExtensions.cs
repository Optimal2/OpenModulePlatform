// File: OpenModulePlatform.Web.Shared/Web/HttpRequestExtensions.cs
using Microsoft.AspNetCore.Http;

namespace OpenModulePlatform.Web.Shared.Web;

public static class HttpRequestExtensions
{
    public static string GetPublicBaseUrl(this HttpRequest request)
    {
        // Use request.Scheme/Host, which the ForwardedHeaders middleware
        // already populated from X-Forwarded-* ONLY for trusted proxies
        // (KnownProxies/KnownNetworks). Reading the raw X-Forwarded-* headers
        // here bypassed that trust model and let any client spoof the host in
        // absolute links - host header injection / phishing (R3-E3).
        return $"{request.Scheme}://{request.Host.Value}";
    }
}
