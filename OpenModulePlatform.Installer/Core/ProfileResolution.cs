using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>The outcome of matching discovered host profiles against this computer.</summary>
public enum ProfileMatchOutcome
{
    /// <summary>No profile was found on disk at all.</summary>
    NoProfilesFound,

    /// <summary>Profiles exist, but none of them is prepared for this computer.</summary>
    NoMatch,

    /// <summary>Exactly one profile matches this computer.</summary>
    SingleMatch,

    /// <summary>More than one profile matches this computer.</summary>
    MultipleMatches
}

/// <summary>The result of resolving the host profile for this computer.</summary>
public sealed record ProfileResolution(
    ProfileMatchOutcome Outcome,
    BootstrapConfigProfile? Profile,
    IReadOnlyList<BootstrapConfigProfile> MatchingProfiles,
    IReadOnlySet<string> CandidateMachineNames)
{
    public static ProfileResolution NoProfilesFound { get; } = new(
        ProfileMatchOutcome.NoProfilesFound,
        null,
        [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Selects the host profile this installer will work with. Uses the shared
/// discovery and matching rules (<see cref="HostProfileDiscovery"/> /
/// <see cref="HostProfileMatcher"/>), with one deliberate tightening: sample
/// configs never count for a first install, even when no real profile matches.
/// </summary>
public static class InstallerProfileResolver
{
    /// <summary>
    /// Pure selection over already-discovered profiles. Sample configs are
    /// removed before matching so a first install can never lock onto a
    /// template; the shared matcher then decides 0 / 1 / many.
    /// </summary>
    public static ProfileResolution Resolve(
        IReadOnlyList<BootstrapConfigProfile> discoveredProfiles,
        IReadOnlySet<string> candidateMachineNames)
    {
        var realProfiles = discoveredProfiles
            .Where(profile => !HostProfileMatcher.IsSampleConfig(profile.ConfigPath))
            .ToArray();
        if (realProfiles.Length == 0 && discoveredProfiles.Count == 0)
        {
            return ProfileResolution.NoProfilesFound;
        }

        var matches = HostProfileMatcher.MatchProfiles(realProfiles, candidateMachineNames);
        return matches.Count switch
        {
            0 => new ProfileResolution(ProfileMatchOutcome.NoMatch, null, [], candidateMachineNames),
            1 => new ProfileResolution(ProfileMatchOutcome.SingleMatch, matches[0], matches, candidateMachineNames),
            _ => new ProfileResolution(ProfileMatchOutcome.MultipleMatches, null, matches, candidateMachineNames)
        };
    }

    /// <summary>
    /// Discovers profiles with the same search rules as the Bootstrapper
    /// (hosts\ and configs\ around the current directory, the executable
    /// folder and their parents) and resolves this computer's profile.
    /// </summary>
    public static ProfileResolution DiscoverAndResolve(IReadOnlySet<string>? candidateMachineNames = null)
    {
        var profiles = HostProfileDiscovery.ResolveProfiles(configPath: null, configDirectory: null);
        if (profiles.Count == 0)
        {
            return ProfileResolution.NoProfilesFound;
        }

        return Resolve(profiles, candidateMachineNames ?? HostProfileDiscovery.GetLocalMachineNames());
    }
}
