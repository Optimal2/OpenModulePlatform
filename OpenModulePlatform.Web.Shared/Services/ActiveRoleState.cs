namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Holds the active role for the current request or Blazor Server circuit when the
/// role cookie cannot be read directly through HttpContext.
/// </summary>
public sealed class ActiveRoleState
{
    public int? ActiveRoleId { get; set; }
}
