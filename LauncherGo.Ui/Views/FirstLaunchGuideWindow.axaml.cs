using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class FirstLaunchGuideWindow : Window
{
    private static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LauncherGo");

    private static string DefaultServerDirectory => Path.Combine(DefaultRootDirectory, "servers");
    private static string DefaultProfileDirectory => Path.Combine(DefaultRootDirectory, "profiles");
    private static string DefaultSaveDirectory => Path.Combine(DefaultRootDirectory, "saves");
    private static string DefaultQqBotDirectory => Path.Combine(DefaultRootDirectory, "qqbot");

    private static readonly string[] BlinkTexts =
    [
        "Hello",
        "Wellcome",
        "LauncherGo",
        "Author: HansJack",
        "Team: VSCN"
    ];

    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IServerPackageService _serverPackageService;
    private readonly DispatcherTimer _blinkTimer;

    private readonly List<LanguageOption> _languageOptions =
    [
        new("zh-CN", "中文", "Chinese"),
        new("en-US", "英文", "English")
    ];

    private readonly List<ThemeOption> _themeOptions =
    [
        new(ThemeMode.Light, "亮色主题", "Light Theme"),
        new(ThemeMode.Dark, "暗色主题", "Dark Theme"),
        new(ThemeMode.System, "跟随系统", "Follow System")
    ];

    private readonly ObservableCollection<ServerVersionListItem> _serverVersionItems = [];

    private LauncherPreferences _preferences;
    private List<ServerDownloadEntry> _catalogEntries = [];
    private int _currentStep;
    private int _blinkIndex;
    private bool _blinkVisible = true;
    private bool _isApplyingUi;
    private bool _versionsLoaded;
    private bool _isChinese;

    public FirstLaunchGuideWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>())
    {
    }

    public FirstLaunchGuideWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;

        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        WelcomeBlinkTextBlock.Text = BlinkTexts[0];
        _blinkIndex = 1;
        ServerVersionsListBox.ItemsSource = _serverVersionItems;

        _preferences = _preferencesService.Load();

        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _blinkTimer.Tick += (_, _) => TickBlinkText();
        _blinkTimer.Start();

        LoadPreferencesToUi();
        ApplyLocalizedTexts();
        UpdateStepUi();

        Closed += (_, _) =>
        {
            _blinkTimer.Stop();
        };
    }

    private async void OnDownloadVersionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerVersionListItem item })
        {
            return;
        }

        await DownloadServerVersionAsync(item.Entry);
    }

    private async Task DownloadServerVersionAsync(ServerDownloadEntry entry)
    {
        var serverDirectory = NormalizeDirectoryInput(ServerDirectoryTextBox.Text, DefaultServerDirectory);
        Directory.CreateDirectory(serverDirectory);
        ServerDirectoryTextBox.Text = serverDirectory;

        var targetPath = Path.Combine(serverDirectory, entry.FileName);
        ToggleDownloadActions(enabled: false);

        try
        {
            var progress = new Progress<double>(value =>
            {
                SetDownloadStatus(T(
                    $"正在下载 {entry.Version} {value:P0}",
                    $"Downloading {entry.Version} {value:P0}"));
            });

            await _serverPackageService.DownloadByCdnAsync(entry.CdnUrl, targetPath, progress);
            SetDownloadStatus(T($"下载完成：{entry.Version}", $"Download completed: {entry.Version}"));
            await EnsureCatalogLoadedAsync(forceReload: true);
            RebuildCatalogDisplay();
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"下载失败：{ex.Message}", $"Download failed: {ex.Message}"));
        }
        finally
        {
            ToggleDownloadActions(enabled: true);
        }
    }

    private async void OnImportPackageClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择服务端压缩包", "Select server package"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var selected = files.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        var sourcePath = TryGetLocalPath(selected);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            SetDownloadStatus(T("无法读取所选文件路径。", "Cannot read selected file path."));
            return;
        }

        var serverDirectory = NormalizeDirectoryInput(ServerDirectoryTextBox.Text, DefaultServerDirectory);
        ServerDirectoryTextBox.Text = serverDirectory;

        try
        {
            var importedPath = await _serverPackageService.ImportServerPackageAsync(sourcePath, serverDirectory);
            SetDownloadStatus(T($"导入完成：{Path.GetFileName(importedPath)}", $"Imported: {Path.GetFileName(importedPath)}"));
            await EnsureCatalogLoadedAsync(forceReload: true);
            RebuildCatalogDisplay();
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingUi)
        {
            return;
        }

        var index = LanguageComboBox.SelectedIndex;
        if (index < 0 || index >= _languageOptions.Count)
        {
            return;
        }

        var code = _languageOptions[index].Code;
        _isChinese = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        ApplyCulture(code);
        ApplyLocalizedTexts();
    }

    private void OnAppearanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingUi)
        {
            return;
        }

        var mode = GetSelectedThemeMode();
        ApplyTheme(mode);
        UpdateStepUi();
    }

    private async void OnBrowseServerDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync(ServerDirectoryTextBox, T("选择服务端目录", "Select server directory"));
    }

    private async void OnBrowseProfileDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync(ProfileDirectoryTextBox, T("选择档案目录", "Select profile directory"));
    }

    private async void OnBrowseSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync(SaveDirectoryTextBox, T("选择存档目录", "Select save directory"));
    }

    private async void OnBrowseQqBotDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync(QqBotDirectoryTextBox, T("选择QQ机器人目录", "Select QQ bot directory"));
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep <= 0)
        {
            return;
        }

        _currentStep--;
        UpdateStepUi();
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep >= 4)
        {
            SaveAndClose();
            return;
        }

        _currentStep++;
        UpdateStepUi();

        if (_currentStep == 3)
        {
            await EnsureCatalogLoadedAsync();
            RebuildCatalogDisplay();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        DeactivateInputControlsOnBackgroundClick(e.Source);

        if (ShouldSkipWindowDrag(e.Source))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void DeactivateInputControlsOnBackgroundClick(object? source)
    {
        if (ShouldKeepInputFocus(source))
        {
            return;
        }

        var closedDropDown = CloseOpenComboBoxDropDowns();
        var focusedElement = FocusManager?.GetFocusedElement();
        if (focusedElement is TextBox or ComboBox or ComboBoxItem || closedDropDown)
        {
            FocusManager?.Focus(null!, NavigationMethod.Pointer, KeyModifiers.None);
        }
    }

    private bool CloseOpenComboBoxDropDowns()
    {
        var closedAny = false;
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
        {
            if (!comboBox.IsDropDownOpen)
            {
                continue;
            }

            comboBox.IsDropDownOpen = false;
            closedAny = true;
        }

        return closedAny;
    }

    private static bool ShouldKeepInputFocus(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is TextBox
                or ComboBox
                or ComboBoxItem
                or NumericUpDown)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private async Task BrowseFolderAsync(TextBox targetTextBox, string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        var selected = result.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        var path = TryGetLocalPath(selected);
        if (!string.IsNullOrWhiteSpace(path))
        {
            targetTextBox.Text = path;
        }
    }

    private static bool ShouldSkipWindowDrag(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is Button
                or TextBox
                or ComboBox
                or ComboBoxItem
                or ListBox
                or ListBoxItem
                or ScrollBar
                or Thumb)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private void LoadPreferencesToUi()
    {
        _isApplyingUi = true;

        var languageCode = string.IsNullOrWhiteSpace(_preferences.Language)
            ? CultureInfo.CurrentUICulture.Name
            : _preferences.Language.Trim();

        _isChinese = languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        ApplyCulture(languageCode);

        LanguageComboBox.ItemsSource = _languageOptions.Select(option => option.ZhLabel).ToList();
        LanguageComboBox.SelectedIndex = _isChinese ? 0 : 1;

        AppearanceComboBox.ItemsSource = _themeOptions.Select(option => option.ZhLabel).ToList();
        AppearanceComboBox.SelectedIndex = GetThemeOptionIndex(_preferences.ThemeMode);

        ServerDirectoryTextBox.Text = NormalizeDirectoryInput(_preferences.ServerDirectory, DefaultServerDirectory);
        ProfileDirectoryTextBox.Text = NormalizeDirectoryInput(_preferences.ProfileDirectory, DefaultProfileDirectory);
        SaveDirectoryTextBox.Text = NormalizeDirectoryInput(_preferences.SaveDirectory, DefaultSaveDirectory);
        QqBotDirectoryTextBox.Text = NormalizeDirectoryInput(_preferences.QqBotDirectory, DefaultQqBotDirectory);

        ApplyTheme(_preferences.ThemeMode);

        _isApplyingUi = false;
    }

    private void ApplyLocalizedTexts()
    {
        _isApplyingUi = true;

        Title = T("LauncherGo 首次引导", "LauncherGo First Launch Guide");

        StepWelcomeText.Text = T("欢迎", "Welcome");
        StepAppearanceText.Text = T("外观", "Appearance");
        StepDirectoryText.Text = T("全局目录设置", "Global Directory");
        StepDownloadText.Text = T("下载", "Download");
        StepCompleteText.Text = T("完成", "Done");

        AppearanceTitleTextBlock.Text = T("选择适合你的外观", "Choose your appearance");
        AppearanceHintTextBlock.Text = T("稍后可以在启动器设置的“外观”页面修改外观设置", "You can change appearance later in launcher settings.");

        DirectoryTitleTextBlock.Text = T("全局目录设置", "Global directory setup");
        ServerDirectoryLabelTextBlock.Text = T("服务端目录", "Server directory");
        ProfileDirectoryLabelTextBlock.Text = T("档案目录", "Profile directory");
        SaveDirectoryLabelTextBlock.Text = T("存档目录", "Save directory");
        QqBotDirectoryLabelTextBlock.Text = T("QQ机器人目录", "QQ bot directory");
        BrowseServerDirectoryButton.Content = T("浏览", "Browse");
        BrowseProfileDirectoryButton.Content = T("浏览", "Browse");
        BrowseSaveDirectoryButton.Content = T("浏览", "Browse");
        BrowseQqBotDirectoryButton.Content = T("浏览", "Browse");

        DownloadTitleTextBlock.Text = T("下载", "Download");
        DownloadHintTextBlock.Text = T("从版本列表中下载服务端，也可以导入已有服务端压缩包。", "Download from the version list, or import an existing server package.");
        ImportPackageButton.Content = T("导入服务端文件", "Import Server Package");

        CompleteTitleTextBlock.Text = T("完成", "Done");
        CompleteHintTextBlock.Text = T("恭喜你完成了全部初始启动设置，快使用LauncherGo创建服务器吧！", "Congratulations! Initial setup is complete. Start creating your server with LauncherGo.");

        PreviousButtonLabelTextBlock.Text = T("上一步", "Previous");
        NextButtonLabelTextBlock.Text = _currentStep >= 4 ? T("完成", "Finish") : T("下一步", "Next");
        NextArrowIcon.IsVisible = _currentStep < 4;

        LanguageComboBox.ItemsSource = _languageOptions
            .Select(option => _isChinese ? option.ZhLabel : option.EnLabel)
            .ToList();

        AppearanceComboBox.ItemsSource = _themeOptions
            .Select(option => _isChinese ? option.ZhLabel : option.EnLabel)
            .ToList();

        LanguageComboBox.SelectedIndex = _isChinese ? 0 : 1;
        AppearanceComboBox.SelectedIndex = GetThemeOptionIndex(GetSelectedThemeMode());

        RebuildCatalogDisplay();

        _isApplyingUi = false;
    }

    private void UpdateStepUi()
    {
        WelcomePanel.IsVisible = _currentStep == 0;
        AppearancePanel.IsVisible = _currentStep == 1;
        DirectoryPanel.IsVisible = _currentStep == 2;
        DownloadPanel.IsVisible = _currentStep == 3;
        CompletePanel.IsVisible = _currentStep == 4;

        SetStepActive(StepWelcomeBorder, _currentStep == 0);
        SetStepActive(StepAppearanceBorder, _currentStep == 1);
        SetStepActive(StepDirectoryBorder, _currentStep == 2);
        SetStepActive(StepDownloadBorder, _currentStep == 3);
        SetStepActive(StepCompleteBorder, _currentStep == 4);

        PreviousButton.IsEnabled = _currentStep > 0;
        NextButtonLabelTextBlock.Text = _currentStep >= 4 ? T("完成", "Finish") : T("下一步", "Next");
        NextArrowIcon.IsVisible = _currentStep < 4;
    }

    private void TickBlinkText()
    {
        if (!WelcomePanel.IsVisible)
        {
            return;
        }

        if (_blinkVisible)
        {
            WelcomeBlinkTextBlock.Opacity = 0;
            _blinkVisible = false;
            return;
        }

        WelcomeBlinkTextBlock.Opacity = 1;
        WelcomeBlinkTextBlock.Text = BlinkTexts[_blinkIndex];
        _blinkIndex = (_blinkIndex + 1) % BlinkTexts.Length;
        _blinkVisible = true;
    }

    private void SetStepActive(Border border, bool active)
    {
        var textBlock = border.Child as TextBlock;
        var textBrush = Resources.TryGetResource("StepTextBrush", ActualThemeVariant, out var stepTextBrushObj) && stepTextBrushObj is IBrush stepTextBrush
            ? stepTextBrush
            : new SolidColorBrush(Color.Parse("#101010"));
        var underlineBrush = Resources.TryGetResource("StepUnderlineBrush", ActualThemeVariant, out var underlineBrushObj) && underlineBrushObj is IBrush stepUnderlineBrush
            ? stepUnderlineBrush
            : textBrush;

        if (active)
        {
            border.BorderBrush = underlineBrush;
            border.BorderThickness = new Thickness(0, 0, 0, 2);
            if (textBlock is not null)
            {
                textBlock.Foreground = textBrush;
                textBlock.FontWeight = FontWeight.SemiBold;
            }

            return;
        }

        border.BorderBrush = Brushes.Transparent;
        border.BorderThickness = new Thickness(0, 0, 0, 2);
        if (textBlock is not null)
        {
            textBlock.Foreground = textBrush;
            textBlock.FontWeight = FontWeight.Regular;
        }
    }

    private async Task EnsureCatalogLoadedAsync(bool forceReload = false)
    {
        if (_versionsLoaded && !forceReload)
        {
            return;
        }

        SetDownloadStatus(T("正在加载服务端版本列表...", "Loading server versions..."));

        try
        {
            _catalogEntries = (await _serverPackageService.GetServerDownloadEntriesAsync()).ToList();
            _versionsLoaded = true;

            RebuildCatalogDisplay();
            SetDownloadStatus(T($"已加载 {_catalogEntries.Count} 条版本记录。", $"Loaded {_catalogEntries.Count} version entries."));
        }
        catch (Exception ex)
        {
            _versionsLoaded = false;
            _catalogEntries = [];
            RebuildCatalogDisplay();
            SetDownloadStatus(T($"加载失败：{ex.Message}", $"Load failed: {ex.Message}"));
        }
    }

    private void RebuildCatalogDisplay()
    {
        _serverVersionItems.Clear();
        foreach (var entry in _catalogEntries)
        {
            _serverVersionItems.Add(new ServerVersionListItem(
                entry,
                entry.Version,
                IsDownloadedInServerDirectory(entry.FileName),
                T("已下载", "Downloaded"),
                T("下载", "Download")));
        }
    }

    private void SetDownloadStatus(string message)
    {
        DownloadStatusTextBlock.Text = message;
    }

    private ThemeMode GetSelectedThemeMode()
    {
        var index = AppearanceComboBox.SelectedIndex;
        if (index < 0 || index >= _themeOptions.Count)
        {
            return ThemeMode.System;
        }

        return _themeOptions[index].Mode;
    }

    private int GetThemeOptionIndex(ThemeMode mode)
    {
        var index = _themeOptions.FindIndex(option => option.Mode == mode);
        return index >= 0 ? index : _themeOptions.FindIndex(option => option.Mode == ThemeMode.System);
    }

    private void ToggleDownloadActions(bool enabled)
    {
        ServerVersionsListBox.IsEnabled = enabled;
        ImportPackageButton.IsEnabled = enabled;
    }

    private bool IsDownloadedInServerDirectory(string fileName)
    {
        var serverDirectory = NormalizeDirectoryInput(ServerDirectoryTextBox.Text, DefaultServerDirectory);
        var target = Path.Combine(serverDirectory, fileName);
        return File.Exists(target);
    }

    private void SaveAndClose()
    {
        var selectedLanguage = _isChinese ? "zh-CN" : "en-US";
        var selectedTheme = GetSelectedThemeMode();

        var updated = new LauncherPreferences
        {
            IsOnboardingCompleted = true,
            Language = selectedLanguage,
            ThemeMode = selectedTheme,
            ServerDirectory = NormalizeDirectoryInput(ServerDirectoryTextBox.Text, DefaultServerDirectory),
            ProfileDirectory = NormalizeDirectoryInput(ProfileDirectoryTextBox.Text, DefaultProfileDirectory),
            SaveDirectory = NormalizeDirectoryInput(SaveDirectoryTextBox.Text, DefaultSaveDirectory),
            QqBotDirectory = NormalizeDirectoryInput(QqBotDirectoryTextBox.Text, DefaultQqBotDirectory)
        };

        _preferencesService.Save(updated);
        _preferences = updated;

        ApplyCulture(updated.Language);
        ApplyTheme(updated.ThemeMode);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = ServiceLocator.GetRequiredService<LauncherMainWindow>();
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            Close();
            return;
        }

        Close();
    }

    private void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    private static void ApplyCulture(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
            // ignore invalid culture code
        }
    }

    private static string NormalizeDirectoryInput(string? input, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(input))
        {
            try
            {
                return Path.GetFullPath(input.Trim());
            }
            catch
            {
                // ignore
            }
        }

        return Path.GetFullPath(fallback);
    }

    private static string? TryGetLocalPath(IStorageItem storageItem)
    {
        try
        {
            if (storageItem.Path.IsAbsoluteUri)
            {
                return storageItem.Path.LocalPath;
            }

            return storageItem.Path.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string T(string zh, string en)
    {
        return _isChinese ? zh : en;
    }

    private sealed record LanguageOption(string Code, string ZhLabel, string EnLabel);

    private sealed record ThemeOption(ThemeMode Mode, string ZhLabel, string EnLabel);

    public sealed class ServerVersionListItem
    {
        public ServerVersionListItem(
            ServerDownloadEntry entry,
            string displayText,
            bool isDownloaded,
            string downloadedText,
            string actionText)
        {
            Entry = entry;
            DisplayText = displayText;
            IsDownloaded = isDownloaded;
            DownloadedText = downloadedText;
            ActionText = actionText;
        }

        public ServerDownloadEntry Entry { get; }

        public string DisplayText { get; }

        public bool IsDownloaded { get; }

        public bool CanDownload => !IsDownloaded;

        public string DownloadedText { get; }

        public string ActionText { get; }
    }
}
