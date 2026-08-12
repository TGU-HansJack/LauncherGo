# LauncherGo

<p align="center">
  <a href="./README.md">简体中文</a> |
  <strong>English</strong>
</p>

<p align="center">
  <a href="https://github.com/vscn-studio/LauncherGo/releases"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/vscn-studio/LauncherGo?include_prereleases&amp;sort=semver"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/releases"><img alt="Total Downloads" src="https://img.shields.io/github/downloads/vscn-studio/LauncherGo/total?logo=github&amp;label=downloads"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/stargazers"><img alt="GitHub Stars" src="https://img.shields.io/github/stars/vscn-studio/LauncherGo?logo=github&amp;style=flat"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml"><img alt="Windows Build" src="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml/badge.svg?branch=2.0.0"></a>
  <a href="https://github.com/vscn-studio/LauncherGo/actions/workflows/windows-packages.yml"><img alt="Windows Build Count" src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2Fvscn-studio%2FLauncherGo%2Factions%2Fworkflows%2Fwindows-packages.yml%2Fruns%3Fper_page%3D1&amp;query=%24.total_count&amp;label=builds&amp;logo=githubactions"></a>
  <a href="./LICENSE"><img alt="License" src="https://img.shields.io/github/license/vscn-studio/LauncherGo"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&amp;logoColor=white">
  <img alt="Avalonia 12" src="https://img.shields.io/badge/Avalonia-12.0.1-8B44AC">
</p>

<p align="center">
  <strong>The second-generation Vintage Story server launcher</strong><br/>
  <span>Developed and maintained by Vintage Story CN Studio, also known as VSCN</span>
</p>

## Project Positioning

LauncherGo is a graphical server launcher for Vintage Story. Its goal is to integrate server downloads, profile management, save management, configuration editing, process control, automation, mod management, authentication, open server information transport, QQ bot integration, and FRP tunneling into one desktop application.

## Version Information

| Item | Current Status |
| --- | --- |
| Application version | Local development builds default to `2.5.5`; Windows packaging and Release publishing override `Version` and `InformationalVersion` through `.github/workflows/windows-packages.yml`, `.github/workflows/publish-release.yml`, or `v*` tags |
| Product stage | Second-generation server launcher, under active development |
| Target framework | `.NET net10.0` |
| Desktop UI | `Avalonia 12.0.1` and `Semi.Avalonia 12.0.1` |
| Default release platform | The current release workflows package `win-x64` as a self-contained installer, portable single-file package, framework-dependent small package, and embedded ServerAuth mod package |
| Vintage Story server version | Server versions are downloaded from the official or configured third-party server catalog, and each profile runs the selected version |
| Embedded authentication mod | `serverauth.dll`, with the current server-side integration version constant set to `1.0.0` |
| Auth mod build reference | GitHub Actions defaults to Vintage Story `1.22.2` server API references, and the version can be changed in workflow inputs |

## Current Features

| Area | Current Implementation |
| --- | --- |
| First launch | Welcome, appearance, directory setup, server download, and completion pages |
| Home | Server status, robot status, online players, network status, event ticker, and realtime charts |
| Console | Relay control channel, LogTail following, command sending, custom quick commands, and process status sync |
| Process control | `ServerProcessRelay`, background control channel, existing process attachment, relay state file, and orphan process handling |
| Profiles | Profile creation, import, deletion, refresh, and server version selection |
| Server configuration | Server settings, world configuration, world rules, multi-column layout, and automatic saving |
| Saves | Save creation, import, deletion, default launch save locking, and clickable save directory paths |
| Automation | Scheduled start and stop, scheduled backup, backup before shutdown, scheduled broadcast, and log export |
| Mod management | Mod scanning, enable and disable operations, dependency display, issue display, and file status display |
| Downloads | Server version list, search, download, server package import, and download cache cleanup |
| Connections | Regular FRP, third-party FRPC, OpenServerQuery open information, QQ bot, and ServerAuth password, Discourse SSO, and OAuth2/OIDC configuration |
| Settings | Server, appearance, network, advanced, about, sponsors, and contributors pages; GitHub proxy selection and automatic or manual LauncherGo update checks |
| Launcher updates | Installation-aware updates for the full installer, small installer, portable single-file build, and Small directory package, with SHA-256 verification and Markdown release notes |
| Logging | Application log files, console logs, automation runtime logs, server log export, and direct access to each profile's `Logs` directory |
| Internationalization | Chinese and English resources with runtime language switching |
| Release | Windows packaging, framework-dependent small-package distribution, prerelease, official release, and embedded ServerAuth build |
| Sponsor data | Fetched from `https://vscn.studio/api/afdian/sponsors`; the client does not store Afdian USERID or Token |

## Development Team

| Item | Content |
| --- | --- |
| Studio name | 微尘工作室（Vintage Story CN Studio） |
| Short name | VSCN |
| Main direction | Vintage Story Chinese community ecosystem, server tools, mod tools, information services, and community infrastructure |

## Studio Projects

| Project | Description |
| --- | --- |
| VSMAP | Legacy map preview project |
| LauncherGo | Second-generation Vintage Story server launcher |
| ServerAuth | Server authentication mod |
| OpenServerQuery | Server information transport and open information linkage |

## Studio Maintenance

| Area | Description |
| --- | --- |
| 复古物语中文社区 | Community maintenance for Chinese players and server administrators |
| 复古物语中文模组网 | Chinese mod publishing, indexing, and related content maintenance |
| 中文社区游戏服务器 | Community server operation, maintenance, and supporting services |

