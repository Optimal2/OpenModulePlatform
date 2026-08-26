using OpenModulePlatform.Portal.Pages.Admin.Rbac;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 2: the silent
/// ADUser -> OmpUser rewrite in the RBAC admin page must be reported to the
/// operator and must be possible to decline. Follow-up phase 2, finding 1:
/// when the principal resolves to more than one active OMP user the rewrite
/// must ABSTAIN and report the ambiguity, with the same wording as the bulk
/// move's AmbiguousLinkedUsers reason. Test data is invented (CONTOSO\anna).
/// </summary>
public sealed class RoleAdUserRewriteTests
{
    [Fact]
    public void Decide_LinkedAdUser_Default_RewritesToOmpUserAndReportsIt()
    {
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            new AdLinkedActiveOmpUserResolution(1, 42),
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
            new AdLinkedActiveOmpUserResolution(1, 42),
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
            AdLinkedActiveOmpUserResolution.None,
            preserveLiteralAdPrincipal: false);

        Assert.Equal("ADUser", result.PrincipalType);
        Assert.Equal(@"CONTOSO\anna", result.Principal);
        Assert.Null(result.RewrittenFrom);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Decide_AmbiguousLinkedUsers_AbstainsAndReportsAmbiguity()
    {
        // Fail-closed: two active OMP users can hold enabled AD links that
        // differ only in letter case (production uniqueness is on the SHA-256
        // hash of the raw key). No role may be assigned on a guess, and the
        // ambiguity must be reported to the operator instead of silently
        // picking the lowest user id.
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            new AdLinkedActiveOmpUserResolution(2, null),
            preserveLiteralAdPrincipal: false);

        Assert.Null(result.PrincipalType);
        Assert.Null(result.Principal);
        Assert.Null(result.RewrittenFrom);
        Assert.Equal(
            "The principal resolves to more than one active OMP user, so nothing was added. Resolve the duplicate AD links first.",
            result.ErrorMessage);
    }

    [Fact]
    public void Decide_AmbiguousLinkedUsers_PreserveLiteral_KeepsAdPrincipal()
    {
        // Preserve-literal is an explicit operator choice to store the exact AD
        // principal, not a rewrite, so it is honored even when the rewrite
        // would have abstained.
        var result = RoleModel.DecideAdUserNormalization(
            @"CONTOSO\anna",
            new AdLinkedActiveOmpUserResolution(2, null),
            preserveLiteralAdPrincipal: true);

        Assert.Equal("ADUser", result.PrincipalType);
        Assert.Equal(@"CONTOSO\anna", result.Principal);
        Assert.Null(result.RewrittenFrom);
        Assert.Null(result.ErrorMessage);
    }
}
