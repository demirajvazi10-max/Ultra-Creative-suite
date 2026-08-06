; ══════════════════════════════════════════════════════════════════════════
;  UltraCaptions.iss
;  Inno Setup installer for Ultra Captions
;
;  Same simple pattern as UltraShield: no external runtime dependencies to
;  download - it's a plain WPF/.NET 8 app. This installer just copies the
;  already-built application and creates shortcuts.
;
;  Whisper itself is NOT bundled (it's a separate Python install the user
;  needs for the auto-transcription feature) - manual keyboard timing works
;  without it either way, so this isn't a hard requirement to install/run.
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
;  UltraCaptions.iss" without touching this file - the local default below
;  stays untouched for manual testing.
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Captions"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Demir Ajvazi"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraCaptions.exe"

; Folder where your build output lives (.exe + DLLs). For a real release,
; build in the Release configuration first (Build > Rebuild), not Debug -
; Debug is unoptimized and carries extra .pdb symbols nobody outside of you
; needs. Adjust this path to match your actual checkout location.
#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\Ultra-Creative-suite\UltraCaptions\bin\Release\net8.0-windows"
#endif

[Setup]
; Unique GUID, generated for this app specifically - do not reuse any other
; Ultra app's GUID, or Windows will treat this as the same application.
AppId={{1B67DB97-7E82-4E59-8D05-8F1D0817485D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=UltraCaptionsSetup-{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; SetupIconFile=app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; English-only for now, same starting point as UltraShield. Mirror the
; German/Serbian setup from UltraVideoEditor.iss / UltraAudioEditor.iss
; here later if localization gets added.

[CustomMessages]
english.DesktopIconGroup=Additional icons:
english.DesktopIconTaskName=Create a desktop icon

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"

[Files]
; *.pdb                    - debug symbols, not needed by end users.
; runtimes\ios*/linux*/osx*/android*
;                           - non-Windows native binaries some NuGet
;                             packages pull in by default; this app only
;                             ever runs on Windows.
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
