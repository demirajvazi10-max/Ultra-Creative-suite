; ══════════════════════════════════════════════════════════════════════════
;  UltraCast.iss
;  Inno Setup installer for Ultra Cast
;
;  Same simple pattern as UltraRecord/UltraCaptions (plain WPF/.NET 8 app,
;  no admin-installed dependencies other than a downloaded FFmpeg), plus
;  one addition borrowed directly from UltraVideoEditor.iss: FFmpeg is
;  downloaded via curl.exe (built into Windows 10 1803+/Windows 11) if it
;  isn't already present, since Ultra Cast needs it to encode recordings.
;  Unlike the Video Editor, no VLC/Ollama - Ultra Cast has no AI or
;  playback dependency, just the encoder.
;
;  BUILD-TIME DEPENDENCIES (on your machine, not the end user's):
;    - Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
;  BEFORE COMPILING, ADJUST (if needed):
;    - SetupIconFile, if you have an .ico (waiting on the logo)
;    - #define MyAppSourceDir below - verify it matches your current build
;      output folder.
;
;  CI/GitHub Actions: MyAppVersion and MyAppSourceDir can be overridden
;  from outside via "ISCC /DMyAppVersion=... /DMyAppSourceDir=...
;  UltraCast.iss" without touching this file - the local default below
;  stays untouched for manual testing.
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Cast"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Demir Ajvazi"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraCast.exe"

#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\Ultra-Creative-suite\UltraCast\bin\Release\net8.0-windows"
#endif

[Setup]
; Unique GUID, generated for this app specifically - do not reuse any other
; Ultra app's GUID, or Windows will treat this as the same application.
AppId={{7E3A9F52-4B6C-4D8E-9A1F-2C5D8E6B3F71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Same reasoning as UltraVideoEditor.iss: the app is a win-x64 self-contained
; build, and FFmpeg is downloaded as a win64 build - without this, {pf}
; would resolve to the 32-bit Program Files and the installer would never
; find/confirm the FFmpeg it just installed.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=UltraCastSetup-{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; SetupIconFile=app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.DesktopIconGroup=Additional icons:
english.DesktopIconTaskName=Create a desktop icon

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"

[Files]
; ffmpeg.exe/ffprobe.exe are excluded from the bulk copy - they're fetched
; separately by InstallDependencies below and placed in {app}\Ffmpeg, same
; split as the Video Editor installer.
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,ffmpeg.exe,ffprobe.exe,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]

var
  StatusPage: TOutputProgressWizardPage;
  CurlExitCode: Integer; // last curl.exe ResultCode, for diagnostic MsgBoxes below

// Downloads a file via curl.exe (built into Windows 10 1803+/Windows 11 -
// no external dependencies). Returns True if the file is actually present
// on disk after the call. Identical helper to the one in
// UltraVideoEditor.iss, copied here rather than shared, since each .iss
// is compiled standalone.
function DownloadFileCurl(Url, DestPath: String): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\curl.exe'),
       '-L -f -s --retry 3 --retry-delay 2 -o "' + DestPath + '" "' + Url + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  CurlExitCode := ResultCode;
  Result := FileExists(DestPath);
end;

// Turns curl's numeric exit code into a short, human-readable reason.
function CurlErrorReason(Code: Integer): String;
begin
  case Code of
    6:  Result := 'could not resolve host — check your internet connection';
    7:  Result := 'could not connect to server';
    22: Result := 'server returned an HTTP error (e.g. the file was moved or no longer exists — this is a bug in the installer, please report it)';
    28: Result := 'connection timed out';
  else
    Result := 'curl exit code ' + IntToStr(Code);
  end;
end;

// Recursively searches RootDir for FileNameToFind and copies the FIRST
// match found to DestPath - same helper as in UltraVideoEditor.iss.
procedure CopyFirstMatchRecursive(RootDir, FileNameToFind, DestPath: String);
var
  FindRec: TFindRec;
  FullPath: String;
begin
  if FileExists(DestPath) then Exit; // already found in a previous call/attempt

  if FindFirst(RootDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          FullPath := RootDir + '\' + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CopyFirstMatchRecursive(FullPath, FileNameToFind, DestPath)
          else if CompareText(FindRec.Name, FileNameToFind) = 0 then
          begin
            if not FileExists(DestPath) then
              FileCopy(FullPath, DestPath, False);
          end;
        end;
      until (not FindNext(FindRec)) or FileExists(DestPath);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InstallDependencies();
var
  ResultCode: Integer;
  FfmpegDestDir: String;
begin
  FfmpegDestDir := ExpandConstant('{app}\Ffmpeg');

  StatusPage.SetText('Setting up Ultra Cast...', '');
  StatusPage.Show;
  try
    if not FileExists(FfmpegDestDir + '\ffmpeg.exe') then
    begin
      StatusPage.SetText('Downloading FFmpeg...', 'This may take a moment (~100 MB)');
      // Same permanent "latest" URL used by UltraVideoEditor.iss - always
      // points at the newest build, never goes stale like a dated tag would.
      if not DownloadFileCurl('https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip',
                               ExpandConstant('{tmp}\ffmpeg.zip')) then
        MsgBox('Could not download FFmpeg (' + CurlErrorReason(CurlExitCode) + '). Download it manually from ffmpeg.org and place ffmpeg.exe in: ' + FfmpegDestDir, mbInformation, MB_OK);

      if FileExists(ExpandConstant('{tmp}\ffmpeg.zip')) then
      begin
        StatusPage.SetText('Installing FFmpeg...', '');
        ForceDirectories(FfmpegDestDir);
        ForceDirectories(ExpandConstant('{tmp}\ffmpeg_extract'));
        // tar.exe ships built into Windows from version 1803 onward and can unpack .zip.
        Exec(ExpandConstant('{sys}\tar.exe'),
             '-xf "' + ExpandConstant('{tmp}\ffmpeg.zip') + '" -C "' + ExpandConstant('{tmp}\ffmpeg_extract') + '"',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

        CopyFirstMatchRecursive(ExpandConstant('{tmp}\ffmpeg_extract'), 'ffmpeg.exe', FfmpegDestDir + '\ffmpeg.exe');
        CopyFirstMatchRecursive(ExpandConstant('{tmp}\ffmpeg_extract'), 'ffprobe.exe', FfmpegDestDir + '\ffprobe.exe');

        if not FileExists(FfmpegDestDir + '\ffmpeg.exe') then
          MsgBox('FFmpeg could not be installed automatically. Download it manually from ffmpeg.org and place ffmpeg.exe in: ' + FfmpegDestDir, mbInformation, MB_OK);
      end;
    end;
  finally
    StatusPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallDependencies();
end;

procedure InitializeWizard();
begin
  StatusPage := CreateOutputProgressPage('Setting up Ultra Cast', 'Please wait while FFmpeg is downloaded and installed.');
end;
