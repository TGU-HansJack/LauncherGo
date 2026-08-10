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
english.DotNetRuntimeTask=Open the .NET 10 Runtime (x64) download page
english.DotNetRuntimeRun=Open the .NET 10 Runtime (x64) download page
#ifexist "C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl"
chinesesimp.DotNetRuntimeTask=打开 .NET 10 Runtime (x64) 下载页面
chinesesimp.DotNetRuntimeRun=打开 .NET 10 Runtime (x64) 下载页面
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifdef SmallPackage
Name: "dotnetruntime"; Description: "{cm:DotNetRuntimeTask}"; GroupDescription: "Prerequisites:"; Check: not IsDotNetRuntime10Installed
#endif

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"
Name: "{autodesktop}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"; Tasks: desktopicon

[Code]
var
  DotNetRuntime10Checked: Boolean;
  DotNetRuntime10Installed: Boolean;

function HasDotNetRuntime10(const DotNetPath: string): Boolean;
var
  Index: Integer;
  ResultCode: Integer;
  Output: TExecOutput;
begin
  Result := False;
  if not FileExists(DotNetPath) then begin
    Log('dotnet host not found: ' + DotNetPath);
    exit;
  end;

  try
    if not ExecAndCaptureOutput(DotNetPath, '--list-runtimes', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode, Output) then begin
      Log('Failed to start dotnet runtime detection.');
      exit;
    end;

    if ResultCode <> 0 then begin
      Log('dotnet runtime detection exited with code ' + IntToStr(ResultCode) + '.');
      exit;
    end;

    for Index := 0 to GetArrayLength(Output.StdOut) - 1 do begin
      if Pos('Microsoft.NETCore.App 10.', Output.StdOut[Index]) = 1 then begin
        Log('Found compatible .NET runtime: ' + Output.StdOut[Index]);
        Result := True;
        exit;
      end;
    end;
  except
    Log('dotnet runtime detection failed: ' + GetExceptionMessage);
  end;

  Log('No compatible .NET 10 Runtime (x64) was found.');
end;

function IsDotNetRuntime10Installed: Boolean;
var
  DotNetPath: string;
begin
  if not DotNetRuntime10Checked then begin
    DotNetRuntime10Checked := True;
    DotNetPath := ExpandConstant('{commonpf64}\dotnet\dotnet.exe');
    DotNetRuntime10Installed := HasDotNetRuntime10(DotNetPath);
  end;

  Result := DotNetRuntime10Installed;
end;

[Run]
Filename: "{app}\LauncherGo.App.exe"; Description: "{cm:LaunchProgram,LauncherGo}"; Flags: nowait postinstall skipifsilent
#ifdef SmallPackage
Filename: "https://dotnet.microsoft.com/download/dotnet/10.0"; Description: "{cm:DotNetRuntimeRun}"; Flags: postinstall shellexec skipifsilent; Tasks: dotnetruntime
#endif
