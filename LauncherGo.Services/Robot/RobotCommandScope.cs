using System.Globalization;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

internal sealed class RobotCommandScope
{
    private readonly HashSet<long> _superUsers;
    private readonly HashSet<long> _boundGroupIds;
    private readonly IReadOnlyDictionary<long, HashSet<string>> _profilesByGroupId;
    private readonly IReadOnlyDictionary<long, HashSet<string>> _profilesBySuperUserId;

    public RobotCommandScope(
        IEnumerable<long>? superUsers,
        IEnumerable<long>? boundGroupIds,
        IEnumerable<RobotProfileBinding>? bindings)
    {
        _superUsers = (superUsers ?? [])
            .Where(static id => id > 0)
            .ToHashSet();
        _boundGroupIds = (boundGroupIds ?? [])
            .Where(static id => id > 0)
            .ToHashSet();

        var profilesByGroupId = new Dictionary<long, HashSet<string>>();
        var profilesBySuperUserId = new Dictionary<long, HashSet<string>>();
        var bindingSuperUserIds = new HashSet<long>();
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding.ProfileId?.Trim() ?? string.Empty;
            var superUserId = ParsePositiveInt64(binding.SuperUserId);
            if (superUserId > 0)
            {
                bindingSuperUserIds.Add(superUserId);
            }

            if (string.IsNullOrWhiteSpace(profileId))
            {
                continue;
            }

            var groupId = ParsePositiveInt64(binding.GroupId);
            if (groupId > 0)
            {
                AddProfile(profilesByGroupId, groupId, profileId);
                _boundGroupIds.Add(groupId);
            }

            if (superUserId > 0)
            {
                AddProfile(profilesBySuperUserId, superUserId, profileId);
            }
        }

        _superUsers.ExceptWith(bindingSuperUserIds);
        _profilesByGroupId = profilesByGroupId;
        _profilesBySuperUserId = profilesBySuperUserId;
    }

    public bool IsAdmin(long userId)
    {
        return userId > 0 &&
               (_superUsers.Contains(userId) || _profilesBySuperUserId.ContainsKey(userId));
    }

    public bool IsGroupBound(long groupId)
    {
        return groupId > 0 && _boundGroupIds.Contains(groupId);
    }

    public bool CanControlProfile(long userId, string profileId)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (_profilesBySuperUserId.TryGetValue(userId, out var scopedProfiles) && scopedProfiles.Count > 0)
        {
            return scopedProfiles.Contains(profileId.Trim());
        }

        return _superUsers.Contains(userId);
    }

    public IReadOnlyList<string> GetProfileIdsForGroup(long groupId)
    {
        return GetProfileIds(_profilesByGroupId, groupId);
    }

    public IReadOnlyList<string> GetProfileIdsForSuperUser(long userId)
    {
        return GetProfileIds(_profilesBySuperUserId, userId);
    }

    private static void AddProfile(
        IDictionary<long, HashSet<string>> profilesById,
        long id,
        string profileId)
    {
        if (!profilesById.TryGetValue(id, out var profiles))
        {
            profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            profilesById[id] = profiles;
        }

        profiles.Add(profileId);
    }

    private static IReadOnlyList<string> GetProfileIds(
        IReadOnlyDictionary<long, HashSet<string>> profilesById,
        long id)
    {
        return profilesById.TryGetValue(id, out var profiles)
            ? profiles.OrderBy(static profileId => profileId, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    private static long ParsePositiveInt64(string? value)
    {
        return long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : 0;
    }
}

internal static class RobotServerCommandTargetResolver
{
    public static RobotServerCommandTargetResolution Resolve(
        IReadOnlyList<InstanceProfile> profiles,
        IReadOnlyCollection<string> fallbackProfileIds,
        RobotCommandScope commandScope,
        long userId,
        long? groupId,
        string? targetSelector)
    {
        if (!commandScope.IsAdmin(userId))
        {
            return Failed("Permission denied. Super admin only.");
        }

        if (groupId is > 0 && !commandScope.IsGroupBound(groupId.Value))
        {
            return Failed("当前群未绑定机器人，不能执行服务器管理指令。");
        }

        var selector = targetSelector?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var explicitTarget = ResolveExplicitProfile(profiles, selector);
            if (!string.IsNullOrWhiteSpace(explicitTarget.ErrorMessage) || explicitTarget.Profile is null)
            {
                return explicitTarget;
            }

            return ValidateAuthorization(commandScope, userId, groupId, explicitTarget.Profile);
        }

        var contextProfileIds = groupId is > 0
            ? commandScope.GetProfileIdsForGroup(groupId.Value)
            : commandScope.GetProfileIdsForSuperUser(userId);
        var candidateIds = contextProfileIds.Count > 0
            ? contextProfileIds
            : fallbackProfileIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var candidates = candidateIds
            .Select(profileId => profiles.FirstOrDefault(profile =>
                profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
            .OfType<InstanceProfile>()
            .Where(profile => IsAuthorized(commandScope, userId, groupId, profile.Id))
            .DistinctBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
        {
            return Succeeded(candidates[0]);
        }

        if (candidates.Count > 1)
        {
            return Failed(
                $"可控制多个服务器档案，请使用档案名或 ID 明确选择：{FormatProfiles(candidates)}");
        }

        if (contextProfileIds.Count > 0)
        {
            return Failed("当前绑定的服务器档案不存在或无权控制，请检查机器人档案绑定。");
        }

        return Failed("未解析到唯一服务器档案，请使用档案名或 ID 明确选择。");
    }

    private static RobotServerCommandTargetResolution ResolveExplicitProfile(
        IReadOnlyList<InstanceProfile> profiles,
        string selector)
    {
        var idMatch = profiles.FirstOrDefault(profile =>
            profile.Id.Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (idMatch is not null)
        {
            return Succeeded(idMatch);
        }

        var nameMatches = profiles
            .Where(profile => profile.Name.Equals(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return nameMatches.Count switch
        {
            1 => Succeeded(nameMatches[0]),
            > 1 => Failed($"存在多个同名服务器档案，请使用档案 ID 明确选择：{FormatProfiles(nameMatches)}"),
            _ => Failed($"未找到服务器档案：{selector}")
        };
    }

    private static RobotServerCommandTargetResolution ValidateAuthorization(
        RobotCommandScope commandScope,
        long userId,
        long? groupId,
        InstanceProfile profile)
    {
        return IsAuthorized(commandScope, userId, groupId, profile.Id)
            ? Succeeded(profile)
            : Failed($"无权控制服务器档案：{profile.Name}");
    }

    private static bool IsAuthorized(
        RobotCommandScope commandScope,
        long userId,
        long? groupId,
        string profileId)
    {
        if (!commandScope.CanControlProfile(userId, profileId))
        {
            return false;
        }

        if (groupId is not > 0)
        {
            return true;
        }

        var groupProfileIds = commandScope.GetProfileIdsForGroup(groupId.Value);
        return groupProfileIds.Count == 0 ||
               groupProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatProfiles(IEnumerable<InstanceProfile> profiles)
    {
        return string.Join("、", profiles.Select(profile => $"{profile.Name} ({profile.Id})"));
    }

    private static RobotServerCommandTargetResolution Succeeded(InstanceProfile profile)
    {
        return new RobotServerCommandTargetResolution(profile, string.Empty);
    }

    private static RobotServerCommandTargetResolution Failed(string message)
    {
        return new RobotServerCommandTargetResolution(null, message);
    }
}

internal readonly record struct RobotServerCommandTargetResolution(
    InstanceProfile? Profile,
    string ErrorMessage);
