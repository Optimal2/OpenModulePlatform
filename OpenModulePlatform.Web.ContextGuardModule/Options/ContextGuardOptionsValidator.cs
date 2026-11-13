using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Options;

public sealed partial class ContextGuardOptionsValidator : IValidateOptions<ContextGuardOptions>
{
    public ValidateOptionsResult Validate(string? name, ContextGuardOptions options)
    {
        var errors = new List<string>();
        ValidateRange(options.HandoffTtlSeconds, 1, 3600, "HandoffTtlSeconds", errors);
        ValidateRange(options.HeartbeatIntervalSeconds, 1, 300, "HeartbeatIntervalSeconds", errors);
        ValidateRange(options.HeartbeatTimeoutSeconds, 1, 3600, "HeartbeatTimeoutSeconds", errors);
        ValidateRange(options.HeartbeatFailureLimit, 1, 100, "HeartbeatFailureLimit", errors);
        ValidateRange(options.MaxPendingHandoffs, 1, 100_000, "MaxPendingHandoffs", errors);
        ValidateRange(options.MaxFieldCount, 1, 1000, "MaxFieldCount", errors);
        ValidateRange(options.MaxTotalFormBytes, 128, 1_048_576, "MaxTotalFormBytes", errors);
        ValidateRange(options.MaxFieldValueLength, 1, 100_000, "MaxFieldValueLength", errors);

        if (options.HeartbeatTimeoutSeconds < options.HeartbeatIntervalSeconds * 2)
        {
            errors.Add("HeartbeatTimeoutSeconds must be at least twice HeartbeatIntervalSeconds.");
        }

        if (!string.Equals(options.Scope, ContextGuardScopes.User, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Scope, ContextGuardScopes.UserAndClient, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Scope must be 'user' or 'user+client'.");
        }

        if (options.Profiles.Count == 0)
        {
            errors.Add("At least one profile must be configured.");
        }

        foreach (var (profileKey, profile) in options.Profiles)
        {
            if (!ProfileKeyRegex().IsMatch(profileKey))
            {
                errors.Add($"Profile key '{profileKey}' must contain 1-64 letters, digits, '_' or '-'.");
            }

            if (!Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(target.UserInfo))
            {
                errors.Add($"Profile '{profileKey}' must have an absolute HTTP(S) TargetUrl without user information.");
            }

            if (!string.Equals(profile.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profile.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Profile '{profileKey}' Method must be GET or POST.");
            }

            if (!string.Equals(profile.Delivery, ContextGuardDelivery.Embed, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profile.Delivery, ContextGuardDelivery.Redirect, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Profile '{profileKey}' Delivery must be embed or redirect.");
            }

            if (profile.AllowedFieldNames.Count == 0)
            {
                errors.Add($"Profile '{profileKey}' must allow at least one field name.");
            }

            if (profile.AllowedFieldNames.Any(string.IsNullOrWhiteSpace)
                || profile.AllowedFieldNames.Any(field => field.Length > 128)
                || profile.AllowedFieldNames.Distinct(StringComparer.Ordinal).Count() != profile.AllowedFieldNames.Count)
            {
                errors.Add($"Profile '{profileKey}' AllowedFieldNames must be unique, non-empty and at most 128 characters.");
            }

            if (string.IsNullOrWhiteSpace(profile.CorrelationFieldName)
                || profile.CorrelationFieldName.Length > 128
                || !profile.AllowedFieldNames.Contains(profile.CorrelationFieldName, StringComparer.Ordinal))
            {
                errors.Add($"Profile '{profileKey}' CorrelationFieldName must be included in AllowedFieldNames.");
            }

            if (profile.AllowedOriginHosts.Any(host => string.IsNullOrWhiteSpace(host)
                || host.Contains('/') || host.Contains(':') || host.Contains('@')))
            {
                errors.Add($"Profile '{profileKey}' AllowedOriginHosts entries must be host names without scheme, port or path.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name, List<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }

    internal static bool IsValidProfileKey(string profileKey) => ProfileKeyRegex().IsMatch(profileKey);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileKeyRegex();
}
