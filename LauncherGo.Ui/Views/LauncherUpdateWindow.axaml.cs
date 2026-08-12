using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

public partial class LauncherUpdateWindow : Window
{
    private readonly ILauncherUpdateService _updateService = null!;
    private readonly LauncherUpdateCheckResult _update = null!;
    private readonly GitHubProxyKind _proxy;
    private readonly bool _isChinese;
    private CancellationTokenSource? _downloadCts;

    // Avalonia's compiled-resource loader requires a public parameterless constructor.
    // The application uses the fully initialized constructor below when showing this window.
    public LauncherUpdateWindow()
    {
        InitializeComponent();
    }

    public LauncherUpdateWindow(
        ILauncherUpdateService updateService,
        LauncherUpdateCheckResult update,
        GitHubProxyKind proxy,
        bool isChinese)
    {
        _updateService = updateService;
        _update = update;
        _proxy = proxy;
        _isChinese = isChinese;

        InitializeComponent();
        ApplyContent();
        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
        };
    }

    private void ApplyContent()
    {
        TitleTextBlock.Text = _update.IsUpdateAvailable
            ? T("发现 LauncherGo 新版本", "LauncherGo update available")
            : T("LauncherGo 已是最新版本", "LauncherGo is up to date");
        VersionTextBlock.Text = T(
            $"当前 {_update.CurrentVersion}  ->  最新 {_update.LatestVersion}",
            $"Current {_update.CurrentVersion}  ->  Latest {_update.LatestVersion}");
        PackageKindTextBlock.Text = FormatPackageKind(_update.PackageKind);
        ReleaseNotesTitleTextBlock.Text = T("更新日志", "Release notes");
        RenderReleaseNotes(string.IsNullOrWhiteSpace(_update.Release.Body)
            ? T("此版本没有提供更新日志。", "No release notes were provided for this version.")
            : _update.Release.Body);
        OpenReleaseButton.Content = T("打开发布页", "Open release");
        CloseButton.Content = T("关闭", "Close");
        UpdateButton.Content = T("立即更新", "Update now");
        UpdateButton.IsVisible = _update.IsUpdateAvailable;
        UpdateButton.IsEnabled = _update.SelectedAsset is not null;

        StatusTextBlock.Text = _update.SelectedAsset is not null
            ? T($"将下载：{_update.SelectedAsset.Name}", $"Download: {_update.SelectedAsset.Name}")
            : _update.IsUpdateAvailable
                ? T("没有找到与当前安装方式匹配的更新包，请从发布页手动下载。", "No update asset matches this installation type. Download it from the release page.")
            : T("未发现需要安装的更新。", "No update needs to be installed.");
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Size short release notes naturally, while keeping long notes in a
        // bounded scrolling region so the action buttons remain visible.
        Dispatcher.UIThread.Post(ApplyAdaptiveHeight, DispatcherPriority.Loaded);
    }

    private void ApplyAdaptiveHeight()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null)
            return;

        var scale = RenderScaling > 0 ? RenderScaling : 1d;
        var workAreaHeight = screen.WorkingArea.Height / scale;
        var maxWindowHeight = Math.Max(MinHeight, Math.Min(workAreaHeight - 48, 560));
        MaxHeight = maxWindowHeight;

        var notesDesiredHeight = Math.Max(140, ReleaseNotesPanel.DesiredSize.Height + 24);
        var desiredWindowHeight = Math.Ceiling(notesDesiredHeight + 220);
        var targetHeight = Math.Clamp(desiredWindowHeight, MinHeight, maxWindowHeight);
        Height = targetHeight;
        ReleaseNotesScrollViewer.MaxHeight = Math.Max(140, targetHeight - 220);

        UpdateReleaseNotesWidth();
    }

    private void OnReleaseNotesSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateReleaseNotesWidth();
    }

    private void UpdateReleaseNotesWidth()
    {
        var viewportWidth = ReleaseNotesScrollViewer.Viewport.Width;
        if (viewportWidth <= 0)
            return;

        var padding = ReleaseNotesScrollViewer.Padding;
        ReleaseNotesPanel.Width = Math.Max(0, viewportWidth - padding.Left - padding.Right);
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_update.SelectedAsset is null)
            return;

        SetBusy(true);
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgressBar.Value = Math.Clamp(value * 100d, 0d, 100d);
                ProgressTextBlock.Text = T($"正在下载 {value:P0}", $"Downloading {value:P0}");
            });
            await _updateService.PrepareAndLaunchUpdateAsync(_update, _proxy, progress, _downloadCts.Token);
            ProgressTextBlock.Text = T("更新程序已启动，LauncherGo 即将重启；服务器不会停止。", "Updater started. LauncherGo will restart without stopping the server.");
            await Task.Delay(400);
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = T("更新已取消。", "Update cancelled.");
            SetBusy(false);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = T($"更新失败：{ex.Message}", $"Update failed: {ex.Message}");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy && _update.SelectedAsset is not null;
        OpenReleaseButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        DownloadProgressBar.IsVisible = busy;
        ProgressTextBlock.IsVisible = busy;
    }

    private void OnOpenReleaseClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_update.Release.HtmlUrl))
            Process.Start(new ProcessStartInfo { FileName = _update.Release.HtmlUrl, UseShellExecute = true });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void RenderReleaseNotes(string markdown)
    {
        ReleaseNotesPanel.Children.Clear();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();
        var paragraphLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
                return;
            ReleaseNotesPanel.Children.Add(CreateMarkdownTextBlock(string.Join(" ", paragraphLines), 14));
            paragraphLines.Clear();
        }

        void FlushCodeBlock()
        {
            if (codeLines.Count == 0)
                return;
            var codeText = new TextBlock
            {
                Text = string.Join(Environment.NewLine, codeLines),
                TextWrapping = TextWrapping.WrapWithOverflow,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Avalonia.Thickness(10),
                Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128))
            };
            ReleaseNotesPanel.Children.Add(codeText);
            codeLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (inCodeBlock)
                    FlushCodeBlock();
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var heading = System.Text.RegularExpressions.Regex.Match(line, "^(#{1,6})\\s+(.+)$");
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Value.Length;
                ReleaseNotesPanel.Children.Add(CreateMarkdownTextBlock(
                    heading.Groups[2].Value.Trim(), Math.Max(15, 22 - (level * 2)), FontWeight.SemiBold));
                continue;
            }

            var bullet = System.Text.RegularExpressions.Regex.Match(line, "^\\s*[-*+]\\s+(.+)$");
            if (bullet.Success)
            {
                FlushParagraph();
                ReleaseNotesPanel.Children.Add(CreateListItem("•", bullet.Groups[1].Value));
                continue;
            }

            var numbered = System.Text.RegularExpressions.Regex.Match(line, "^\\s*(\\d+)[.)]\\s+(.+)$");
            if (numbered.Success)
            {
                FlushParagraph();
                ReleaseNotesPanel.Children.Add(CreateListItem($"{numbered.Groups[1].Value}.", numbered.Groups[2].Value));
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), "^(---+|\\*\\*\\*+|___+)$"))
            {
                FlushParagraph();
                ReleaseNotesPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Avalonia.Thickness(0, 6),
                    Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128))
                });
                continue;
            }

            paragraphLines.Add(line.Trim());
        }

        if (inCodeBlock)
            FlushCodeBlock();
        FlushParagraph();
    }

    private Control CreateListItem(string marker, string markdown)
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(new TextBlock { Text = marker, FontSize = 14, FontWeight = FontWeight.SemiBold });
        var text = CreateMarkdownTextBlock(markdown, 14);
        Grid.SetColumn(text, 1);
        panel.Children.Add(text);
        return panel;
    }

    private TextBlock CreateMarkdownTextBlock(string markdown, double fontSize, FontWeight? fontWeight = null)
    {
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeight.Regular,
            TextWrapping = TextWrapping.WrapWithOverflow,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        foreach (var inline in ParseMarkdownInlines(markdown))
            textBlock.Inlines!.Add(inline);
        return textBlock;
    }

    private static IEnumerable<Inline> ParseMarkdownInlines(string markdown)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:(?<boldMarker>\*\*|__)(?<boldText>.+?)\k<boldMarker>)|(?<code>`(?<codeText>.+?)`)|(?<link>\[(?<linkText>.+?)\]\((?<linkUrl>[^)]+)\))|(?:(?<italicMarker>\*|_)(?<italicText>.+?)\k<italicMarker>)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var position = 0;
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(markdown))
        {
            if (match.Index > position)
                yield return new Run(markdown[position..match.Index]);

            if (match.Groups["boldText"].Success)
                yield return new Run(match.Groups["boldText"].Value) { FontWeight = FontWeight.Bold };
            else if (match.Groups["codeText"].Success)
                yield return new Run(match.Groups["codeText"].Value) { FontFamily = new FontFamily("Consolas") };
            else if (match.Groups["linkText"].Success)
                yield return new Run(match.Groups["linkText"].Value) { TextDecorations = TextDecorations.Underline };
            else
                yield return new Run(match.Groups["italicText"].Value) { FontStyle = FontStyle.Italic };

            position = match.Index + match.Length;
        }

        if (position < markdown.Length)
            yield return new Run(markdown[position..]);
    }

    private string FormatPackageKind(LauncherPackageKind kind) => kind switch
    {
        LauncherPackageKind.Installer => T("完整安装版", "Full installer"),
        LauncherPackageKind.SmallInstaller => T("精简安装版", "Small installer"),
        LauncherPackageKind.Portable => T("单文件便携版", "Single-file portable"),
        LauncherPackageKind.SmallPackage => T("Small 目录版", "Small package"),
        _ => T("未知安装方式", "Unknown package")
    };

    private string T(string zh, string en) => _isChinese ? zh : en;
}
