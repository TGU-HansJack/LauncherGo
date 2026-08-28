using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Fields;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace LauncherGo.Services;

/// <summary>
///     Writes mod data to common portable formats without requiring Office to be installed.
/// </summary>
public sealed class ModListExportService : IModListExportService
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly Lazy<bool> PdfSharpConfiguration = new(ConfigurePdfSharp);
    private static readonly HttpClient WebsiteHttpClient = CreateWebsiteHttpClient();
    private static readonly ConcurrentDictionary<string, string> WebsiteUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyList<ExportColumn> ExportColumns =
    [
        new(ModListExportColumn.Name, "Name", static row => row.Name),
        new(ModListExportColumn.ModId, "Mod ID", static row => row.ModId),
        new(ModListExportColumn.Version, "Version", static row => row.Version),
        new(ModListExportColumn.Side, "Side", static row => row.Side),
        new(ModListExportColumn.Dependencies, "Dependencies", static row => row.Dependencies),
        new(ModListExportColumn.Issues, "Issues", static row => row.Issues),
        new(ModListExportColumn.ConfigPath, "Config Path", static row => row.ConfigPath),
        new(ModListExportColumn.FilePath, "File Path", static row => row.FilePath),
        new(ModListExportColumn.Enabled, "Enabled", static row => row.Enabled),
        new(ModListExportColumn.Status, "Status", static row => row.Status),
        new(ModListExportColumn.Website, "Website", static row => row.Website)
    ];

    public async Task ExportAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        ModListExportFormat format,
        Stream destination,
        CancellationToken cancellationToken = default,
        ModListExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(destination);

        options ??= new ModListExportOptions();
        var columns = ExportColumns.Where(column => options.Columns.HasFlag(column.Column)).ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException("Select at least one mod export column.");

        var websiteUrls = options.ResolveWebsiteUrls && columns.Any(column => column.Column == ModListExportColumn.Website)
            ? await ResolveWebsiteUrlsAsync(mods, cancellationToken)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = CreateRows(mods, websiteUrls);
        switch (format)
        {
            case ModListExportFormat.Csv:
                await WriteUtf8Async(destination, BuildCsv(profile, rows, columns), includeBom: true, cancellationToken);
                break;
            case ModListExportFormat.Txt:
                await WriteUtf8Async(destination, BuildText(profile, rows, columns), includeBom: false, cancellationToken);
                break;
            case ModListExportFormat.Markdown:
                await WriteUtf8Async(destination, BuildMarkdown(profile, rows, columns), includeBom: false, cancellationToken);
                break;
            case ModListExportFormat.Xlsx:
                await WriteXlsxAsync(destination, profile, rows, columns, cancellationToken);
                break;
            case ModListExportFormat.Pdf:
                await WritePdfAsync(destination, profile, rows, columns, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.");
        }
    }

    public string GetFileExtension(ModListExportFormat format)
    {
        return format switch
        {
            ModListExportFormat.Csv => "csv",
            ModListExportFormat.Pdf => "pdf",
            ModListExportFormat.Txt => "txt",
            ModListExportFormat.Markdown => "md",
            ModListExportFormat.Xlsx => "xlsx",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.")
        };
    }

    private static IReadOnlyList<ExportRow> CreateRows(
        IEnumerable<ModEntry> mods,
        IReadOnlyDictionary<string, string> websiteUrls)
    {
        return mods
            .OrderBy(static mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new ExportRow(
                mod.Name,
                mod.ModId,
                mod.Version,
                mod.Side,
                mod.DependenciesText,
                mod.IssuesText,
                mod.ConfigPath,
                mod.FilePath,
                mod.IsDisabled ? "No" : "Yes",
                mod.Status,
                websiteUrls.TryGetValue(mod.ModId, out var websiteUrl)
                    ? websiteUrl
                    : BuildFallbackWebsiteUrl(mod.ModId)))
            .ToList();
    }

    private static async Task<Dictionary<string, string>> ResolveWebsiteUrlsAsync(
        IEnumerable<ModEntry> mods,
        CancellationToken cancellationToken)
    {
        var modIds = mods
            .Select(static mod => mod.ModId?.Trim() ?? string.Empty)
            .Where(static modId => !string.IsNullOrWhiteSpace(modId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var concurrency = new SemaphoreSlim(4);
        var tasks = modIds.Select(async modId =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var url = await ResolveWebsiteUrlAsync(modId, cancellationToken);
                result[modId] = url;
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks);
        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> ResolveWebsiteUrlAsync(string modId, CancellationToken cancellationToken)
    {
        if (WebsiteUrlCache.TryGetValue(modId, out var cached))
            return cached;

        var fallback = BuildFallbackWebsiteUrl(modId);
        try
        {
            var requestUrl = $"https://mods.vintagestory.at/api/mod/{Uri.EscapeDataString(modId)}";
            using var response = await WebsiteHttpClient.GetAsync(requestUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return WebsiteUrlCache.GetOrAdd(modId, fallback);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var resolved = ResolveWebsiteUrlFromOfficialResponse(document.RootElement, fallback);
            return WebsiteUrlCache.GetOrAdd(modId, resolved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return WebsiteUrlCache.GetOrAdd(modId, fallback);
        }
    }

    internal static string ResolveWebsiteUrlFromOfficialResponse(JsonElement payload, string fallback)
    {
        if (!IsOfficialSuccess(payload) || !payload.TryGetProperty("mod", out var mod) ||
            mod.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        var alias = GetJsonString(mod, "urlalias").Trim().Trim('/');
        if (!string.IsNullOrWhiteSpace(alias))
            return $"https://mods.vintagestory.at/{Uri.EscapeDataString(alias)}";

        var numericId = 0;
        if (mod.TryGetProperty("modid", out var modId) &&
            ((modId.ValueKind == JsonValueKind.Number && modId.TryGetInt32(out numericId)) ||
             (modId.ValueKind == JsonValueKind.String && int.TryParse(modId.GetString(), out numericId))) &&
            numericId > 0)
        {
            return $"https://mods.vintagestory.at/show/mod/{numericId}";
        }

        return fallback;
    }

    private static bool IsOfficialSuccess(JsonElement payload)
    {
        if (!payload.TryGetProperty("statuscode", out var status))
            return false;
        return (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var numeric) && numeric == 200) ||
               (status.ValueKind == JsonValueKind.String && status.GetString()?.Trim() == "200");
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string BuildFallbackWebsiteUrl(string modId)
    {
        return string.IsNullOrWhiteSpace(modId)
            ? string.Empty
            : $"https://mods.vintagestory.at/{Uri.EscapeDataString(modId.Trim())}";
    }

    private static string BuildText(
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vintage Story Mod List");
        builder.AppendLine($"Profile: {profile.Name}");
        builder.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Mods: {rows.Count}");
        builder.AppendLine();

        foreach (var row in rows)
        {
            foreach (var column in columns)
                builder.AppendLine($"{column.Header}: {column.Value(row)}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vintage Story Mod List");
        builder.AppendLine();
        builder.AppendLine($"- Profile: {EscapeMarkdown(profile.Name)}");
        builder.AppendLine($"- Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- Mods: {rows.Count}");
        builder.AppendLine();
        builder.AppendLine("| " + string.Join(" | ", columns.Select(static column => column.Header)) + " |");
        builder.AppendLine("| " + string.Join(" | ", columns.Select(static _ => "---")) + " |");
        foreach (var row in rows)
        {
            builder.Append("| ").Append(string.Join(" | ", columns.Select(column => EscapeMarkdown(column.Value(row))))).AppendLine(" |");
        }

        return builder.ToString();
    }

    private static string BuildCsv(
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Profile,{EscapeCsv(profile.Name)}");
        builder.AppendLine($"Exported,{EscapeCsv(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))}");
        builder.AppendLine(string.Join(',', columns.Select(column => EscapeCsv(column.Header))));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', columns.Select(column => EscapeCsv(column.Value(row)))));
        }

        return builder.ToString();
    }

    private static async Task WriteUtf8Async(
        Stream destination,
        string content,
        bool includeBom,
        CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(includeBom);
        var data = encoding.GetBytes(content);
        await destination.WriteAsync(data, cancellationToken);
    }

    private static async Task WriteXlsxAsync(
        Stream destination,
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await WriteArchiveTextAsync(archive, "[Content_Types].xml", BuildContentTypesXml(), cancellationToken);
        await WriteArchiveTextAsync(archive, "_rels/.rels", BuildRootRelationshipsXml(), cancellationToken);
        await WriteArchiveTextAsync(archive, "xl/workbook.xml", BuildWorkbookXml(), cancellationToken);
        await WriteArchiveTextAsync(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml(), cancellationToken);
        await WriteArchiveTextAsync(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(profile, rows, columns), cancellationToken);
    }

    private static async Task WriteArchiveTextAsync(ZipArchive archive, string name, string value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await WriteUtf8Async(stream, value, includeBom: false, cancellationToken);
    }

    private static string BuildContentTypesXml()
    {
        XNamespace ns = ContentTypeNamespace;
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "Types",
                new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ns + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ns + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildRootRelationshipsXml()
    {
        XNamespace ns = PackageRelationshipNamespace;
        return new XDocument(new XElement(ns + "Relationships",
            new XElement(ns + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "xl/workbook.xml")))).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorkbookXml()
    {
        XNamespace ns = SpreadsheetNamespace;
        XNamespace rel = RelationshipNamespace;
        return new XDocument(new XElement(ns + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", rel),
            new XElement(ns + "sheets", new XElement(ns + "sheet", new XAttribute("name", "Mods"), new XAttribute("sheetId", "1"), new XAttribute(rel + "id", "rId1"))))).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorkbookRelationshipsXml()
    {
        XNamespace ns = PackageRelationshipNamespace;
        return new XDocument(new XElement(ns + "Relationships",
            new XElement(ns + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml")))).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorksheetXml(
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns)
    {
        XNamespace ns = SpreadsheetNamespace;
        var sheetRows = new List<XElement>
        {
            CreateSpreadsheetRow(ns, 1, ["Vintage Story Mod List"]),
            CreateSpreadsheetRow(ns, 2, ["Profile", profile.Name]),
            CreateSpreadsheetRow(ns, 3, ["Exported", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")]),
            CreateSpreadsheetRow(ns, 4, columns.Select(static column => column.Header))
        };
        for (var index = 0; index < rows.Count; index++)
        {
            sheetRows.Add(CreateSpreadsheetRow(ns, index + 5, columns.Select(column => column.Value(rows[index]))));
        }

        return new XDocument(new XElement(ns + "worksheet",
            new XElement(ns + "sheetData", sheetRows))).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement CreateSpreadsheetRow(XNamespace ns, int rowIndex, IEnumerable<string> values)
    {
        return new XElement(ns + "row",
            new XAttribute("r", rowIndex),
            values.Select(value => new XElement(ns + "c", new XAttribute("t", "inlineStr"),
                new XElement(ns + "is", new XElement(ns + "t", value ?? string.Empty)))));
    }

    private static async Task WritePdfAsync(
        Stream destination,
        InstanceProfile profile,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = PdfSharpConfiguration.Value;

        var document = new Document();
        document.Info.Title = "Vintage Story Mod List";
        var normalStyle = document.Styles[StyleNames.Normal]
            ?? throw new InvalidOperationException("The PDF document has no normal text style.");
        normalStyle.Font.Name = WindowsPdfFontResolver.FamilyName;
        normalStyle.Font.Size = Unit.FromPoint(8);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromPoint(52);
        section.PageSetup.RightMargin = Unit.FromPoint(28);
        section.PageSetup.BottomMargin = Unit.FromPoint(28);
        section.PageSetup.LeftMargin = Unit.FromPoint(28);
        section.PageSetup.HeaderDistance = Unit.FromPoint(12);
        section.PageSetup.FooterDistance = Unit.FromPoint(12);

        var header = section.Headers.Primary.AddParagraph("Vintage Story Mod List");
        header.Format.Font.Size = Unit.FromPoint(16);
        header.Format.Font.Bold = true;

        section.AddParagraph($"Profile: {profile.Name}");
        section.AddParagraph($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        section.AddParagraph($"Mods: {rows.Count}");

        foreach (var row in rows)
        {
            foreach (var (exportColumn, index) in columns.Select((exportColumn, index) => (exportColumn, index)))
            {
                var paragraph = section.AddParagraph($"{exportColumn.Header}: {exportColumn.Value(row)}");
                paragraph.Format.SpaceBefore = Unit.FromPoint(index == 0 ? 7 : 3);
                paragraph.Format.KeepWithNext = index == 0 && columns.Count > 1;
                paragraph.Format.Font.Bold = index == 0;
            }
        }

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("Page ");
        footer.Add(new PageField());

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(destination, closeStream: false);

        await destination.FlushAsync(cancellationToken);
    }

    private static bool ConfigurePdfSharp()
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        GlobalFontSettings.FontResolver = new WindowsPdfFontResolver();
        return true;
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static string EscapeMarkdown(string value)
    {
        return (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private sealed record ExportRow(
        string Name,
        string ModId,
        string Version,
        string Side,
        string Dependencies,
        string Issues,
        string ConfigPath,
        string FilePath,
        string Enabled,
        string Status,
        string Website);

    private sealed record ExportColumn(
        ModListExportColumn Column,
        string Header,
        Func<ExportRow, string> Value);

    private static HttpClient CreateWebsiteHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0 ModListExport");
        return client;
    }
}
