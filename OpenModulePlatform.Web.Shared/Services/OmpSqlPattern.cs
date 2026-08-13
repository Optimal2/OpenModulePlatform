// File: OpenModulePlatform.Web.Shared/Services/OmpSqlPattern.cs
namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Escaping for values that are interpolated into a SQL <c>LIKE</c> pattern.
/// </summary>
/// <remarks>
/// R8-P3-10. Parameterising a search term stops SQL injection but not pattern injection: the
/// wildcards still live inside the parameter value. A term of <c>%</c> matches every row, and a
/// term full of <c>%</c> and <c>_</c> turns an indexed prefix search into a scan an anonymous
/// caller can trigger at will. IbsPackager has escaped its search terms since R2; the four OMP
/// call sites -- three in RbacAdminRepository and one in MessageService -- never got it, because
/// the helper was private to IbsPackager's repository. It lives in Web.Shared now so both the
/// Portal and Web.Shared can use the same one rather than growing a second copy, which is the
/// defect class this round exists to sweep for.
/// </remarks>
public static class OmpSqlPattern
{
    /// <summary>
    /// Escapes the <c>LIKE</c> metacharacters in <paramref name="value"/> using bracket escapes,
    /// so the result matches literally and needs no <c>ESCAPE</c> clause.
    /// </summary>
    /// <remarks>
    /// The bracket replacement has to run first: escaping <c>%</c> to <c>[%]</c> introduces
    /// brackets that a later bracket pass would escape again, turning the pattern into nonsense.
    /// </remarks>
    public static string EscapeLike(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a contains-pattern (<c>%term%</c>) whose term is escaped.
    /// </summary>
    public static string ContainsPattern(string? value) => $"%{EscapeLike(value)}%";
}
