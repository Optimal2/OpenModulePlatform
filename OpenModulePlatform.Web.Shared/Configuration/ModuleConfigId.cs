using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Configuration;

/// <summary>
/// A typed wrapper for the module-owned <c>ConfigId</c> bridge stored on
/// <c>omp.AppInstances.ConfigId</c> and <c>omp.InstanceTemplateAppInstances.DesiredConfigId</c>.
/// </summary>
/// <remarks>
/// This is intentionally distinct from the global <c>omp.config_settings.ConfigId</c> primary key.
/// The bridge points at a module-owned <c>Configurations</c> table and therefore cannot have a
/// foreign key enforced by the core schema.
/// </remarks>
public readonly record struct ModuleConfigId(int Value)
{
    /// <summary>
    /// Returns <see langword="true"/> when the value is a valid positive identifier.
    /// All <c>ConfigId</c> columns use <c>IDENTITY(1,1)</c>, so zero and negative values are invalid.
    /// </summary>
    public bool IsValid => Value > 0;

    /// <summary>
    /// Converts a database <c>int NULL</c> into a typed value, treating <c>null</c>, zero and
    /// negative values as "no config selected".
    /// </summary>
    public static ModuleConfigId? FromNullable(int? value)
        => value is > 0 ? new ModuleConfigId(value.Value) : null;

    /// <summary>
    /// Converts the typed value back into a database <c>int NULL</c>.
    /// </summary>
    public int? ToNullable()
        => IsValid ? Value : null;

    /// <summary>
    /// Parses a string representation into a <see cref="ModuleConfigId"/>.
    /// Used by ASP.NET Core model binding.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out ModuleConfigId result)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
        {
            result = new ModuleConfigId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Implicit conversion to <see cref="int"/> for backward-compatible serialization and
    /// SQL parameter binding.
    /// </summary>
    public static implicit operator int(ModuleConfigId id)
        => id.Value;

    /// <inheritdoc />
    public override string ToString()
        => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the identifier using the supplied format and provider.
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => Value.ToString(format, formatProvider);
}
