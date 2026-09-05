using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

public partial class ServerPlayerDetailsWindow : Window
{
    private readonly ObservableCollection<ServerPlayerDetailItem> _basicItems = [];
    private readonly ObservableCollection<ServerPlayerDetailItem> _extendedItems = [];
    private readonly bool _isChinese;

    public ServerPlayerDetailsWindow()
    {
        InitializeComponent();
    }

    public ServerPlayerDetailsWindow(ServerOnlinePlayerInfo player, bool isChinese)
        : this()
    {
        _isChinese = isChinese;
        Title = T("玩家详细信息", "Player Details");
        PlayerNameTextBlock.Text = player.PlayerName;
        ProfileNameTextBlock.Text = player.ProfileName;
        BasicTitleTextBlock.Text = T("基础信息", "Basic Information");
        ExtendedTitleTextBlock.Text = T("扩展信息", "Extended Information");
        SourceTextBlock.Text = T("数据来源：服务器桥接", "Source: Server Bridge");
        CloseButton.Content = T("关闭", "Close");
        BasicItemsControl.ItemsSource = _basicItems;
        ExtendedItemsControl.ItemsSource = _extendedItems;

        AddBasic(T("玩家 UID", "Player UID"), Display(player.PlayerUid));
        AddBasic(T("连接状态", "Connection State"), Display(player.ConnectionState));
        AddBasic(T("延迟", "Latency"), player.PingMilliseconds.HasValue ? $"{player.PingMilliseconds.Value} ms" : "--");
        AddBasic(T("加入时间", "Joined"), FormatTimestamp(player.JoinedAtUtc));
        AddBasic(T("最近活动", "Last Activity"), FormatTimestamp(player.LastActivityUtc));

        if (player.HasExtendedInfo)
        {
            AddExtended(T("游戏模式", "Game Mode"), Display(player.GameMode));
            AddExtended(T("权限组", "Role"), Display(player.Role));
            AddExtended(T("维度", "Dimension"), player.Dimension?.ToString(CultureInfo.InvariantCulture) ?? "--");
            AddExtended(T("坐标", "Position"), FormatPosition(player));
        }
        else
        {
            AddExtended(
                T("状态", "Status"),
                T("未启用，或当前服务端版本未提供扩展玩家信息。", "Not enabled or unavailable on this server version."));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void AddBasic(string label, string value) => _basicItems.Add(new ServerPlayerDetailItem(label, value));

    private void AddExtended(string label, string value) => _extendedItems.Add(new ServerPlayerDetailItem(label, value));

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "--" : value;

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "--";

    private static string FormatPosition(ServerOnlinePlayerInfo player) =>
        player.X.HasValue && player.Y.HasValue && player.Z.HasValue
            ? $"{player.X.Value:F1}, {player.Y.Value:F1}, {player.Z.Value:F1}"
            : "--";

    private string T(string zh, string en)
    {
        try
        {
            return ServiceLocator.GetRequiredService<ILocalizationService>().Resolve(zh, en);
        }
        catch (InvalidOperationException)
        {
            return _isChinese ? zh : en;
        }
    }
}

public sealed record ServerPlayerDetailItem(string Label, string Value);
