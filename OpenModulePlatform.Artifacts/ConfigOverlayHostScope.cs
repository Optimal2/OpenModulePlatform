namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Host scope of a config overlay. <see cref="Host"/> (the historical default)
/// targets exactly one host key. <see cref="AllHosts"/> targets every host in
/// the same OMP installation, optionally narrowed by the other selectors
/// (module/app/package/target/artifact minimum). It never crosses installations:
/// it is resolved against the connected database only.
/// </summary>
public enum ConfigOverlayHostScope
{
    Host = 0,
    AllHosts = 1
}

public static class ConfigOverlayHostScopeNames
{
    public const string Host = "Host";
    public const string AllHosts = "AllHosts";

    /// <summary>Stored HostKey sentinel for AllHosts rows. Never a real host key.</summary>
    public const string AllHostsHostKey = "*";

    public static string ToName(ConfigOverlayHostScope scope)
        => scope == ConfigOverlayHostScope.AllHosts ? AllHosts : Host;

    /// <summary>
    /// Parses the portable <c>hostScope</c> value. Absent/empty means
    /// <see cref="ConfigOverlayHostScope.Host"/> for backward compatibility.
    /// Any unrecognized value fails closed (throws) so a malformed selector
    /// can never silently become a broadcast overlay.
    /// </summary>
    public static ConfigOverlayHostScope Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ConfigOverlayHostScope.Host;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOverlayHostScope.Host;
        }

        if (trimmed.Equals(AllHosts, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("all-hosts", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("all_hosts", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOverlayHostScope.AllHosts;
        }

        throw new InvalidOperationException(
            $"Config overlay hostScope '{value}' is not recognized; expected 'Host' or 'AllHosts'.");
    }

    public static bool IsWildcardHostKey(string? hostKey)
        => hostKey is not null && (hostKey.Contains('*') || hostKey.Contains('?'));

    /// <summary>
    /// Validates a host-specific host key. Wildcards are rejected: '*' is the
    /// internal AllHosts sentinel only and must never be treated as a real host.
    /// </summary>
    public static string RequireHostKey(string? hostKey)
    {
        if (string.IsNullOrWhiteSpace(hostKey))
        {
            throw new InvalidOperationException("Config overlay must contain hostKey when hostScope is 'Host'.");
        }

        var trimmed = hostKey.Trim();
        if (IsWildcardHostKey(trimmed))
        {
            throw new InvalidOperationException(
                "Config overlay hostKey must not contain wildcards. Use hostScope 'AllHosts' for an installation-wide overlay.");
        }

        return trimmed;
    }

    /// <summary>Stored HostKey for a document: the real key, or the sentinel for AllHosts.</summary>
    public static string StoredHostKey(ConfigOverlayHostScope scope, string? hostKey)
        => scope == ConfigOverlayHostScope.AllHosts ? AllHostsHostKey : RequireHostKey(hostKey);

    /// <summary>
    /// Host-only imports remain compatible with older schemas. An AllHosts
    /// import must persist its explicit scope, including on identical re-imports.
    /// Call before any document, sibling-state, or file-row writes.
    /// </summary>
    public static void RequireWritableSchema(ConfigOverlayHostScope scope, bool hasHostScopeColumn)
    {
        if (scope == ConfigOverlayHostScope.AllHosts && !hasHostScopeColumn)
        {
            throw new InvalidOperationException(
                "Cannot save an AllHosts config overlay because omp.ConfigOverlayDocuments.HostScope is missing. Apply the platform schema upgrade before importing this overlay.");
        }
    }

    /// <summary>
    /// Resolution priority per file: artifact base 0, AllHosts overlay 1,
    /// host-specific overlay 2. A host-specific overlay therefore wins over a
    /// matching AllHosts overlay even when its overlayVersion is older.
    /// </summary>
    public static int ScopePriority(ConfigOverlayHostScope scope)
        => scope == ConfigOverlayHostScope.AllHosts ? 1 : 2;

    public static ConfigOverlayHostScope FromStored(string? storedScope, string? storedHostKey)
    {
        if (!string.IsNullOrWhiteSpace(storedScope))
        {
            return Parse(storedScope);
        }

        // Pre-HostScope rows have no column value. Only an explicit AllHosts
        // marker could broadcast; a legacy '*' host key stays host-specific
        // (and matches no real host), never a broadcast.
        return ConfigOverlayHostScope.Host;
    }

    public static bool MatchesHost(ConfigOverlayHostScope scope, string? storedHostKey, string hostKey)
    {
        if (scope == ConfigOverlayHostScope.AllHosts)
        {
            return true;
        }

        return storedHostKey is not null
            && string.Equals(storedHostKey, hostKey, StringComparison.OrdinalIgnoreCase);
    }
}
