// File: OpenModulePlatform.Web.Shared/Web/HttpRequestExtensions.cs
using Microsoft.AspNetCore.Http;

namespace OpenModulePlatform.Web.Shared.Web;

public static class HttpRequestExtensions
{
    public static string GetPublicBaseUrl(this HttpRequest request)
    {
        var scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && !string.IsNullOrWhiteSpace(proto)
            ? proto.ToString()
            : request.Scheme;

        var host = request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost) && !string.IsNullOrWhiteSpace(forwardedHost)
            ? forwardedHost.ToString()
            : request.Host.Value;

        return $"{scheme}://{host}";
    }
}
