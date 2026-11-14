; Per-user Windows setup for a HopeLab app (no admin prompt), into %LOCALAPPDATA%\Programs\<AppName>: the folder
; install.ps1 uses. The folder is the user's own, so the app's in-place updater can replace its files later, and it
; never moves, so anything holding a path into it keeps working across updates.
;
; The shared release workflow builds it from the published folder and the app's hopelab-app.json:
;   iscc /DAppName=OneRing /DAppId={8B7C…} /DAppExe=OneRing /DAppVersion=0.1.412 /DSourceDir=<published> ^
;        /DOutputDir=dist /DIconFile=<.ico> /DPublisher=QuantumHope /DDescription="…" /DAddToPath=1 ^
;        /DStopArgs="ring|host stop" /DUninstallArgs="ring|host uninstall-service" /DProcesses="OneRing,ring-host" app.iss

#ifndef AppName
  #error Pass /DAppName=...
#endif
#ifndef AppId
  #error Pass /DAppId={GUID}
#endif
#ifndef AppExe
  #define AppExe AppName
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif
#ifndef Publisher
  #define Publisher "QuantumHope"
#endif
#ifndef Description
  #define Description AppName
#endif
#ifndef AddToPath
  #define AddToPath "0"
#endif
; "exe|arguments": a clean stop to try first, and what to run before uninstalling. Empty for none.
#ifndef StopArgs
  #define StopArgs ""
#endif
#ifndef UninstallArgs
  #define UninstallArgs ""
#endif
; Comma-separated executables (no .exe) to stop, from the install folder only, before files are replaced.
#ifndef Processes
  #define Processes AppExe
#endif
#ifndef RepoUrl
  #define RepoUrl "https://github.com/QuantumHope"
#endif

#if StopArgs != ""
  #define StopExe Copy(StopArgs, 1, Pos("|", StopArgs) - 1)
  #define StopParams Copy(StopArgs, Pos("|", StopArgs) + 1, 1000)
#endif
#if UninstallArgs != ""
  #define UninstallExe Copy(UninstallArgs, 1, Pos("|", UninstallArgs) - 1)
  #define UninstallParams Copy(UninstallArgs, Pos("|", UninstallArgs) + 1, 1000)
#endif

[Setup]
; Never change an app's AppId once released: upgrades find the installed copy by it, and its in-place updater writes
; Apps & features' version under it.
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
#ifdef IconFile
SetupIconFile={#IconFile}
#endif
UninstallDisplayIcon={app}\{#AppExe}.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
#if AddToPath == "1"
ChangesEnvironment=yes
#endif
; The app's processes are stopped in PrepareToInstall; don't let the Restart Manager prompt about them as well.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
#if AddToPath == "1"
Name: "addtopath"; Description: "Add {#AppName} to your PATH (for its commands in terminals)"; GroupDescription: "Command line:"
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}.exe"; WorkingDir: "{app}"; Comment: "{#Description}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}.exe"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent

#if UninstallArgs != ""
[UninstallRun]
Filename: "{app}\{#UninstallExe}.exe"; Parameters: "{#UninstallParams}"; Flags: runhidden waituntilterminated; RunOnceId: "BeforeUninstall"
#endif

[UninstallDelete]
; In-place updates add and replace files setup doesn't know about; remove the whole folder, not only setup's list.
Type: filesandordirs; Name: "{app}"

[Code]
const
  EnvKey = 'Environment';

function PathContains(Path, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') > 0;
end;

procedure AddToPath(Dir: string);
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Path) then Path := '';
  if PathContains(Path, Dir) then exit;
  if (Path <> '') and (Copy(Path, Length(Path), 1) <> ';') then Path := Path + ';';
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Path + Dir);
end;

procedure RemoveFromPath(Dir: string);
var
  Path: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Path) then exit;
  Path := ';' + Path + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Path));
  if P = 0 then exit;
  Delete(Path, P, Length(Dir) + 1);
  Path := Copy(Path, 2, Length(Path) - 2);
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Path);
end;

{ Asks the app's services to stop cleanly, then stops anything of its own still running from the install folder, so
  setup can replace the files. }
procedure StopApp;
var
  Code: Integer;
  Dir, Quoted, Names: string;
begin
  Dir := ExpandConstant('{app}');
#if StopArgs != ""
  if FileExists(Dir + '\{#StopExe}.exe') then
    Exec(Dir + '\{#StopExe}.exe', '{#StopParams}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
#endif
  Quoted := Dir;
  StringChangeEx(Quoted, '''', '''''', True); { a quote in the user name, inside a PowerShell '...' string }
  Names := '{#Processes}';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process ' + Names + ' -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path) -ieq ''' + Quoted + ''') } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
#if AddToPath == "1"
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then AddToPath(ExpandConstant('{app}'));
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopApp;
    RemoveFromPath(ExpandConstant('{app}'));
  end;
end;
