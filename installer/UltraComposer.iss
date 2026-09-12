; Ultra Composer - Inno Setup script
;
; Meant to live at the monorepo root's installer/ folder, next to the other
; Ultra apps' .iss files (same convention as Ultra Video Editor and Ultra
; Audio Editor). Built automatically by
; .github/workflows/composer-release.yml on every "composer-v*" tag, or
; compile it by hand from a Developer Command Prompt with:
;
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\UltraComposer.iss
;
; (that manual command uses the MyAppVersion fallback below; pass
; /DMyAppVersion=1.2.3 to override it, same as the CI workflow does.)

#define MyAppName "Ultra Composer"
#define MyAppPublisher "Ultra Creative Suite"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraComposer.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0-beta.1"
#endif

; A fixed AppId keeps upgrades (installing a newer version over an older
; one) working correctly instead of Windows treating every release as an
; unrelated program - generated once for Ultra Composer specifically, never
; reuse another Ultra app's AppId here and never change this on a later
; release.
[Setup]
AppId={{5F2C9E3B-7B4C-4E4A-9B7A-1C6D9E9B7A4F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Ultra Creative Suite\{#MyAppName}
DefaultGroupName=Ultra Creative Suite\{#MyAppName}
DisableProgramGroupPage=yes
; Full GPL-3.0 text expected at the monorepo root (see README.md's
; "Licensing" section) - grab it from https://www.gnu.org/licenses/gpl-3.0.txt
; if it isn't already sitting there from one of the other Ultra apps.
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=UltraComposer-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 64-bit only - matches the native AudioEngine, which is built x64-only
; (vcpkg triplet x64-windows-static). Explicit like this from the start,
; rather than discovered as a bug later - see the ArchitecturesAllowed fix
; that had to be retrofitted into Ultra Video Editor's installer.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Add a Serbian entry here later if you install Inno Setup's unofficial
; Serbian (Latin) translation file - listing English first keeps it the
; installer's own default language, matching the app itself now defaulting
; to English.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the build already assembled into the App project's own output
; folder - the exe, UltraComposer.Interop.dll, the native
; UltraComposer.AudioEngine.dll (copied in by the CopyNativeAudioEngine
; MSBuild target), and the bundled default SoundFont bank (copied in by
; CopyBundledSoundFont) all land here, so one recursive copy is enough.
; Double-check this exact path against your own local Release build output
; once - if MSBuild puts it somewhere slightly different on your machine,
; this is the one line to adjust.
Source: "..\src\App\bin\x64\Release\net8.0-windows\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Bilingual (English then Serbian) "what is this, how do I start, where's
; the full guide" one-pager - installed alongside the app and offered on the
; Finished page below, same "instructions.txt with a checkbox" idea common
; to a lot of installers. Kept as its own docs\ file (not generated from the
; in-app User Guide) since this one is meant to be readable before the app
; is even launched, e.g. from outside Windows via a text editor.
Source: "..\UltraComposer\docs\GettingStarted.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Second Finished-page checkbox, same pattern as "Launch <program>" above -
; checked by default (no "unchecked" flag) so the guide actually opens the
; first time, same as Demir asked for; the user can still untick it.
Filename: "notepad.exe"; Parameters: """{app}\GettingStarted.txt"""; Description: "View the Getting Started guide"; Flags: postinstall skipifsilent shellexec nowait

[Code]
// Ultra Composer needs the .NET 8 Desktop Runtime (it's a framework-dependent
// build, not self-contained, same as the other Ultra apps) - this only warns
// if it looks missing rather than blocking setup, since the registry check
// below is a best-effort heuristic, not authoritative.
function IsDotNet8DesktopRuntimeInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Length(Names[I]) > 0) and (Names[I][1] = '8') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet8DesktopRuntimeInstalled() then
  begin
    if MsgBox('Ultra Composer needs the .NET 8 Desktop Runtime, which does not appear to be installed yet.' + #13#10 + #13#10 +
              'Setup will continue, but the app will not start until it is installed. Open the download page now?',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
