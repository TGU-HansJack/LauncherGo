using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class LauncherUpdateService : ILauncherUpdateService
{
    private const string Repository = "vscn-studio/LauncherGo";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public string CurrentVersion => ReadCurrentVersion();

    public LauncherPackageKind PackageKind => DetectPackageKind();

    public async Task<LauncherUpdateCheckResult> CheckLatestAsync(
        GitHubProxyKind proxy,
        CancellationToken cancellationToken = default)
    {
        var apiUrl = BuildProxyUrl($"https://api.github.com/repos/{Repository}/releases/latest", proxy);
        using var response = await HttpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub 返回了空的版本信息。");

        var releaseModel = new LauncherUpdateRelease
        {
            TagName = release.TagName ?? string.Empty,
            Name = release.Name ?? release.TagName ?? string.Empty,
            Body = RemoveProxyPrefix(release.Body ?? string.Empty, proxy),
            HtmlUrl = release.HtmlUrl ?? string.Empty,
            IsPrerelease = release.Prerelease,
            PublishedAtUtc = release.PublishedAt,
            Assets = (release.Assets ?? [])
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                .Select(asset => new LauncherUpdateAsset
                {
                    Name = asset.Name!,
                    DownloadUrl = BuildProxyUrl(asset.BrowserDownloadUrl!, proxy),
                    Size = asset.Size,
                    Digest = asset.Digest ?? string.Empty
                })
                .ToList()
        };

        var current = CurrentVersion;
        var latest = NormalizeVersion(releaseModel.TagName);
        var isAvailable = CompareVersions(latest, NormalizeVersion(current)) > 0;
        var packageKind = PackageKind;
        return new LauncherUpdateCheckResult
        {
            Release = releaseModel,
            CurrentVersion = current,
            LatestVersion = latest,
            PackageKind = packageKind,
            SelectedAsset = SelectAsset(releaseModel.Assets, packageKind),
            IsUpdateAvailable = isAvailable
        };
    }

    public async Task PrepareAndLaunchUpdateAsync(
        LauncherUpdateCheckResult update,
        GitHubProxyKind proxy,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable)
            throw new InvalidOperationException("当前已经是最新版本。");

        var asset = update.SelectedAsset ?? throw new InvalidOperationException(
            $"未找到适用于当前安装方式（{update.PackageKind}）的更新文件。");
        var updateRoot = Path.Combine(LauncherPathHelper.AppRoot, "updates", update.LatestVersion);
        Directory.CreateDirectory(updateRoot);
        var fileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("更新文件名无效。");
        var assetPath = Path.Combine(updateRoot, fileName);

        using var response = await HttpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(assetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                if (length is > 0)
                    progress?.Report((double)total / length.Value);
            }
        }
        progress?.Report(1d);
        await VerifyDigestAsync(assetPath, asset.Digest, cancellationToken).ConfigureAwait(false);

        var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, BuildUpdateScript(), cancellationToken).ConfigureAwait(false);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ParentProcessId {Environment.ProcessId} -Asset \"{assetPath}\" -Kind \"{update.PackageKind}\" -InstallDir \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" -Executable \"{processPath}\""
        };
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("无法启动更新程序。");

    }

    internal static LauncherPackageKind DetectPackageKind(string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var executable = Environment.ProcessPath ?? string.Empty;
        var markerPath = Path.Combine(root, "launchergo-package-kind.txt");
        if (File.Exists(markerPath) && Enum.TryParse<LauncherPackageKind>(File.ReadAllText(markerPath).Trim(), true, out var markedKind))
            return markedKind;
        if (File.Exists(Path.Combine(root, "unins000.exe")) || File.Exists(Path.Combine(root, "unins001.exe")))
        {
            var hasSymbols = Directory.EnumerateFiles(root, "*.pdb", SearchOption.TopDirectoryOnly).Any();
            return hasSymbols ? LauncherPackageKind.Installer : LauncherPackageKind.SmallInstaller;
        }

        if (File.Exists(Path.Combine(root, "coreclr.dll")) || File.Exists(Path.Combine(root, "hostfxr.dll")))
            return LauncherPackageKind.SmallPackage;
        return executable.EndsWith("LauncherGo.App.exe", StringComparison.OrdinalIgnoreCase)
            ? LauncherPackageKind.Portable
            : LauncherPackageKind.Unknown;
    }

    internal static LauncherUpdateAsset? SelectAsset(IEnumerable<LauncherUpdateAsset> assets, LauncherPackageKind kind)
    {
        var prefix = kind switch
        {
            LauncherPackageKind.Installer => "LauncherGo-Setup-",
            LauncherPackageKind.SmallInstaller => "LauncherGo-Small-Setup-",
            LauncherPackageKind.Portable => "LauncherGo-portable-",
            LauncherPackageKind.SmallPackage => "LauncherGo-small-package-",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        return assets.FirstOrDefault(asset => asset.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                              asset.Name.EndsWith(kind is LauncherPackageKind.Installer or LauncherPackageKind.SmallInstaller ? ".exe" : ".zip", StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildProxyUrl(string url, GitHubProxyKind proxy)
    {
        var prefix = proxy switch
        {
            GitHubProxyKind.GhProxy => "https://gh-proxy.com/",
            GitHubProxyKind.GhProxyV6 => "https://v6.gh-proxy.com/",
            GitHubProxyKind.GhProxyHk => "https://hk.gh-proxy.com/",
            GitHubProxyKind.GhProxyCdn => "https://cdn.gh-proxy.com/",
            GitHubProxyKind.GhProxyEdgeOne => "https://edgeone.gh-proxy.com/",
            _ => string.Empty
        };
        return prefix + url;
    }

    private static string RemoveProxyPrefix(string body, GitHubProxyKind proxy)
    {
        var prefix = BuildProxyUrl(string.Empty, proxy);
        return string.IsNullOrWhiteSpace(prefix) ? body : body.Replace(prefix, string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyDigestAsync(string path, string digest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return;
        var parts = digest.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase))
            return;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!actual.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件的 SHA-256 校验失败。");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LauncherGo", ReadCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string ReadCurrentVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        var value = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(value)) return NormalizeVersion(value);
        try { return NormalizeVersion(FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion ?? "0.0.0"); }
        catch { return "0.0.0"; }
    }

    internal static string NormalizeVersion(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        return dash >= 0 ? text[..dash] : text.Split('+')[0];
    }

    internal static int CompareVersions(string left, string right)
    {
        var l = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var r = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            _ = int.TryParse(i < l.Length ? l[i] : "0", out var lv);
            _ = int.TryParse(i < r.Length ? r[i] : "0", out var rv);
            if (lv != rv) return lv.CompareTo(rv);
        }
        return 0;
    }

    private static string BuildUpdateScript() => """
param([int]$ParentProcessId, [string]$Asset, [string]$Kind, [string]$InstallDir, [string]$Executable)
try {
  Wait-Process -Id $ParentProcessId -Timeout 60 -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
  if ($Kind -eq 'Installer' -or $Kind -eq 'SmallInstaller') {
    Start-Process -FilePath $Asset -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
  } else {
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ('LauncherGo-update-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $Asset -DestinationPath $stage -Force
    Copy-Item -Path (Join-Path $stage '*') -Destination $InstallDir -Recurse -Force
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
  }
  Start-Process -FilePath $Executable
} catch {
  Add-Content -LiteralPath (Join-Path $PSScriptRoot 'update-error.log') -Value $_
  Start-Process -FilePath $Executable
}
""";

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
