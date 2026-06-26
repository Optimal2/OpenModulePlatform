using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpAdfsDuplicateUserMergeRulesTests
{
    [Fact]
    public void Evaluate_WithEnabledAdfsLink_AllowsPreview()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 20,
            targetUserId: 10,
            targetUser: User(10, 1),
            duplicateUser: User(20, 1),
            adfsProviderExists: true,
            duplicateAuthLinks:
            [
                Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "enabled"),
                Link(2, OmpAuthDefaults.AdfsProviderDisplayName, "sid:S-1-5-21", "disabled")
            ],
            integrityConflictCount: 0);

        Assert.Equal(MergeAdfsDuplicateUserStatus.PreviewOnly, evaluation.Status);
        Assert.True(evaluation.CanMerge);
        Assert.Single(evaluation.AdfsLinksToMove);
        Assert.Single(evaluation.DisabledOrDeletedAdfsLinksIgnored);
        Assert.Equal(1, evaluation.SkippedAuthLinkCount);
    }

    [Fact]
    public void Evaluate_WithInactiveTarget_BlocksMerge()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 20,
            targetUserId: 10,
            targetUser: User(10, 2),
            duplicateUser: User(20, 1),
            adfsProviderExists: true,
            duplicateAuthLinks: [Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "enabled")],
            integrityConflictCount: 0);

        Assert.Equal(MergeAdfsDuplicateUserStatus.TargetNotActive, evaluation.Status);
        Assert.False(evaluation.CanMerge);
    }

    [Fact]
    public void Evaluate_WithoutEnabledAdfsLinks_BlocksMerge()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 20,
            targetUserId: 10,
            targetUser: User(10, 1),
            duplicateUser: User(20, 1),
            adfsProviderExists: true,
            duplicateAuthLinks: [Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "disabled")],
            integrityConflictCount: 0);

        Assert.Equal(MergeAdfsDuplicateUserStatus.NoEnabledAdfsLinks, evaluation.Status);
        Assert.False(evaluation.CanMerge);
    }

    [Fact]
    public void Evaluate_WithEnabledNonAdfsLinks_BlocksMerge()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 20,
            targetUserId: 10,
            targetUser: User(10, 1),
            duplicateUser: User(20, 1),
            adfsProviderExists: true,
            duplicateAuthLinks:
            [
                Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "enabled"),
                Link(2, "lpwd", "duplicate.user", "enabled")
            ],
            integrityConflictCount: 0);

        Assert.Equal(MergeAdfsDuplicateUserStatus.DuplicateHasEnabledNonAdfsLinks, evaluation.Status);
        Assert.False(evaluation.CanMerge);
        Assert.Single(evaluation.DuplicateNonAdfsLinks);
    }

    [Fact]
    public void Evaluate_WithIntegrityConflict_BlocksMerge()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 20,
            targetUserId: 10,
            targetUser: User(10, 1),
            duplicateUser: User(20, 1),
            adfsProviderExists: true,
            duplicateAuthLinks: [Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "enabled")],
            integrityConflictCount: 1);

        Assert.Equal(MergeAdfsDuplicateUserStatus.IntegrityAnomaly, evaluation.Status);
        Assert.False(evaluation.CanMerge);
        Assert.Equal(1, evaluation.ConflictCount);
    }

    [Fact]
    public void Evaluate_WithSystemUserId_BlocksMerge()
    {
        var evaluation = OmpAdfsDuplicateUserMergeRules.Evaluate(
            duplicateUserId: 0,
            targetUserId: 10,
            targetUser: User(10, 1),
            duplicateUser: User(0, 1),
            adfsProviderExists: true,
            duplicateAuthLinks: [Link(1, OmpAuthDefaults.AdfsProviderDisplayName, "name:DOMAIN\\user", "enabled")],
            integrityConflictCount: 0);

        Assert.Equal(MergeAdfsDuplicateUserStatus.SystemUserNotAllowed, evaluation.Status);
        Assert.False(evaluation.CanMerge);
    }

    private static MergeAdfsUserSummary User(int userId, int accountStatus)
        => new(userId, $"User {userId}", accountStatus, []);

    private static MergeAdfsAuthLinkPreview Link(
        int userAuthId,
        string providerDisplayName,
        string providerUserKey,
        string authStatus)
        => new(userAuthId, providerDisplayName, providerUserKey, authStatus);
}
