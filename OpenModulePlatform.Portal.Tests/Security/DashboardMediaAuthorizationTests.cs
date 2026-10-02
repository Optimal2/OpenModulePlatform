using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Pages;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class DashboardMediaAuthorizationTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    [InlineData(false, true, "invalid")]
    [InlineData(true, true, "invalid")]
    public async Task MusicHandlers_WithoutOmpUser_ForbidBeforeReadingMedia(bool track, bool authenticated, string? userId)
    {
        var model = CreateModel(authenticated, userId);

        var result = track ? await model.OnGetMusicTrack(1, default) : await model.OnGetMusicPlaylist(default);
        Assert.IsType<ForbidResult>(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MusicHandlers_WithOmpUser_ReachMediaStorage(bool track)
    {
        var model = CreateModel(true, "42");

        // Missing configuration is a storage sentinel: no SQL connection is opened.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => track
            ? model.OnGetMusicTrack(1, default)
            : model.OnGetMusicPlaylist(default));
        Assert.Equal("Missing connection string: ConnectionStrings:OmpDb", error.Message);
    }

    private static IndexModel CreateModel(bool authenticated, string? userId)
    {
        var identity = new ClaimsIdentity(authenticated ? "Test" : null);
        if (userId is not null)
        {
            identity.AddClaim(new Claim(OmpAuthDefaults.UserIdClaimType, userId));
        }

        var music = new PortalMusicPlayerService(new SqlConnectionFactory(new ConfigurationBuilder().Build()));
        // Other dashboard services are outside these handlers and must not be used.
        return new IndexModel(Microsoft.Extensions.Options.Options.Create(new WebAppOptions()), null!, null!, null!, null!,
            null!, music, null!, null!, null!, null!)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }
}
