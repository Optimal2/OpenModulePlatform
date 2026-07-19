namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class DeploySetConsistencyModes
{
    public const string None = "None";

    public const string Warn = "Warn";

    public const string Block = "Block";

    public static bool IsKnown(string value)
        => string.Equals(value, None, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Warn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Block, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a configured mode. An absent, empty, unknown or misspelled value falls back to
    /// <see cref="Warn"/> — NOT <see cref="None"/>. Returning <c>None</c> made a simple typo (e.g.
    /// "warning" instead of "Warn") silently DISABLE the deploy-set consistency check, even though
    /// the settings property default is <c>Warn</c>: a fail-open surprise that hid exactly the class
    /// of binary-incompatibility incident this check exists for (see ADR 0002). Turning the check
    /// off must be an explicit, spelled-correctly "None".
    /// </summary>
    public static string Normalize(string? value)
        => !string.IsNullOrWhiteSpace(value) && IsKnown(value) ? value : Warn;
}
