using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class ModUpdateWindow : Window
{
    private readonly string _homepageUrl;
    private readonly Func<CancellationToken, Task>? _updateAction;
    private bool _isChinese;
    private bool _isUpdating;

    public ModUpdateWindow()
    {
        InitializeComponent();
        _homepageUrl = string.Empty;
        _updateAction = null;
    }

    public ModUpdateWindow(
        ModEntry mod,
        ModUpdateCheckResult update,
        bool isChinese,
        Func<CancellationToken, Task>? updateAction = null)
        : this()
    {
        var modName = string.IsNullOrWhiteSpace(mod.Name) ? mod.ModId : mod.Name;
        _homepageUrl = update.HomepageUrl;
        _updateAction = updateAction;
        _isChinese = isChinese;

        Title = T("模组更新", "Mod Update", isChinese);
        TitleTextBlock.Text = T($"{modName} 有新版本", $"{modName} has an update", isChinese);
        SummaryTextBlock.Text = T(
            $"模组 ID：{mod.ModId}",
            $"Mod ID: {mod.ModId}",
            isChinese);
        CurrentVersionLabelTextBlock.Text = T("当前版本", "Current version", isChinese);
        LatestVersionLabelTextBlock.Text = T("最新版本", "Latest version", isChinese);
        ReleaseDateLabelTextBlock.Text = T("发布时间", "Released", isChinese);
        CurrentVersionTextBlock.Text = update.CurrentVersion;
        LatestVersionTextBlock.Text = update.LatestVersion;
        ReleaseDateTextBlock.Text = string.IsNullOrWhiteSpace(update.ReleaseDate)
            ? T("未知", "Unknown", isChinese)
            : update.ReleaseDate;
        ChangelogLabelTextBlock.Text = T("更新日志", "Changelog", isChinese);
        NoChangelogTextBlock.Text = T("该版本没有提供更新日志。", "No changelog was provided for this release.", isChinese);
        DownloadHintTextBlock.Text = string.IsNullOrWhiteSpace(update.DownloadUrl)
            ? string.Empty
            : T("点击“更新”按钮将下载并替换当前模组。", "Click Update to download and replace the installed mod.", isChinese);
        HomepageTextBlock.Text = string.IsNullOrWhiteSpace(update.HomepageUrl)
            ? string.Empty
            : T($"官网：{update.HomepageUrl}", $"Homepage: {update.HomepageUrl}", isChinese);
        OpenHomepageButton.Content = T("打开官网", "Open homepage", isChinese);
        OpenHomepageButton.IsVisible = !string.IsNullOrWhiteSpace(update.HomepageUrl);
        UpdateButton.Content = T("更新", "Update", isChinese);
        UpdateButton.IsVisible = update.IsUpdateAvailable &&
                                  !string.IsNullOrWhiteSpace(update.DownloadUrl) &&
                                  _updateAction is not null;
        CloseButton.Content = T("关闭", "Close", isChinese);

        if (string.IsNullOrWhiteSpace(update.Changelog))
        {
            ChangelogPanel.IsVisible = false;
            NoChangelogTextBlock.IsVisible = true;
        }
        else
        {
            ChangelogTextBlock.Text = update.Changelog;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_isUpdating || _updateAction is null)
            return;

        _isUpdating = true;
        UpdateButton.IsEnabled = false;
        OpenHomepageButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _updateAction(cancellation.Token);
            Close(true);
        }
        catch (OperationCanceledException)
        {
            ShowUpdateError(T("更新超时。", "Update timed out.", _isChinese));
        }
        catch (Exception ex)
        {
            ShowUpdateError(T($"更新失败：{ex.Message}", $"Update failed: {ex.Message}", _isChinese));
        }
        finally
        {
            _isUpdating = false;
            UpdateButton.IsEnabled = true;
            OpenHomepageButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void ShowUpdateError(string message)
    {
        DownloadHintTextBlock.Text = message;
        DownloadHintTextBlock.Foreground = Avalonia.Media.Brushes.IndianRed;
    }

    private void OnOpenHomepageClick(object? sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_homepageUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // The dialog is informational; failure to open an external browser is non-fatal.
        }
    }

    private static string T(string zh, string en, bool isChinese)
    {
        try
        {
            return ServiceLocator.GetRequiredService<ILocalizationService>().Resolve(zh, en);
        }
        catch (InvalidOperationException)
        {
            return isChinese ? zh : en;
        }
    }
}
