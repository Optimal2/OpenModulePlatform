namespace OpenModulePlatform.Auth.Services;

public static class OmpAuthSessionLifetimeDefaults
{
    public const string ProviderSessionLifetimesSetting = "providerSessionLifetimes";
    public const int FallbackProviderId = 0;
    public const int BuiltInDefaultMinutes = 600;
    public const int MinimumMinutes = 5;
    public const int MaximumMinutes = 10080;
}
