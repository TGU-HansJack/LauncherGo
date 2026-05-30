#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-local"
#endif

#ifndef BuildDir
  #define BuildDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{26FFCC71-304F-4FF1-AC1A-3E244C276414}
AppName=LauncherGo
AppVersion={#MyAppVersion}
AppPublisher=LauncherGo
DefaultDirName={autopf}\LauncherGo
DefaultGroupName=LauncherGo
OutputDir={#OutputDir}
OutputBaseFilename=LauncherGo-Setup-{#MyAppVersion}-win-x64
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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"
Name: "{autodesktop}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LauncherGo.App.exe"; Description: "{cm:LaunchProgram,LauncherGo}"; Flags: nowait postinstall skipifsilent
