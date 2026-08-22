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
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Features;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;
using LauncherGo.Ui.Platform;
using LauncherGo.Ui.Services.I18n;

namespace LauncherGo.Ui.Views;

public partial class FirstLaunchGuideWindow : Window
{
    private static string DefaultWorkspaceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LauncherGo");

    private static string GetServerDirectory(string workspaceDirectory) =>
        Path.Combine(NormalizeDirectoryInput(workspaceDirectory, DefaultWorkspaceDirectory), "servers");

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
    private readonly ILocalizationService _localizationService;
    private readonly DispatcherTimer _blinkTimer;

    private IReadOnlyList<SupportedLanguageOption> _languageOptions => SupportedLanguages.All;

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
    private bool _languageRefreshQueued;

    public FirstLaunchGuideWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>(),
            ServiceLocator.GetRequiredService<ILocalizationService>())
    {
    }

    public FirstLaunchGuideWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService,
        ILocalizationService? localizationService = null)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;
        _localizationService = localizationService ?? new LocalizationService();

        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _localizationService.LanguageChanged += OnLanguageChanged;

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
        Opened += OnWindowOpened;

        Closed += (_, _) =>
        {
            _blinkTimer.Stop();
            _localizationService.LanguageChanged -= OnLanguageChanged;
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
        var workspaceDirectory = NormalizeDirectoryInput(WorkspaceDirectoryTextBox.Text, DefaultWorkspaceDirectory);
        var serverDirectory = GetServerDirectory(workspaceDirectory);
        Directory.CreateDirectory(serverDirectory);
        WorkspaceDirectoryTextBox.Text = workspaceDirectory;

        ToggleDownloadActions(enabled: false);

        try
        {
            await DownloadCatalogEntryAsync(entry, serverDirectory);
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

        var workspaceDirectory = NormalizeDirectoryInput(WorkspaceDirectoryTextBox.Text, DefaultWorkspaceDirectory);
        var serverDirectory = GetServerDirectory(workspaceDirectory);
        WorkspaceDirectoryTextBox.Text = workspaceDirectory;

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
        _localizationService.SetLanguage(code);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        if (_languageRefreshQueued)
        {
            return;
        }

        _languageRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _languageRefreshQueued = false;
            _isChinese = _localizationService.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            ApplyLocalizedTexts();
        }, DispatcherPriority.Render);
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

    private async void OnBrowseWorkspaceDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderAsync(WorkspaceDirectoryTextBox, T("选择工作目录", "Select workspace directory"));
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

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        WindowsDwmWindowEffects.Apply(this);
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

    private async Task DownloadCatalogEntryAsync(ServerDownloadEntry entry, string serverDirectory)
    {
        var entries = new List<ServerDownloadEntry>();
        if (entry.SourceKind == ServerSourceKind.Stratum)
        {
            var baseEntry = FindBaseServerEntry(entry)
                            ?? throw new InvalidOperationException(T(
                                $"未找到 Stratum 基础版本 {entry.BaseVersion} 的游戏服务端下载项。",
                                $"Game server download entry for Stratum base version {entry.BaseVersion} was not found."));
            entries.Add(baseEntry);
        }

        entries.Add(entry);
        foreach (var current in entries)
        {
            var targetPath = Path.Combine(serverDirectory, current.FileName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            var progress = new Progress<double>(value =>
            {
                SetDownloadStatus(T(
                    $"正在下载 {current.Version} {value:P0}",
                    $"Downloading {current.Version} {value:P0}"));
            });

            await _serverPackageService.DownloadByCdnAsync(current.CdnUrl, targetPath, progress);
        }
    }

    private ServerDownloadEntry? FindBaseServerEntry(ServerDownloadEntry stratumEntry)
    {
        return _catalogEntries.FirstOrDefault(entry =>
            entry.SourceKind == ServerSourceKind.Vanilla &&
            entry.Version.Equals(stratumEntry.BaseVersion, StringComparison.OrdinalIgnoreCase));
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
        _localizationService.SetLanguage(languageCode);

        LanguageComboBox.ItemsSource = _languageOptions.Select(option => option.NativeName).ToList();
        LanguageComboBox.SelectedIndex = SupportedLanguages.FindIndex(languageCode);

        AppearanceComboBox.ItemsSource = _themeOptions.Select(option => option.ZhLabel).ToList();
        AppearanceComboBox.SelectedIndex = GetThemeOptionIndex(_preferences.ThemeMode);

        WorkspaceDirectoryTextBox.Text = NormalizeDirectoryInput(_preferences.WorkspaceRoot, DefaultWorkspaceDirectory);

        ApplyTheme(_preferences.ThemeMode);

        _isApplyingUi = false;
    }

    private void ApplyLocalizedTexts()
    {
        _isApplyingUi = true;

        Title = T("LauncherGo 首次引导", "LauncherGo First Launch Guide");

        StepWelcomeText.Text = T("欢迎", "Welcome");
        StepAppearanceText.Text = T("外观", "Appearance");
        StepDirectoryText.Text = T("工作目录", "Workspace");
        StepDownloadText.Text = T("下载", "Download");
        StepCompleteText.Text = T("完成", "Done");

        AppearanceTitleTextBlock.Text = T("选择适合你的外观", "Choose your appearance");
        AppearanceHintTextBlock.Text = T("稍后可以在启动器设置的“外观”页面修改外观设置", "You can change appearance later in launcher settings.");

        DirectoryTitleTextBlock.Text = T("工作目录设置", "Workspace setup");
        WorkspaceDirectoryLabelTextBlock.Text = T("工作目录", "Workspace");
        BrowseWorkspaceDirectoryButton.Content = T("浏览", "Browse");

        DownloadTitleTextBlock.Text = T("下载", "Download");
        DownloadHintTextBlock.Text = T("从版本列表中下载服务端，也可以导入已有服务端压缩包。", "Download from the version list, or import an existing server package.");
        ImportPackageButton.Content = T("导入服务端文件", "Import Server Package");
        RefreshServerSourceOptions();

        CompleteTitleTextBlock.Text = T("完成", "Done");
        CompleteHintTextBlock.Text = T("恭喜你完成了全部初始启动设置，快使用LauncherGo创建服务器吧！", "Congratulations! Initial setup is complete. Start creating your server with LauncherGo.");

        PreviousButtonLabelTextBlock.Text = T("上一步", "Previous");
        NextButtonLabelTextBlock.Text = _currentStep >= 4 ? T("完成", "Finish") : T("下一步", "Next");
        NextArrowIcon.IsVisible = _currentStep < 4;

        LanguageComboBox.ItemsSource = _languageOptions
            .Select(option => option.NativeName)
            .ToList();

        AppearanceComboBox.ItemsSource = _themeOptions
            .Select(option => _isChinese ? option.ZhLabel : option.EnLabel)
            .ToList();

        LanguageComboBox.SelectedIndex = SupportedLanguages.FindIndex(_localizationService.CurrentCulture.Name);
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
        var sourceKind = GetSelectedServerSourceKind();
        foreach (var entry in _catalogEntries)
        {
            if (entry.SourceKind != sourceKind)
            {
                continue;
            }

            _serverVersionItems.Add(new ServerVersionListItem(
                entry,
                entry.Version,
                IsDownloadedInServerDirectory(entry),
                T("已下载", "Downloaded"),
                T("下载", "Download")));
        }
    }

    private void OnServerSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingUi)
        {
            return;
        }

        RebuildCatalogDisplay();
    }

    private ServerSourceKind GetSelectedServerSourceKind() =>
        ServerFeatureFlags.StratumServerSupportEnabled &&
        ServerSourceComboBox.SelectedIndex == 1
            ? ServerSourceKind.Stratum
            : ServerSourceKind.Vanilla;

    private void RefreshServerSourceOptions()
    {
        var selectedSource = GetSelectedServerSourceKind();
        var labels = new List<string> { T("游戏服务端", "Game Server") };
        if (ServerFeatureFlags.StratumServerSupportEnabled)
        {
            labels.Add(T("Stratum 服务端", "Stratum Server"));
        }

        ServerSourceComboBox.Items.Clear();
        foreach (var label in labels)
        {
            ServerSourceComboBox.Items.Add(label);
        }
        ServerSourceComboBox.SelectedIndex = 0;
        if (selectedSource == ServerSourceKind.Stratum && labels.Count > 1)
        {
            ServerSourceComboBox.SelectedIndex = 1;
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
        ServerSourceComboBox.IsEnabled = enabled;
        ImportPackageButton.IsEnabled = enabled;
    }

    private bool IsDownloadedInServerDirectory(ServerDownloadEntry entry)
    {
        var workspaceDirectory = NormalizeDirectoryInput(WorkspaceDirectoryTextBox.Text, DefaultWorkspaceDirectory);
        var serverDirectory = GetServerDirectory(workspaceDirectory);
        if (!File.Exists(Path.Combine(serverDirectory, entry.FileName)))
        {
            return false;
        }

        if (entry.SourceKind != ServerSourceKind.Stratum)
        {
            return true;
        }

        var baseEntry = FindBaseServerEntry(entry);
        return baseEntry is not null && File.Exists(Path.Combine(serverDirectory, baseEntry.FileName));
    }

    private void SaveAndClose()
    {
        var selectedLanguage = LanguageComboBox.SelectedIndex >= 0 &&
                               LanguageComboBox.SelectedIndex < _languageOptions.Count
            ? _languageOptions[LanguageComboBox.SelectedIndex].Code
            : _localizationService.CurrentCulture.Name;
        var selectedTheme = GetSelectedThemeMode();

        var updated = new LauncherPreferences
        {
            IsOnboardingCompleted = true,
            Language = selectedLanguage,
            ThemeMode = selectedTheme,
            WorkspaceRoot = NormalizeDirectoryInput(WorkspaceDirectoryTextBox.Text, DefaultWorkspaceDirectory)
        };

        _preferencesService.Save(updated);
        _preferences = updated;

        _localizationService.SetLanguage(updated.Language);
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
        return _localizationService.Resolve(zh, en);
    }

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
