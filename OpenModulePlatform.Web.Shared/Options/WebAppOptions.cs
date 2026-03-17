// File: OpenModulePlatform.Web.Shared/Options/WebAppOptions.cs
namespace OpenModulePlatform.Web.Shared.Options;

public enum PermissionMode
{
    Any = 0,
    All = 1
}

public sealed class WebAppOptions
{
    public const string DefaultSectionName = "WebApp";

    public string Title { get; set; } = "OpenModulePlatform";
    public string DefaultCulture { get; set; } = "sv-SE";
    public string[] SupportedCultures { get; set; } = ["sv-SE", "en-US"];
    public bool AllowAnonymous { get; set; }
    public bool UseForwardedHeaders { get; set; }
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Any;
    public bool ForwardedHeadersTrustAllProxies { get; set; }
    public string[] ForwardedHeadersKnownProxies { get; set; } = [];
    public string[] ForwardedHeadersKnownNetworks { get; set; } = [];
}
