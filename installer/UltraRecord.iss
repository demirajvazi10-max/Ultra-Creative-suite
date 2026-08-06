; ══════════════════════════════════════════════════════════════════════════
;  UltraRecord.iss
;  Inno Setup installer for Ultra Record
;
;  Same simple pattern as UltraShield/Ultra Captions: no external runtime
;  dependencies to download - it's a plain WPF/.NET 8 app. This installer
;  just copies the already-built application and creates shortcuts.
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
;  UltraRecord.iss" without touching this file - the local default below
;  stays untouched for manual testing.
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Record"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Demir Ajvazi"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraRecord.exe"

#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\Ultra-Creative-suite\UltraRecord\bin\Release\net8.0-windows"
#endif

[Setup]
; Unique GUID, generated for this app specifically - do not reuse any other
; Ultra app's GUID, or Windows will treat this as the same application.
AppId={{C01BCE15-2DC2-4C35-B9C8-5ED6C6B2E028}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=UltraRecordSetup-{#MyAppVersion}
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
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
