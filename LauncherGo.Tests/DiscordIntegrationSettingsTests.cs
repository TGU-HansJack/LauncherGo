using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class DiscordIntegrationSettingsTests
{
    [Fact]
    public void MissingDiscordNode_UsesIndependentDefaults()
    {
        var preferences = JsonSerializer.Deserialize<LauncherPreferences>("{\"Robot\":{\"AccessToken\":\"qq-token\"}}");

        Assert.NotNull(preferences);
        Assert.NotNull(preferences.Discord);
        Assert.Empty(preferences.Discord.BotToken);
        Assert.Equal("qq-token", preferences.Robot.AccessToken);
    }

    [Fact]
    public void Normalize_DeduplicatesSnowflakesAndBindings()
    {
        var settings = DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings
        {
            ReconnectIntervalSec = 0,
            AdminUserIds = [" 42 ", "42", "invalid", "0"],
            AdminRoleIds = ["99", "099"],
            ProfileBindings =
            [
                Binding(" profile-a ", " 100 ", "200"),
                Binding("profile-a", "100", "200"),
                Binding("profile-a", "bad", "200"),
                Binding("", "100", "200")
            ]
        });

        Assert.Equal(DiscordIntegrationSettings.DefaultReconnectIntervalSec, settings.ReconnectIntervalSec);
        Assert.Equal(["42"], settings.AdminUserIds);
        Assert.Equal(["99"], settings.AdminRoleIds);
        var binding = Assert.Single(settings.ProfileBindings);
        Assert.Equal("profile-a", binding.ProfileId);
        Assert.Equal("100", binding.GuildId);
        Assert.Equal("200", binding.ChannelId);
    }

    [Theory]
    [InlineData("rules", true)]
    [InlineData("/rules-cn_2", true)]
    [InlineData("/Rules", false)]
    [InlineData("/\u547d\u4ee4", false)]
    [InlineData("/this-command-name-is-longer-than-thirty-two", false)]
    public void NativeSlashCommandName_RequiresDiscordFormat(string command, bool expected)
    {
        Assert.Equal(expected, DiscordIntegrationSettingsRules.IsNativeSlashCommandName(command));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("not-a-discord-token", false)]
    [InlineData("MTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAw.abcde.valid-token-value-with-more-than-twenty", true)]
    public void BotToken_IsValidatedWithoutExposingToken(string token, bool expected)
    {
        Assert.Equal(expected, DiscordIntegrationSettingsRules.IsValidBotToken(token));
    }

    [Fact]
    public void Administrator_MatchesConfiguredUserOrRole()
    {
        var settings = DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings
        {
            AdminUserIds = ["10"],
            AdminRoleIds = ["20"]
        });

        Assert.True(DiscordIntegrationSettingsRules.IsAdministrator(settings, "10", []));
        Assert.True(DiscordIntegrationSettingsRules.IsAdministrator(settings, "11", ["20"]));
        Assert.False(DiscordIntegrationSettingsRules.IsAdministrator(settings, "11", ["21"]));
    }

    [Fact]
    public void FindBinding_OnlyMatchesGuildAndChannel()
    {
        var settings = DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings
        {
            ProfileBindings = [Binding("p", "100", "200")]
        });

        Assert.Equal("p", DiscordIntegrationSettingsRules.FindBinding(settings, "100", "200")?.ProfileId);
        Assert.Null(DiscordIntegrationSettingsRules.FindBinding(settings, "100", "201"));
    }

    [Theory]
    [InlineData("zh-cn", "服务器状态")]
    [InlineData("zh_TW", "伺服器狀態")]
    [InlineData("de-de", "Serverstatus")]
    [InlineData("es-es", "Estado del servidor")]
    [InlineData("pt-br", "Estado do servidor")]
    [InlineData("unknown", "Server Status")]
    public void DiscordText_UsesServerLanguageAndRegionalFallback(string language, string expected)
    {
        Assert.Equal(expected, DiscordBotText.Get(language, DiscordBotPhrase.ServerStatus));
    }

    [Fact]
    public void FormatServerStatus_ProducesReadableDiscordMarkdown()
    {
        var data = JsonNode.Parse("""
            {
              "name": "Vintage Story Server",
              "status": "RunGame",
              "version": "v1.22.3 (Stable)",
              "apiVersion": "1.22.0",
              "onlinePlayers": 0,
              "maxPlayers": 16,
              "worldName": "A new world",
              "address": ":42420",
              "description": "",
              "welcomeMessage": "Welcome {0}, may you survive well and prosper",
              "whitelistEnabled": false,
              "passwordProtected": false,
              "uptimeSeconds": 127,
              "performance": { "tps": 21, "averageTickTimeMs": 47.62 },
              "worldTime": "第0年 5月 1日 20:10:37",
              "season": "Spring"
            }
            """)!.AsObject();

        var result = DiscordBotText.FormatServerStatus(
            data,
            "zh-cn",
            new DateTimeOffset(2026, 9, 6, 12, 30, 0, TimeSpan.Zero));

        Assert.Contains("**服务器状态**", result);
        Assert.Contains("状态: **运行中**", result);
        Assert.Contains("玩家: **0/16**", result);
        Assert.Contains("季节: 春季", result);
        Assert.Contains("运行时间: 2分 7秒", result);
        Assert.Contains("性能: TPS `21` · Tick `47.62 ms`", result);
        Assert.DoesNotContain("performance: {", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandDescriptions_ForEveryServerLanguage_MeetDiscordLengthLimit()
    {
        string[] languages =
        [
            "en", "ar", "be", "cs", "da", "de", "es-es", "fr", "hu", "is", "it", "ja", "ko",
            "nl", "no", "pl", "pt-br", "pt-pt", "ru", "sr", "zh-cn", "zh-tw"
        ];
        var commandPhrases = Enum.GetValues<DiscordBotPhrase>()
            .Where(phrase => phrase >= DiscordBotPhrase.ShowCommands);

        foreach (var language in languages)
        foreach (var phrase in commandPhrases)
        {
            var description = DiscordBotText.Get(language, phrase);
            Assert.InRange(description.Length, 1, 100);
        }
    }

    private static DiscordProfileBinding Binding(string profileId, string guildId, string channelId) => new()
    {
        ProfileId = profileId,
        GuildId = guildId,
        ChannelId = channelId
    };
}
