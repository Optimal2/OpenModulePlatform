namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Lightweight description of a role that can be selected for the current user session.
/// </summary>
public sealed record UserRoleOption(int RoleId, string Name, string? Description);
