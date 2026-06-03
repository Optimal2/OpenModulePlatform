using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Portal.Pages.Users;

public sealed class AvatarModel : OmpSecurePageModel<PortalResource>
{
    private readonly UserProfileImageService _profileImages;

    public AvatarModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        UserProfileImageService profileImages)
        : base(options, rbac)
    {
        _profileImages = profileImages;
    }

    public async Task<IActionResult> OnGet(int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return NotFound();
        }

        var image = await _profileImages.GetProfileImageFileAsync(userId, ct);
        return image is null
            ? NotFound()
            : File(image.Data, image.ContentType);
    }
}
