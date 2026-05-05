// File: OpenModulePlatform.Web.Shared/Options/OmpAuthOptions.cs
namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Shared authentication settings for OMP web applications.
/// </summary>
public sealed class OmpAuthOptions
{
    public const string SectionName = "OmpAuth";

    public string CookieName { get; set; } = Security.OmpAuthDefaults.CookieName;
    public string LoginPath { get; set; } = Security.OmpAuthDefaults.LoginPath;
    public string LogoutPath { get; set; } = Security.OmpAuthDefaults.LogoutPath;
    public string AccessDeniedPath { get; set; } = Security.OmpAuthDefaults.AccessDeniedPath;
    public string ApplicationName { get; set; } = "OpenModulePlatform";

    /// <summary>
    /// Optional shared Data Protection key directory. All OMP web apps that
    /// read the shared auth cookie must use the same key ring.
    /// </summary>
    public string DataProtectionKeyPath { get; set; } = "";
}
