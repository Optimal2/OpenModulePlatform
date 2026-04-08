namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Resolved RBAC context for the current user request.
/// </summary>
public sealed class UserRoleContext
{
    public static UserRoleContext Empty { get; } = new();

    public IReadOnlyList<UserRoleOption> AvailableRoles { get; init; } = Array.Empty<UserRoleOption>();

    public int? ActiveRoleId { get; init; }

    public string? ActiveRoleName { get; init; }

    public HashSet<string> EffectivePermissions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
