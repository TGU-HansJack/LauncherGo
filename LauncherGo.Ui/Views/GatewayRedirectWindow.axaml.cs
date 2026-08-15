using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LauncherGo.Ui.Views;

public enum GatewayRedirectOperation
{
    Player,
    Evacuate,
    Maintenance
}

public partial class GatewayRedirectWindow : Window
{
    private readonly GatewayRedirectOperation _operation;
    private readonly bool _isChinese;
    private readonly ObservableCollection<GatewayRedirectTargetItem> _targets = [];

    public GatewayRedirectWindow()
    {
        InitializeComponent();
    }

    public GatewayRedirectWindow(
        GatewayRedirectOperation operation,
        string sourceName,
        IEnumerable<GatewayRedirectTargetItem> targets,
        bool isChinese)
        : this()
    {
        _operation = operation;
        _isChinese = isChinese;
        _targets = new ObservableCollection<GatewayRedirectTargetItem>(targets);
        TargetComboBox.ItemsSource = _targets;
        TargetComboBox.SelectedIndex = _targets.Count > 0 ? 0 : -1;
        Title = operation switch
        {
            GatewayRedirectOperation.Player => T("重定向玩家", "Redirect Player"),
            GatewayRedirectOperation.Evacuate => T("疏散服务器", "Evacuate Server"),
            _ => T("进入维护", "Enter Maintenance")
        };
        TitleTextBlock.Text = Title;
        SourceTextBlock.Text = T($"来源后端：{sourceName}", $"Source backend: {sourceName}");
        PlayerPanel.IsVisible = operation == GatewayRedirectOperation.Player;
        PlayerLabelTextBlock.Text = T("玩家名称或 UID", "Player name or UID");
        PlayerTextBox.PlaceholderText = T("玩家名称或 UID", "Player name or UID");
        TargetLabelTextBlock.Text = T("目标后端（ServerId）", "Target backend (ServerId)");
        HintTextBlock.Text = operation switch
        {
            GatewayRedirectOperation.Player => T("仅向所选玩家发送重定向请求。", "Only the selected player receives the redirect request."),
            GatewayRedirectOperation.Evacuate => T("将向来源服务器的全部在线玩家发送重定向请求。", "All online players on the source server receive a redirect request."),
            _ => T("来源后端将切换为 Draining，不再接受普通新连接。", "The source backend becomes Draining and stops accepting ordinary new connections.")
        };
        CancelButton.Content = T("取消", "Cancel");
        ConfirmButton.Content = operation == GatewayRedirectOperation.Maintenance
            ? T("确认维护", "Confirm")
            : T("发送", "Send");
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var target = TargetComboBox.SelectedItem as GatewayRedirectTargetItem;
        var player = PlayerTextBox.Text?.Trim() ?? string.Empty;
        if (target is null)
        {
            ShowError(T("请选择一个可用的目标后端。", "Select an available target backend."));
            return;
        }

        if (_operation == GatewayRedirectOperation.Player && string.IsNullOrWhiteSpace(player))
        {
            ShowError(T("请输入玩家名称或 UID。", "Enter a player name or UID."));
            return;
        }

        Close(new GatewayRedirectRequest(_operation, target.ServerId, player));
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.IsVisible = true;
    }

    private string T(string zh, string en) => _isChinese ? zh : en;
}

public sealed record GatewayRedirectTargetItem(string ServerId, string DisplayName);

public sealed record GatewayRedirectRequest(
    GatewayRedirectOperation Operation,
    string TargetServerId,
    string PlayerNameOrUid);
