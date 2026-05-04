using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace OpenModulePlatform.Web.Shared.OpenDocViewer;

/// <summary>
/// Builds the sample Portable Document Bundle used by the OMP OpenDocViewer integration examples.
/// </summary>
public static class OpenDocViewerExampleBundleFactory
{
    public const string DefaultBundleEndpointPath = "opendocviewer-demo/bundle";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static object BuildSampleBundle(
        HttpRequest request,
        OpenDocViewerExampleOptions options,
        string source,
        string? userName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var issuedAt = DateTimeOffset.UtcNow;
        var sampleFileUrl = ToAbsoluteUrl(request, options.SampleFileUrl);
        var safeSource = string.IsNullOrWhiteSpace(source) ? "OpenModulePlatform example" : source;

        return new
        {
            session = new
            {
                id = $"omp-example:{safeSource}:{issuedAt:yyyyMMddHHmmss}",
                userId = string.IsNullOrWhiteSpace(userName) ? "anonymous" : userName,
                issuedAt = issuedAt.ToString("O")
            },
            documents = new[]
            {
                new
                {
                    documentId = "odv-sample-pdf",
                    files = new[]
                    {
                        new
                        {
                            id = "odv-sample-pdf-file",
                            url = sampleFileUrl,
                            ext = "pdf",
                            displayName = "sample.pdf",
                            contentType = "application/pdf",
                            fileNumber = 1
                        }
                    },
                    meta = new[]
                    {
                        Metadata("source", safeSource, "Source"),
                        Metadata("integration", "sessionurl", "Integration mode"),
                        Metadata("sampleFile", "OpenDocViewer public/sample.pdf", "Sample file")
                    },
                    metadata = new
                    {
                        source = safeSource,
                        integration = "sessionurl",
                        sampleFile = "sample.pdf"
                    }
                }
            },
            integration = new
            {
                source = safeSource,
                mode = "sessionurl",
                sample = true
            }
        };
    }

    public static string BuildFrameUrl(OpenDocViewerExampleOptions options, string bundleUrl)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseUrl = NormalizeViewerBaseUrl(options.BaseUrl);
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}sessionurl={Uri.EscapeDataString(bundleUrl)}";
    }

    public static string ToAbsoluteUrl(HttpRequest request, string url)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        var path = url.StartsWith("/", StringComparison.Ordinal)
            ? url
            : CombinePath(request.PathBase.Value, url);

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        return $"{request.Scheme}://{request.Host}{path}";
    }

    public static string ToAbsoluteUrl(Uri baseUri, string url)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            return $"{baseUri.Scheme}://{baseUri.Authority}{url}";
        }

        return new Uri(baseUri, url).ToString();
    }

    private static object Metadata(string key, string value, string label)
        => new
        {
            key,
            value,
            label
        };

    private static string NormalizeViewerBaseUrl(string? baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl)
            ? "/opendocviewer/"
            : baseUrl.Trim();

        return value.Contains("?", StringComparison.Ordinal) || value.EndsWith("/", StringComparison.Ordinal)
            ? value
            : value + "/";
    }

    private static string CombinePath(string? pathBase, string relativePath)
    {
        var left = string.IsNullOrWhiteSpace(pathBase) ? string.Empty : pathBase.TrimEnd('/');
        var right = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.TrimStart('/');
        return string.IsNullOrEmpty(left) ? right : $"{left}/{right}";
    }
}
