// File: OpenModulePlatform.Portal.Tests/Services/AdRolePrincipalMigrationPlannerTests.cs
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Tier D tests for the bulk AD role-principal migration planner (campaign
/// ad-principalformen-hela-vagen-adfs-till-rbac, DEL 3). All principals, domains
/// and SIDs below are invented test data, never real customer data.
/// </summary>
public sealed class AdRolePrincipalMigrationPlannerTests
{
    [Fact]
    public void Plan_SingleActiveLinkedUser_Moves()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\anna",
            candidates: [Candidate(7, "Anna Example", isActive: true, hasNonAdEnabledLink: true)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, decision.Outcome);
        Assert.Equal(7, decision.TargetUserId);
        Assert.Equal("Anna Example", decision.TargetDisplayName);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.None, decision.SkipReason);
    }

    [Fact]
    public void Plan_LegacyUserPrincipalType_Moves()
    {
        var decision = PlanSingle(Input(
            principalType: "User",
            principal: @"CONTOSO\bertil",
            candidates: [Candidate(8, "Bertil Example", isActive: true, hasNonAdEnabledLink: true)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, decision.Outcome);
        Assert.Equal(8, decision.TargetUserId);
    }

    [Fact]
    public void Plan_NoLinkCandidates_SkipsNoEnabledAdLink()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\carina",
            candidates: []));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, decision.Outcome);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.NoEnabledAdLink, decision.SkipReason);
        Assert.Null(decision.TargetUserId);
    }

    [Fact]
    public void Plan_TwoDistinctActiveUsers_SkipsAmbiguous()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\dan",
            candidates:
            [
                Candidate(9, "Dan Example", isActive: true, hasNonAdEnabledLink: true),
                Candidate(10, "Dan Junior Example", isActive: true, hasNonAdEnabledLink: true)
            ]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, decision.Outcome);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.AmbiguousLinkedUsers, decision.SkipReason);
        Assert.Null(decision.TargetUserId);
    }

    [Fact]
    public void Plan_OnlyInactiveUsers_SkipsLinkedUserInactive()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\elena",
            candidates: [Candidate(11, "Elena Example", isActive: false, hasNonAdEnabledLink: false)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, decision.Outcome);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.LinkedUserInactive, decision.SkipReason);
        Assert.Null(decision.TargetUserId);
    }

    [Fact]
    public void Plan_OneActivePlusOneInactiveSameMatch_MovesToActiveUser()
    {
        // An inactive duplicate must not make the row ambiguous or blocked.
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\fredrik",
            candidates:
            [
                Candidate(12, "Fredrik Example", isActive: true, hasNonAdEnabledLink: true),
                Candidate(13, "Fredrik Example (old)", isActive: false, hasNonAdEnabledLink: false)
            ]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, decision.Outcome);
        Assert.Equal(12, decision.TargetUserId);
    }

    [Fact]
    public void Plan_AdGroupPrincipalType_IsRejected()
    {
        var decision = PlanSingle(Input(
            principalType: "ADGroup",
            principal: "S-1-5-21-11-22-33-1001",
            candidates: [Candidate(14, "Should Not Matter", isActive: true, hasNonAdEnabledLink: true)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, decision.Outcome);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.UnsupportedPrincipalType, decision.SkipReason);
        Assert.Null(decision.TargetUserId);
    }

    [Fact]
    public void Plan_AlreadyPresentAssignment_ReportsAlreadyPresent()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\greta",
            candidates: [Candidate(15, "Greta Example", isActive: true, hasNonAdEnabledLink: true)],
            ompUserAssignmentAlreadyPresent: true));

        Assert.Equal(AdRolePrincipalMigrationOutcome.AlreadyPresent, decision.Outcome);
        Assert.Equal(15, decision.TargetUserId);
        Assert.Equal(AdRolePrincipalMigrationSkipReason.None, decision.SkipReason);
    }

    [Fact]
    public void Plan_UserWithoutNonAdEnabledLink_SetsRiskNote()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\henrik",
            candidates: [Candidate(16, "Henrik Example", isActive: true, hasNonAdEnabledLink: false)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, decision.Outcome);
        Assert.True(decision.RiskNote);
    }

    [Fact]
    public void Plan_UserWithNonAdEnabledLink_HasNoRiskNote()
    {
        var decision = PlanSingle(Input(
            principalType: "ADUser",
            principal: @"CONTOSO\ingrid",
            candidates: [Candidate(17, "Ingrid Example", isActive: true, hasNonAdEnabledLink: true)]));

        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, decision.Outcome);
        Assert.False(decision.RiskNote);
    }

    [Fact]
    public void Plan_SameInputTwice_ProducesIdenticalDecisions()
    {
        // Determinism is what makes the preview equal the execute outcome.
        var rows = new[]
        {
            Input("ADUser", @"CONTOSO\anna", [Candidate(7, "Anna Example", true, true)]),
            Input("ADUser", @"CONTOSO\carina", []),
            Input("ADUser", @"CONTOSO\dan", [Candidate(9, "Dan Example", true, true), Candidate(10, "Dan Junior Example", true, false)]),
            Input("ADGroup", "S-1-5-21-11-22-33-1002", [])
        };

        var first = AdRolePrincipalMigrationPlanner.Plan(rows);
        var second = AdRolePrincipalMigrationPlanner.Plan(rows);

        Assert.Equal(first, second);
        Assert.Equal(
            [AdRolePrincipalMigrationOutcome.Move,
             AdRolePrincipalMigrationOutcome.Skipped,
             AdRolePrincipalMigrationOutcome.Skipped,
             AdRolePrincipalMigrationOutcome.Skipped],
            first.Select(static decision => decision.Outcome).ToArray());
    }

    private static AdRolePrincipalMigrationDecision PlanSingle(AdRolePrincipalMigrationInput input)
        => Assert.Single(AdRolePrincipalMigrationPlanner.Plan([input]));

    private static AdRolePrincipalMigrationInput Input(
        string principalType,
        string principal,
        IReadOnlyList<AdRolePrincipalMigrationLinkCandidate> candidates,
        bool ompUserAssignmentAlreadyPresent = false)
        => new(
            RoleId: 1,
            RoleName: "Invented Test Role",
            principalType,
            principal,
            ompUserAssignmentAlreadyPresent,
            candidates);

    private static AdRolePrincipalMigrationLinkCandidate Candidate(
        int userId,
        string displayName,
        bool isActive,
        bool hasNonAdEnabledLink)
        => new(userId, displayName, isActive, hasNonAdEnabledLink);
}
