using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

public partial class LithosProbeReleaseSelectionWindow : Window
{
    private readonly bool _isChinese;
    private readonly ObservableCollection<ReleaseItem> _items = [];

    public LithosProbeReleaseSelectionWindow()
    {
        InitializeComponent();
    }

    public LithosProbeReleaseSelectionWindow(
        string profileName,
        string gameVersion,
        IReadOnlyList<LithosProbeRelease> releases,
        bool isChinese)
        : this()
    {
        _isChinese = isChinese;
        Title = T("选择 Lithos Probe 版本", "Choose Lithos Probe version");
        TitleTextBlock.Text = T("选择要部署的官方发布", "Choose an official release to deploy");
        HintTextBlock.Text = T(
            $"{profileName} 使用 Vintage Story {gameVersion}。没有精确兼容的官方发布，请确认要安装的版本。",
            $"{profileName} uses Vintage Story {gameVersion}. No exact official match is available; confirm the release to install.");
        SelectionHintTextBlock.Text = T("仅显示官方 ModDB 发布。", "Only official ModDB releases are shown.");
        InstallButton.Content = T("部署", "Deploy");
        CancelButton.Content = T("取消", "Cancel");

        foreach (var release in releases)
        {
            _items.Add(new ReleaseItem(release, gameVersion, isChinese));
        }

        ReleaseListBox.ItemsSource = _items;
        ReleaseListBox.SelectedIndex = _items.Count > 0 ? 0 : -1;
        UpdateInstallButton();
    }

    private void OnReleaseSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateInstallButton();

    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ReleaseListBox.SelectedItem is ReleaseItem item)
        {
            Close(item.Release);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void UpdateInstallButton() => InstallButton.IsEnabled = ReleaseListBox.SelectedItem is ReleaseItem;

    private string T(string zh, string en) => _isChinese ? zh : en;

    private sealed class ReleaseItem
    {
        private readonly LithosProbeRelease _release;
        private readonly string _gameVersion;
        private readonly bool _isChinese;

        public ReleaseItem(LithosProbeRelease release, string gameVersion, bool isChinese)
        {
            _release = release;
            _gameVersion = gameVersion;
            _isChinese = isChinese;
        }

        public LithosProbeRelease Release => _release;
        public string Version => _release.Version;
        public string CompatibilityText => _release.SupportedGameVersions.Count == 0
            ? (_isChinese ? "未提供支持版本标签" : "No supported-version tags supplied")
            : (_isChinese ? "支持：" : "Supports: ") + string.Join(", ", _release.SupportedGameVersions) +
              (_release.SupportsGameVersion(_gameVersion) ? (_isChinese ? "（精确匹配）" : " (exact match)") : string.Empty);
        public string ReleaseDateText => _release.CreatedAtUtc is null
            ? (_isChinese ? "发布时间未知" : "Release date unknown")
            : (_isChinese ? "发布：" : "Released: ") + _release.CreatedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
