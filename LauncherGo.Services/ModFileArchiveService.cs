using System.IO.Compression;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     Packages installed server and universal mods while excluding client-only mods.
/// </summary>
public sealed class ModFileArchiveService : IModFileArchiveService
{
    public async Task CreateServerModArchiveAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(destination);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(static mod => !IsClientOnly(mod)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = mod.FilePath?.Trim() ?? string.Empty;
            if (File.Exists(sourcePath))
            {
                var fileName = GetUniqueName(
                    usedNames,
                    $"Mods/{SanitizeEntryName(Path.GetFileName(sourcePath))}");
                archive.CreateEntryFromFile(sourcePath, fileName, CompressionLevel.Fastest);
                continue;
            }

            if (!Directory.Exists(sourcePath))
                continue;

            var folderName = SanitizeEntryName(Path.GetFileName(sourcePath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = SanitizeEntryName(mod.ModId);
            folderName = GetUniqueName(usedNames, $"Mods/{folderName}");

            foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourcePath, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var entryName = $"{folderName}/{relativePath}";
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
            }
        }

        await destination.FlushAsync(cancellationToken);
    }

    internal static bool IsClientOnly(ModEntry mod)
    {
        var side = mod.Side?.Trim() ?? string.Empty;
        return side.Equals("client", StringComparison.OrdinalIgnoreCase) ||
               side.Equals("client-only", StringComparison.OrdinalIgnoreCase) ||
               side.Equals("clientonly", StringComparison.OrdinalIgnoreCase) ||
               side.Equals("客户端", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueName(HashSet<string> usedNames, string candidate)
    {
        var normalized = candidate.Trim('/');
        if (usedNames.Add(normalized))
            return normalized;

        var extension = Path.GetExtension(normalized);
        var stem = extension.Length > 0 ? normalized[..^extension.Length] : normalized;
        for (var index = 2; ; index++)
        {
            var alternative = $"{stem}-{index}{extension}";
            if (usedNames.Add(alternative))
                return alternative;
        }
    }

    private static string SanitizeEntryName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "mod";

        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character).ToArray());
    }
}
