using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class RobotCommandScopeTests
{
    private static readonly InstanceProfile ProfileA = new()
    {
        Id = "profile-a",
        Name = "Server A"
    };

    private static readonly InstanceProfile ProfileB = new()
    {
        Id = "profile-b",
        Name = "Server B"
    };

    [Fact]
    public void PrivateCommand_UsesProfileBoundToSuperUser()
    {
        var scope = CreateScope(
            bindings:
            [
                Binding(ProfileA.Id, groupId: 10001, superUserId: 42)
            ]);

        var result = Resolve(scope, userId: 42, groupId: null, selector: string.Empty);

        Assert.Same(ProfileA, result.Profile);
        Assert.Empty(result.ErrorMessage);
    }

    [Fact]
    public void BoundSuperUser_CannotControlAnotherProfileExplicitly()
    {
        var scope = CreateScope(
            bindings:
            [
                Binding(ProfileA.Id, groupId: 10001, superUserId: 42)
            ]);

        var result = Resolve(scope, userId: 42, groupId: null, selector: ProfileB.Name);

        Assert.Null(result.Profile);
        Assert.Contains("无权控制", result.ErrorMessage);
    }

    [Fact]
    public void MultipleFallbackProfiles_RequireExplicitTarget()
    {
        var scope = CreateScope(superUsers: [42]);

        var result = Resolve(scope, userId: 42, groupId: null, selector: string.Empty);

        Assert.Null(result.Profile);
        Assert.Contains("明确选择", result.ErrorMessage);
    }

    [Fact]
    public void MultipleGroupProfiles_RequireExplicitTargetForGlobalAdmin()
    {
        var scope = CreateScope(
            superUsers: [42],
            bindings:
            [
                Binding(ProfileA.Id, groupId: 10001),
                Binding(ProfileB.Id, groupId: 10001)
            ]);

        var ambiguous = Resolve(scope, userId: 42, groupId: 10001, selector: string.Empty);
        var explicitTarget = Resolve(scope, userId: 42, groupId: 10001, selector: ProfileB.Name);

        Assert.Null(ambiguous.Profile);
        Assert.Contains("明确选择", ambiguous.ErrorMessage);
        Assert.Same(ProfileB, explicitTarget.Profile);
    }

    [Fact]
    public void AdminCommand_FromUnboundGroup_IsRejected()
    {
        var scope = CreateScope(superUsers: [42], boundGroupIds: [10001]);

        var result = Resolve(scope, userId: 42, groupId: 20002, selector: ProfileA.Name);

        Assert.Null(result.Profile);
        Assert.Contains("当前群未绑定", result.ErrorMessage);
    }

    [Fact]
    public void InvalidBinding_DoesNotPromoteSuperUserToGlobalAdmin()
    {
        var scope = CreateScope(
            superUsers: [42],
            bindings:
            [
                Binding(string.Empty, superUserId: 42)
            ]);

        Assert.False(scope.IsAdmin(42));
    }

    private static RobotCommandScope CreateScope(
        IReadOnlyList<long>? superUsers = null,
        IReadOnlyList<long>? boundGroupIds = null,
        IReadOnlyList<RobotProfileBinding>? bindings = null)
    {
        return new RobotCommandScope(superUsers, boundGroupIds, bindings);
    }

    private static RobotServerCommandTargetResolution Resolve(
        RobotCommandScope scope,
        long userId,
        long? groupId,
        string selector)
    {
        return RobotServerCommandTargetResolver.Resolve(
            [ProfileA, ProfileB],
            [ProfileA.Id, ProfileB.Id],
            scope,
            userId,
            groupId,
            selector);
    }

    private static RobotProfileBinding Binding(
        string profileId,
        long groupId = 0,
        long superUserId = 0)
    {
        return new RobotProfileBinding
        {
            ProfileId = profileId,
            GroupId = groupId > 0 ? groupId.ToString() : string.Empty,
            SuperUserId = superUserId > 0 ? superUserId.ToString() : string.Empty
        };
    }
}
