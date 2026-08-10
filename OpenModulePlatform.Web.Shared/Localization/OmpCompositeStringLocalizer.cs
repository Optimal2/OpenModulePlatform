using Microsoft.Extensions.Localization;

namespace OpenModulePlatform.Web.Shared.Localization;

/// <summary>
/// Looks a string up in the app's own resource first and falls back to the
/// shared resource. Framework-level template strings (the validation message
/// templates stamped by <see cref="OmpValidationMetadataProvider"/>) live in
/// SharedResource once, while an app can still override any of them by
/// adding the same key to its own resource.
/// </summary>
public sealed class OmpCompositeStringLocalizer : IStringLocalizer
{
    private readonly IStringLocalizer _primary;
    private readonly IStringLocalizer _fallback;

    public OmpCompositeStringLocalizer(IStringLocalizer primary, IStringLocalizer fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public LocalizedString this[string name]
    {
        get
        {
            var result = _primary[name];
            return result.ResourceNotFound ? _fallback[name] : result;
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var probe = _primary[name];
            return probe.ResourceNotFound ? _fallback[name, arguments] : _primary[name, arguments];
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        => _primary.GetAllStrings(includeParentCultures)
            .Concat(_fallback.GetAllStrings(includeParentCultures));
}