## Project Structure

| Path | Description |
| --- | --- |
| `LauncherGo.App` | Avalonia application entry point, host, theme, and global resources |
| `LauncherGo.ServerHost` | Independent server process host, recoverable control channel, and crash cleanup |
| `LauncherGo.Ui` | Main window, guide window, UI resources, platform window effects, and interface logic |
| `LauncherGo.Services` | Server downloads, profiles, saves, process control, logs, automation, FRP, OpenServerQuery, QQ bot, and authentication services |
| `LauncherGo.Abstractions` | Service interfaces and cross-layer abstractions |
| `LauncherGo.Domains` | Domain models, configuration models, enums, and data structures |
| `LauncherGo.Services/EmbeddedMods/VsslAuthMod` | Embedded ServerAuth mod source code |
| `installer` | Inno Setup script for Windows installer packages |
| `.github/workflows` | Windows packaging, small-package distribution, Release publishing, and embedded authentication mod build workflows |

See [`docs/serverauth-oauth2.md`](docs/serverauth-oauth2.md) for ServerAuth OAuth2/OIDC configuration and a Vintage Story Connect example.

## Open Source Projects Used

| Project | Usage | Referenced Version |
| --- | --- | --- |
| Avalonia | Cross-platform desktop UI framework | `12.0.1` |
| Avalonia.Desktop | Desktop application runtime support | `12.0.1` |
| Semi.Avalonia | Semi-style Avalonia theme | `12.0.1` |
| AvaloniaUI.DiagnosticsSupport | Debug diagnostics support | `2.2.1` |
| Microsoft.Extensions.Hosting | Application hosting and dependency injection foundation | `10.0.1` |
| Microsoft.Extensions.DependencyInjection.Abstractions | Dependency injection abstractions | `10.0.1` |
| Microsoft.Extensions.Hosting.Abstractions | Hosting abstractions | `10.0.1` |
| Microsoft.Extensions.Logging.Abstractions | Logging abstractions | `10.0.1` |
| Microsoft.Data.Sqlite | Local data storage for QQ bot and related services | `10.0.7` |
| Serilog | Application logging | `4.3.1` |
| Serilog.Enrichers.Thread | Thread information enrichment for logs | `4.0.0` |
| Serilog.Extensions.Logging | Microsoft Logging integration for Serilog | `10.0.0` |
| Serilog.Sinks.File | File log output | `7.0.0` |
| protobuf-net | Vintage Story and robot related data handling | `3.2.56` |
| System.Management | Windows management information access | `10.0.8` |
| ZstdSharp.Port | Zstandard compressed data handling | `0.8.6` |
| actions/checkout | GitHub Actions repository checkout | `v4` |
| actions/setup-dotnet | GitHub Actions .NET SDK setup | `v4` |
| actions/upload-artifact | GitHub Actions artifact upload | `v4` |
| softprops/action-gh-release | GitHub Release creation and asset upload | `v2` |
| Inno Setup | Windows installer generation | `6.x` |

The copyrights and licenses of the projects above belong to their respective owners. The authoritative license text is the one declared by each upstream repository and NuGet package.

## Development Environment

| Item | Requirement |
| --- | --- |
| .NET SDK | `10.0.x` |
| Recommended system | Windows 10 or later |
| Runtime system | Avalonia supports cross-platform runtime, while the current release workflow mainly targets Windows x64 |
| ServerAuth build | Requires a Vintage Story server directory, or the `VINTAGE_STORY` environment variable pointing to a directory containing `VintagestoryAPI.dll` |

## Local Run

```powershell
dotnet restore .\LauncherGo.slnx
dotnet run --project .\LauncherGo.App\LauncherGo.App.csproj
```

## Hot Reload Development

```powershell
dotnet watch run --project .\LauncherGo.App\LauncherGo.App.csproj
```

If hot reload fails because assemblies are locked, stop the running `LauncherGo.App` process and run the command again.

## Small Package Publishing

```powershell
dotnet publish .\LauncherGo.App\LauncherGo.App.csproj -c Release -p:PublishProfile=SmallPackage-win-x64 -p:Version=0.0.0 -p:InformationalVersion=0.0.0 -o .\artifacts\publish\small-package
```

The `SmallPackage-win-x64` publish profile produces a framework-dependent Windows x64 distribution folder and automatically removes `.pdb` debug symbols after publish. Manual use of this profile requires `.NET 10 Runtime (x64)` to be installed.

`.github/workflows/windows-small-package.yml` produces a self-contained Windows x64 small package: it retains the .NET Runtime while removing all `.pdb` debug symbols.

The same workflow also produces `LauncherGo-Small-Setup-<version>-win-x64.exe`, which does not require a preinstalled .NET Runtime. The full self-contained installer retains debug symbols and is still generated by `installer/LauncherGo.iss`.

## Building the Embedded ServerAuth Mod

```powershell
$env:VINTAGE_STORY="E:\\Path\\To\\VintageStoryServer"
dotnet build .\LauncherGo.Services\EmbeddedMods\VsslAuthMod\VsslAuthMod.csproj -c Release
```

The directory referenced by `VINTAGE_STORY` must contain `VintagestoryAPI.dll`, `VintagestoryLib.dll`, and `Lib\protobuf-net.dll`.

## License

LauncherGo is licensed under `GNU General Public License v3.0`. See [LICENSE](./LICENSE) for the full license text.
