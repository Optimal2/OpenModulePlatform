namespace OpenModulePlatform.Auth.Services;

public sealed class OmpOidcProviderStatus
{
    public bool IsEnabled { get; init; }
    public string DisplayName { get; init; } = "";
}
