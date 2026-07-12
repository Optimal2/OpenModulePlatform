using OpenModulePlatform.Web.Shared.Configuration;

namespace OpenModulePlatform.Portal.Tests.Configuration;

public sealed class ModuleConfigIdTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 1)]
    [InlineData(42, 42)]
    public void FromNullable_MapsValidAndInvalidValues(int? input, int? expected)
    {
        var actual = ModuleConfigId.FromNullable(input);
        Assert.Equal(expected, actual?.Value);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 1)]
    [InlineData(42, 42)]
    public void ToNullable_MapsValidAndInvalidValues(int value, int? expected)
    {
        var id = new ModuleConfigId(value);
        Assert.Equal(expected, id.ToNullable());
    }

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("42", true, 42)]
    [InlineData("0", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParse_HonoursPositiveIntegerRule(string? input, bool expectedSuccess, int expectedValue)
    {
        var success = ModuleConfigId.TryParse(input, out var result);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedValue, result.Value);
    }

    [Fact]
    public void ImplicitConversionToInt_ReturnsUnderlyingValue()
    {
        var id = new ModuleConfigId(7);
        int raw = id;
        Assert.Equal(7, raw);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    public void ToString_RendersUnderlyingValue(int value, string expected)
    {
        var id = new ModuleConfigId(value);
        Assert.Equal(expected, id.ToString());
    }
}
