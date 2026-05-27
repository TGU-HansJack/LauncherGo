# LauncherGo

LauncherGo is a Vintage Story server launcher built with `Avalonia 12 + Semi.Avalonia`.  
At the current stage, this repository focuses on the first-launch onboarding window.

## Preview

![LauncherGo Guidance Interface](./guidance_interface.gif)

## Current Status

- Implemented: first-launch guide window (Welcome, Appearance, Global Directories, Download, Complete)
- Implemented: Chinese/English switch, theme switch, directory selection, server package download/import
- In progress: full launcher feature set

## Project Structure

- `LauncherGo.App`: application entry and host
- `LauncherGo.Ui`: Avalonia UI layer
- `LauncherGo.Services`: service implementations (download, preferences, etc.)
- `LauncherGo.Abstractions`: shared interfaces
- `LauncherGo.Domains`: domain models and enums

## Requirements

- `.NET SDK 10.0+`
- Windows/macOS/Linux (Avalonia cross-platform)

## Quick Start

```powershell
dotnet restore .\LauncherGo.slnx
dotnet run --project .\LauncherGo.App\LauncherGo.App.csproj
```

## Hot Reload (Development)

```powershell
dotnet watch run --project .\LauncherGo.App\LauncherGo.App.csproj
```

If hot reload fails due to locked assemblies, stop the running `LauncherGo.App` process and retry.

## License

This project is licensed under `GNU General Public License v3.0` (GPL-3.0).  
See [LICENSE](./LICENSE) for details.
