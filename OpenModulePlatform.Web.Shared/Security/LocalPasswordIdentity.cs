// File: OpenModulePlatform.Web.Shared/Security/LocalPasswordIdentity.cs
namespace OpenModulePlatform.Web.Shared.Security;

public static class LocalPasswordIdentity
{
    public const string ProviderDisplayName = "lpwd";

    public static string NormalizeUserName(string userName)
        => userName.Trim().ToLowerInvariant();
}
