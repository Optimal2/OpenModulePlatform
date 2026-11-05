using OpenModulePlatform.Portal.Services;
using System.Runtime.CompilerServices;
using UserEditModel = OpenModulePlatform.Portal.Pages.Admin.Users.EditModel;

namespace OpenModulePlatform.Portal.Tests.Pages;

public sealed class UserEditBitTextTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(null, "")]
    public void Bit_values_keep_their_invariant_display_text(bool? value, string expected)
    {
        // This presentation method does not use the page's request-bound services.
        var model = (UserEditModel)RuntimeHelpers.GetUninitializedObject(typeof(UserEditModel));
        var row = new PortalUserSettingValueRow(
            UserId: 1,
            UserSettingDefinitionId: 2,
            SettingCategory: "Portal",
            SettingName: "Example",
            ValueKind: PortalUserSettingsAdminRepository.BitValueKind,
            DefaultIntValue: null,
            DefaultStringValue: null,
            DefaultBitValue: null,
            Description: null,
            SortOrder: 0,
            IsEnabled: true,
            IntValue: null,
            StringValue: null,
            BitValue: value,
            UpdatedAt: DateTime.UnixEpoch);

        Assert.Equal(expected, model.PortalSettingValueText(row));
    }
}
