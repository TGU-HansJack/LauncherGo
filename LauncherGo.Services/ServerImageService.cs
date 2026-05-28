using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class ServerImageService : IServerImageService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
        ".bmp"
    };

    public string GetImageRootPath(InstanceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.DirectoryPath))
        {
            throw new InvalidOperationException("档案目录不能为空。");
        }

        return Path.Combine(Path.GetFullPath(profile.DirectoryPath), "OpenServerQuery");
    }

    public Task<IReadOnlyList<ServerImageFileInfo>> LoadServerImagesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = GetImageRootPath(profile);
        var result = new List<ServerImageFileInfo>();
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<ServerImageFileInfo>>(result);
        }

        var coverFile = Directory
            .EnumerateFiles(root, "cover.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedImageFile)
            .OrderBy(GetCoverSortKey)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(coverFile) && BuildInfo(coverFile, ServerImageKind.Cover, root) is { } cover)
        {
            result.Add(cover);
        }

        var showcaseRoot = Path.Combine(root, "showcase");
        if (Directory.Exists(showcaseRoot))
        {
            foreach (var file in Directory
                         .EnumerateFiles(showcaseRoot, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsSupportedImageFile)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (BuildInfo(file, ServerImageKind.Showcase, root) is { } showcase)
                {
                    result.Add(showcase);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ServerImageFileInfo>>(result);
    }

    public async Task<ServerImageFileInfo> ImportImageAsync(
        InstanceProfile profile,
        string sourcePath,
        ServerImageKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidOperationException("图片路径不能为空。");
        }

        var sourceFullPath = Path.GetFullPath(sourcePath.Trim());
        if (!File.Exists(sourceFullPath))
        {
            throw new InvalidOperationException("图片文件不存在。");
        }

        if (!IsSupportedImageFile(sourceFullPath))
        {
            throw new InvalidOperationException("仅支持 png/jpg/jpeg/webp/gif/bmp 图片。");
        }

        var root = GetImageRootPath(profile);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "showcase"));

        string destinationPath;
        if (kind == ServerImageKind.Cover)
        {
            var sourceBytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken);
            DeleteExistingCoverFiles(root, sourceFullPath);
            destinationPath = Path.Combine(root, "cover" + Path.GetExtension(sourceFullPath).ToLowerInvariant());
            await File.WriteAllBytesAsync(destinationPath, sourceBytes, cancellationToken);
        }
        else
        {
            var showcaseRoot = Path.Combine(root, "showcase");
            var baseName = LauncherWorkspacePathHelper.SanitizeFileName(Path.GetFileNameWithoutExtension(sourceFullPath));
            var ext = Path.GetExtension(sourceFullPath).ToLowerInvariant();
            destinationPath = GetUniqueShowcasePath(showcaseRoot, baseName, ext);
            await using var sourceStream = File.Open(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var destinationStream = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }

        return BuildInfo(destinationPath, kind, root)
               ?? throw new InvalidOperationException("图片导入失败。");
    }

    public async Task<int> ImportImagesFromFolderAsync(
        InstanceProfile profile,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidOperationException("图片文件夹不能为空。");
        }

        var sourceRoot = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException("图片文件夹不存在。");
        }

        var count = 0;
        foreach (var file in Directory
                     .EnumerateFiles(sourceRoot, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedImageFile)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ImportImageAsync(profile, file, ServerImageKind.Showcase, cancellationToken);
            count++;
        }

        return count;
    }

    public Task DeleteImageAsync(
        InstanceProfile profile,
        ServerImageFileInfo image,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(image.FullPath))
        {
            return Task.CompletedTask;
        }

        var root = LauncherWorkspacePathHelper.NormalizePath(GetImageRootPath(profile));
        var fullPath = LauncherWorkspacePathHelper.NormalizePath(image.FullPath);
        if (!LauncherWorkspacePathHelper.IsSameOrChildPath(fullPath, root))
        {
            throw new InvalidOperationException("只能删除当前档案目录中的图片。");
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private static ServerImageFileInfo? BuildInfo(string fullPath, ServerImageKind kind, string rootPath)
    {
        var file = new FileInfo(fullPath);
        if (!file.Exists || !IsSupportedImageFile(file.FullName))
        {
            return null;
        }

        return new ServerImageFileInfo
        {
            Kind = kind,
            FullPath = file.FullName,
            RelativePath = Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/'),
            FileName = file.Name,
            SizeBytes = file.Length,
            LastWriteUtc = file.LastWriteTimeUtc
        };
    }

    private static bool IsSupportedImageFile(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath) ?? string.Empty);
    }

    private static int GetCoverSortKey(string path)
    {
        return (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 2,
            ".webp" => 3,
            ".gif" => 4,
            ".bmp" => 5,
            _ => 100
        };
    }

    private static void DeleteExistingCoverFiles(string coverDirectory, string? preservePath = null)
    {
        foreach (var file in Directory.EnumerateFiles(coverDirectory, "cover.*", SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedImageFile))
        {
            if (!string.IsNullOrWhiteSpace(preservePath) &&
                Path.GetFullPath(file).Equals(Path.GetFullPath(preservePath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(file);
        }
    }

    private static string GetUniqueShowcasePath(string showcaseRoot, string baseName, string ext)
    {
        Directory.CreateDirectory(showcaseRoot);
        var safeBaseName = string.IsNullOrWhiteSpace(baseName) ? "showcase" : baseName;
        var candidate = Path.Combine(showcaseRoot, $"{safeBaseName}{ext}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(showcaseRoot, $"{safeBaseName}-{i:000}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(showcaseRoot, $"{safeBaseName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }
}
