using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModListExportServiceTests
{
    private readonly ModListExportService _service = new();

    [Theory]
    [InlineData(ModListExportFormat.Csv, "csv")]
    [InlineData(ModListExportFormat.Pdf, "pdf")]
    [InlineData(ModListExportFormat.Txt, "txt")]
    [InlineData(ModListExportFormat.Markdown, "md")]
    [InlineData(ModListExportFormat.Xlsx, "xlsx")]
    public void GetFileExtension_ReturnsExpectedExtension(ModListExportFormat format, string extension)
    {
        Assert.Equal(extension, _service.GetFileExtension(format));
    }

    [Theory]
    [InlineData(ModListExportFormat.Csv)]
    [InlineData(ModListExportFormat.Txt)]
    [InlineData(ModListExportFormat.Markdown)]
    public async Task TextExports_IncludeWebsiteAndAllModFields(ModListExportFormat format)
    {
        await using var output = new MemoryStream();

        await _service.ExportAsync(CreateProfile(), [CreateMod()], format, output, options: AllColumnsWithoutWebsiteLookup());

        var content = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Simple Voice Chat", content);
        Assert.Contains("中文 Simple Voice Chat", content);
        Assert.Contains("simplevoicechat", content);
        Assert.Contains("https://mods.vintagestory.at/simplevoicechat", content);
        Assert.Contains("C:\\mods\\simplevoicechat.zip", content);
        Assert.Contains("exampledependency@1.2.0", content);
    }

    [Fact]
    public async Task XlsxExport_CreatesReadableWorksheet()
    {
        await using var output = new MemoryStream();

        await _service.ExportAsync(CreateProfile(), [CreateMod()], ModListExportFormat.Xlsx, output, options: AllColumnsWithoutWebsiteLookup());

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(worksheet);
        using var reader = new StreamReader(worksheet!.Open(), Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        Assert.Contains("Simple Voice Chat", content);
        Assert.Contains("https://mods.vintagestory.at/simplevoicechat", content);
    }

    [Fact]
    public async Task PdfExport_CreatesPdfDocument()
    {
        await using var output = new MemoryStream();

        await _service.ExportAsync(CreateProfile(), [CreateMod()], ModListExportFormat.Pdf, output, options: DefaultColumnsWithoutWebsiteLookup());

        var content = output.ToArray();
        Assert.True(content.Length > 100);
        Assert.Equal("%PDF-1.4", Encoding.ASCII.GetString(content, 0, 8));
        Assert.Contains(Encoding.ASCII.GetBytes("%%EOF"), content);
    }

    [Fact]
    public async Task DefaultColumns_ExcludeLocalConfigAndModFilePaths()
    {
        await using var output = new MemoryStream();

        await _service.ExportAsync(CreateProfile(), [CreateMod()], ModListExportFormat.Csv, output, options: DefaultColumnsWithoutWebsiteLookup());

        var content = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("Config Path", content);
        Assert.DoesNotContain("File Path", content);
        Assert.DoesNotContain("C:\\config\\simplevoicechat.json", content);
        Assert.DoesNotContain("C:\\mods\\simplevoicechat.zip", content);
    }

    [Theory]
    [InlineData("{\"statuscode\":200,\"mod\":{\"urlalias\":\"simplevoicechat\",\"modid\":12}}", "https://mods.vintagestory.at/simplevoicechat")]
    [InlineData("{\"statuscode\":200,\"mod\":{\"modid\":12}}", "https://mods.vintagestory.at/show/mod/12")]
    [InlineData("{\"statuscode\":404}", "https://mods.vintagestory.at/simplevoicechat")]
    public void OfficialWebsiteResolution_PrefersAliasThenNumericIdThenFallback(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        var actual = ModListExportService.ResolveWebsiteUrlFromOfficialResponse(
            document.RootElement,
            "https://mods.vintagestory.at/simplevoicechat");

        Assert.Equal(expected, actual);
    }

    private static InstanceProfile CreateProfile() => new() { Id = "test", Name = "Test Server" };

    private static ModEntry CreateMod() => new()
    {
        Name = "中文 Simple Voice Chat",
        ModId = "simplevoicechat",
        Version = "1.0.0",
        Side = "Server",
        FilePath = "C:\\mods\\simplevoicechat.zip",
        ConfigPath = "C:\\config\\simplevoicechat.json",
        Status = "Valid",
        IsDisabled = false,
        Dependencies = [new ModDependency { ModId = "exampledependency", Version = "1.2.0" }],
        DependencyIssues = ["Missing optional dependency"]
    };

    private static ModListExportOptions AllColumnsWithoutWebsiteLookup() => new()
    {
        Columns = ModListExportColumn.All,
        ResolveWebsiteUrls = false
    };

    private static ModListExportOptions DefaultColumnsWithoutWebsiteLookup() => new()
    {
        Columns = ModListExportColumn.Default,
        ResolveWebsiteUrls = false
    };
}
