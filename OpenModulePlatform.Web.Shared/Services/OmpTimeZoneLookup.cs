namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Resolves IANA time zone ids on every supported host, including Windows
/// hosts without icu.dll (Windows before 10 1903 / Server 2019), where .NET
/// runs in NLS globalization mode. There <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
/// throws <see cref="TimeZoneNotFoundException"/> for IANA ids and
/// <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId"/> returns false, so
/// production code must never call them directly. Resolution order:
/// 1. the platform lookup (accepts IANA ids when ICU is available, and
///    Windows ids everywhere);
/// 2. the platform IANA-to-Windows conversion, then a lookup of the converted
///    Windows id;
/// 3. a small built-in IANA-to-Windows table, then a lookup of the mapped
///    Windows id.
/// The caller keeps the IANA id as the reported id; unknown ids throw a
/// <see cref="TimeZoneNotFoundException"/> that names the id.
/// App-local ICU is not an option for OMP: worker and channel-type plugins
/// load inside WorkerProcessHost and cannot carry their own globalization
/// mode, so this fallback lives in shared code instead.
/// </summary>
public sealed class OmpTimeZoneLookup
{
    /// <summary>Signature mirror of <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId"/> for injection.</summary>
    public delegate bool TryConvertIanaIdToWindowsIdDelegate(string ianaId, out string? windowsId);

    /// <summary>
    /// The built-in IANA-to-Windows fallback table, used only when the
    /// platform itself cannot convert (NLS mode). It covers the zones OMP and
    /// its consumers are configured with; the platform conversion remains the
    /// primary path for every other IANA id. IANA ids are ASCII and compared
    /// case-insensitively, so the table lookup is too: on an ICU host the
    /// platform accepts 'europe/stockholm', and the fallback must not be
    /// stricter than the path it replaces.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuiltInIanaToWindowsIds { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UTC"] = "UTC",
            ["Etc/UTC"] = "UTC",
            ["Etc/Universal"] = "UTC",
            ["Europe/Stockholm"] = "W. Europe Standard Time",
            ["Europe/Oslo"] = "W. Europe Standard Time",
            ["Europe/Copenhagen"] = "Romance Standard Time",
            ["Europe/Helsinki"] = "FLE Standard Time",
            ["Europe/Berlin"] = "W. Europe Standard Time",
            ["Europe/Amsterdam"] = "W. Europe Standard Time",
            ["Europe/Paris"] = "Romance Standard Time",
            ["Europe/London"] = "GMT Standard Time",
        };

    /// <summary>The lookup backed by the real platform APIs.</summary>
    public static OmpTimeZoneLookup Platform { get; } = new();

    private readonly Func<string, TimeZoneInfo> _platformFind;
    private readonly TryConvertIanaIdToWindowsIdDelegate _platformConvert;

    // null = not probed yet. True when the platform converts a known IANA id
    // (ICU present); false on NLS-mode hosts, where the built-in table is the
    // only IANA path. Probed lazily so construction never touches the platform.
    private bool? _platformConvertsIanaIds;

    /// <summary>
    /// Creates a lookup. Both delegates default to the platform APIs; tests
    /// inject replacements to simulate a host in NLS mode (IANA lookups throw
    /// and the IANA-to-Windows conversion returns false).
    /// </summary>
    public OmpTimeZoneLookup(
        Func<string, TimeZoneInfo>? platformFind = null,
        TryConvertIanaIdToWindowsIdDelegate? platformConvert = null)
    {
        _platformFind = platformFind ?? TimeZoneInfo.FindSystemTimeZoneById;
        _platformConvert = platformConvert ?? TimeZoneInfo.TryConvertIanaIdToWindowsId;
    }

    /// <summary>
    /// Resolves <paramref name="id"/> (IANA or Windows) to a
    /// <see cref="TimeZoneInfo"/>. Unknown ids throw a
    /// <see cref="TimeZoneNotFoundException"/> naming the id.
    /// </summary>
    public TimeZoneInfo FindSystemTimeZoneById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            return _platformFind(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // NLS mode rejects IANA ids; fall through to the Windows-id paths.
        }

        if (TryConvertIanaIdToWindowsId(id, out var windowsId))
        {
            try
            {
                return _platformFind(windowsId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new TimeZoneNotFoundException(
                    $"The time zone id '{id}' maps to the Windows time zone '{windowsId}', which is not installed on this host.", ex);
            }
        }

        if (id.Contains('/') && !PlatformConvertsIanaIds())
        {
            // NLS mode: the platform knows no IANA ids at all, so "not found"
            // here means "not in the built-in table", not "not a real zone".
            // Name the supported ids so the operator can fix the configuration
            // without reading source.
            throw new TimeZoneNotFoundException(
                $"The IANA time zone id '{id}' could not be resolved: this host has no ICU " +
                "(NLS globalization mode, Windows before 10 1903 / Server 2019), and the id is not in " +
                "the built-in IANA-to-Windows table. The table covers: " +
                string.Join(", ", BuiltInIanaToWindowsIds.Keys) +
                ". Configure one of these ids, or run on a host with ICU.");
        }

        throw new TimeZoneNotFoundException(
            $"Unknown time zone id '{id}'. Configure an IANA identifier such as Europe/Stockholm, or UTC.");
    }

    /// <summary>
    /// True when the platform itself converts IANA ids (ICU present). Probed
    /// once with a known id and cached; a throwing conversion delegate counts
    /// as "cannot convert".
    /// </summary>
    private bool PlatformConvertsIanaIds()
    {
        if (_platformConvertsIanaIds is null)
        {
            try
            {
                _platformConvertsIanaIds = _platformConvert("Europe/Stockholm", out _);
            }
            catch
            {
                _platformConvertsIanaIds = false;
            }
        }

        return _platformConvertsIanaIds.Value;
    }

    /// <summary>
    /// Converts an IANA id to its Windows time zone id, using the platform
    /// conversion first and the built-in table as the NLS-mode fallback.
    /// Returns false for unknown ids and for ids that are not IANA ids.
    /// </summary>
    public bool TryConvertIanaIdToWindowsId(string ianaId, out string windowsId)
    {
        if (!string.IsNullOrWhiteSpace(ianaId) &&
            _platformConvert(ianaId, out var converted) &&
            converted is not null)
        {
            windowsId = converted;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ianaId) && BuiltInIanaToWindowsIds.TryGetValue(ianaId, out var mapped))
        {
            windowsId = mapped;
            return true;
        }

        windowsId = string.Empty;
        return false;
    }
}
