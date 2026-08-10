#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-local"
#endif

#ifndef BuildDir
  #define BuildDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "LauncherGo-Setup-{#MyAppVersion}-win-x64"
#endif

[Setup]
AppId={{26FFCC71-304F-4FF1-AC1A-3E244C276414}
AppName=LauncherGo
AppVersion={#MyAppVersion}
AppPublisher=LauncherGo
DefaultDirName={autopf}\LauncherGo
DefaultGroupName=LauncherGo
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\LauncherGo.App.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifexist "C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[CustomMessages]
english.DotNetRuntimeTask=Open the .NET 10 Desktop Runtime (x64) download page
english.DotNetRuntimeRun=Open the .NET 10 Desktop Runtime (x64) download page
chinesesimp.DotNetRuntimeTask=打开 .NET 10 Desktop Runtime (x64) 下载页面
chinesesimp.DotNetRuntimeRun=打开 .NET 10 Desktop Runtime (x64) 下载页面

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifdef SmallPackage
Name: "dotnetruntime"; Description: "{cm:DotNetRuntimeTask}"; GroupDescription: "Prerequisites:"; Check: not IsDotNetDesktopRuntime10Installed
#endif

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"
Name: "{autodesktop}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"; Tasks: desktopicon

[Code]
function IsDotNetDesktopRuntime10Installed: Boolean;
var
  FindData: TFindData;
  SearchPath: string;
begin
  SearchPath := ExpandConstant('{autopf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*');
  Result := FindFirst(SearchPath, FindData);
  if Result then
    FindClose(FindData);
end;

[Run]
Filename: "{app}\LauncherGo.App.exe"; Description: "{cm:LaunchProgram,LauncherGo}"; Flags: nowait postinstall skipifsilent
#ifdef SmallPackage
Filename: "https://dotnet.microsoft.com/download/dotnet/10.0"; Description: "{cm:DotNetRuntimeRun}"; Flags: postinstall shellexec skipifsilent; Tasks: dotnetruntime
#endif
