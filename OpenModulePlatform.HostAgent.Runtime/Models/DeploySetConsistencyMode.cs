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

    public static string Normalize(string? value)
        => !string.IsNullOrWhiteSpace(value) && IsKnown(value) ? value : None;
}
