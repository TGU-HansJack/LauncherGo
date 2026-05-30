# ServerMap

ServerMap is the LauncherGo/OpenServerQuery map mod package for Vintage Story servers.

It renders map tiles, player positions, spawn, traders, translocators, and marker layers into the profile `ModData/<save>/ServerMap` directory. LauncherGo reads those files and uploads them to the external ServerMap web project through OSQ.

## Build

```powershell
dotnet build .\livemap.csproj /p:VINTAGE_STORY=D:\Vintagestory
```

The build output is `bin/Debug/ServerMap.dll`. LauncherGo embeds that folder under `LauncherGo.Services/EmbeddedMods/servermap`.
