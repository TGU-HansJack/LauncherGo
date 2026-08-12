using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;

namespace LauncherGo.Ui.Views;

public sealed record LauncherExitConfirmationResult(bool CloseToTrayOnExit, bool ExitApplication);

public partial class LauncherExitConfirmationWindow : Window
{
    public LauncherExitConfirmationWindow()
        : this(CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase), false)
    {
    }

    public LauncherExitConfirmationWindow(bool isChinese, bool closeToTrayOnExit)
    {
        InitializeComponent();

        Title = isChinese ? "退出 LauncherGo" : "Exit LauncherGo";
        TitleTextBlock.Text = isChinese ? "要退出 LauncherGo 吗？" : "Exit LauncherGo?";
        MessageTextBlock.Text = isChinese
            ? "退出启动器不会停止正在运行的服务器。"
            : "Exiting the launcher will not stop running servers.";
        CloseToTrayLabelTextBlock.Text = isChinese
            ? "关闭时隐藏到托盘，不直接退出"
            : "Hide to the tray when closing";
        CloseToTrayHintTextBlock.Text = isChinese
            ? "启用后，之后关闭主窗口会保留启动器在后台运行。"
            : "When enabled, future window closes keep the launcher running in the background.";
        CancelButton.Content = isChinese ? "取消" : "Cancel";
        HideToTrayButton.Content = isChinese ? "隐藏到托盘" : "Hide to tray";
        ExitButton.Content = isChinese ? "退出" : "Exit";
        CloseToTrayToggleSwitch.IsChecked = closeToTrayOnExit;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHideToTrayClick(object? sender, RoutedEventArgs e) => Complete(exitApplication: false);

    private void OnExitClick(object? sender, RoutedEventArgs e) => Complete(exitApplication: true);

    private void Complete(bool exitApplication) => Close(new LauncherExitConfirmationResult(
        CloseToTrayToggleSwitch.IsChecked == true,
        exitApplication));
}
