namespace OpenModulePlatform.Portal.Options;

/// <summary>
/// Why the configured module-fragment endpoint cannot be used.
/// </summary>
public enum ModuleFragmentEndpointIssue
{
    None = 0,

    /// <summary>InternalBaseUrl is not an absolute HTTP or HTTPS URL.</summary>
    InvalidInternalBaseUrl,

    /// <summary>InternalBaseUrl contains a username or password.</summary>
    InternalBaseUrlUserInfo,

    /// <summary>InternalBaseUrl uses HTTP for an HTTPS request without the explicit opt-in.</summary>
    InsecureInternalBaseUrlForHttpsRequest
}

/// <summary>
/// Checks <see cref="ModuleFragmentWidgetOptions.InternalBaseUrl"/> before any fragment
/// request is built. An unusable value disables module-fragment widgets instead of
/// preventing the Portal from starting, and it never falls through to another destination.
/// </summary>
public static class ModuleFragmentEndpointCheck
{
    public const string InternalBaseUrlKey =
        ModuleFragmentWidgetOptions.SectionName + ":" + nameof(ModuleFragmentWidgetOptions.InternalBaseUrl);

    public const string AllowInsecureInternalBaseUrlKey =
        ModuleFragmentWidgetOptions.SectionName + ":" + nameof(ModuleFragmentWidgetOptions.AllowInsecureInternalBaseUrl);

    /// <summary>
    /// Returns the request-independent issue of the configuration, if any.
    /// </summary>
    public static ModuleFragmentEndpointIssue CheckConfiguration(ModuleFragmentWidgetOptions options)
        => Check(options, requestIsHttps: false, out _);

    /// <summary>
    /// Returns the issue that blocks <paramref name="options"/> for a request, or
    /// <see cref="ModuleFragmentEndpointIssue.None"/> with the usable override in
    /// <paramref name="internalBase"/> (null when no override is configured).
    /// </summary>
    public static ModuleFragmentEndpointIssue Check(
        ModuleFragmentWidgetOptions options,
        bool requestIsHttps,
        out Uri? internalBase)
    {
        internalBase = null;
        if (string.IsNullOrWhiteSpace(options.InternalBaseUrl))
        {
            return ModuleFragmentEndpointIssue.None;
        }

        if (!Uri.TryCreate(options.InternalBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return ModuleFragmentEndpointIssue.InvalidInternalBaseUrl;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return ModuleFragmentEndpointIssue.InternalBaseUrlUserInfo;
        }

        if (requestIsHttps && uri.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureInternalBaseUrl)
        {
            return ModuleFragmentEndpointIssue.InsecureInternalBaseUrlForHttpsRequest;
        }

        internalBase = uri;
        return ModuleFragmentEndpointIssue.None;
    }
}
