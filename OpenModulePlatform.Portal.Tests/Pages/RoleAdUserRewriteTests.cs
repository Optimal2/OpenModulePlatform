using OpenModulePlatform.Portal.Pages.Admin.Rbac;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 2: the silent
/// ADUser -> OmpUser rewrite in the RBAC admin page must be reported to the
/// operator and must be possible to decline. Test data is invented
/// (CONTOSO\anna).
/// </summary>
public sealed class RoleAdUserRewriteTests
{
    [Fact]
    public void Decide_LinkedAdUser_Default_RewritesToOmpUserAndReportsIt()
    {
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            linkedOmpUserId: 42,
            preserveLiteralAdPrincipal: false);

        Assert.Equal("OmpUser", result.PrincipalType);
        Assert.Equal("42", result.Principal);
        Assert.Equal(@"CONTOSO\anna", result.RewrittenFrom);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Decide_LinkedAdUser_PreserveLiteral_KeepsAdPrincipal()
    {
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            linkedOmpUserId: 42,
            preserveLiteralAdPrincipal: true);

        Assert.Equal("ADUser", result.PrincipalType);
        Assert.Equal(@"CONTOSO\anna", result.Principal);
        Assert.Null(result.RewrittenFrom);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Decide_UnlinkedAdUser_KeepsAdPrincipalWithoutRewrite()
    {
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            linkedOmpUserId: null,
            preserveLiteralAdPrincipal: false);

        Assert.Equal("ADUser", result.PrincipalType);
        Assert.Equal(@"CONTOSO\anna", result.Principal);
        Assert.Null(result.RewrittenFrom);
        Assert.Null(result.ErrorMessage);
    }
}
