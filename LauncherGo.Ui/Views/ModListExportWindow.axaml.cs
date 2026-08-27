using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public sealed record ModListExportDialogResult(ModListExportFormat Format, ModListExportOptions Options);

public partial class ModListExportWindow : Window
{
    private readonly bool _isChinese;

    public ModListExportWindow()
        : this(CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
    {
    }

    public ModListExportWindow(bool isChinese)
    {
        _isChinese = isChinese;
        InitializeComponent();

        Title = T("导出模组清单", "Export mod list");
        TitleTextBlock.Text = T("导出模组清单", "Export mod list");
        HintTextBlock.Text = T("选择文件格式和需要导出的表格列。", "Choose a file format and the table columns to include.");
        FormatLabelTextBlock.Text = T("文件格式", "File format");
        ColumnsLabelTextBlock.Text = T("导出列", "Columns");
        NameColumnCheckBox.Content = T("名称", "Name");
        ModIdColumnCheckBox.Content = "Mod ID";
        VersionColumnCheckBox.Content = T("版本", "Version");
        SideColumnCheckBox.Content = T("端", "Side");
        DependenciesColumnCheckBox.Content = T("依赖", "Dependencies");
        IssuesColumnCheckBox.Content = T("问题", "Issues");
        ConfigPathColumnCheckBox.Content = T("配置路径", "Config path");
        FilePathColumnCheckBox.Content = T("文件路径", "File path");
        EnabledColumnCheckBox.Content = T("启用", "Enabled");
        StatusColumnCheckBox.Content = T("状态", "Status");
        WebsiteColumnCheckBox.Content = T("模组网址", "Mod website");
        CancelButton.Content = T("取消", "Cancel");
        ExportButton.Content = T("保存", "Save");
        FormatComboBox.ItemsSource = new[]
        {
            new FormatOption(ModListExportFormat.Csv, "CSV"),
            new FormatOption(ModListExportFormat.Pdf, "PDF"),
            new FormatOption(ModListExportFormat.Txt, "TXT"),
            new FormatOption(ModListExportFormat.Markdown, "Markdown"),
            new FormatOption(ModListExportFormat.Xlsx, "XLSX")
        };
        FormatComboBox.SelectedIndex = 0;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (FormatComboBox.SelectedItem is not FormatOption format)
        {
            ErrorTextBlock.Text = T("请选择文件格式。", "Select a file format.");
            return;
        }

        var columns = ModListExportColumn.None;
        columns |= IsSelected(NameColumnCheckBox) ? ModListExportColumn.Name : ModListExportColumn.None;
        columns |= IsSelected(ModIdColumnCheckBox) ? ModListExportColumn.ModId : ModListExportColumn.None;
        columns |= IsSelected(VersionColumnCheckBox) ? ModListExportColumn.Version : ModListExportColumn.None;
        columns |= IsSelected(SideColumnCheckBox) ? ModListExportColumn.Side : ModListExportColumn.None;
        columns |= IsSelected(DependenciesColumnCheckBox) ? ModListExportColumn.Dependencies : ModListExportColumn.None;
        columns |= IsSelected(IssuesColumnCheckBox) ? ModListExportColumn.Issues : ModListExportColumn.None;
        columns |= IsSelected(ConfigPathColumnCheckBox) ? ModListExportColumn.ConfigPath : ModListExportColumn.None;
        columns |= IsSelected(FilePathColumnCheckBox) ? ModListExportColumn.FilePath : ModListExportColumn.None;
        columns |= IsSelected(EnabledColumnCheckBox) ? ModListExportColumn.Enabled : ModListExportColumn.None;
        columns |= IsSelected(StatusColumnCheckBox) ? ModListExportColumn.Status : ModListExportColumn.None;
        columns |= IsSelected(WebsiteColumnCheckBox) ? ModListExportColumn.Website : ModListExportColumn.None;
        if (columns == ModListExportColumn.None)
        {
            ErrorTextBlock.Text = T("请至少选择一列。", "Select at least one column.");
            return;
        }

        Close(new ModListExportDialogResult(format.Format, new ModListExportOptions { Columns = columns }));
    }

    private static bool IsSelected(CheckBox checkBox) => checkBox.IsChecked == true;

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

    private sealed record FormatOption(ModListExportFormat Format, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
