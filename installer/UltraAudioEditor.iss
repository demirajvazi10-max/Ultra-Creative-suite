; ══════════════════════════════════════════════════════════════════════════
;  UltraAudioEditor.iss
;  Inno Setup installer for Ultra Audio Editor
;  (The video editor has its own separate .iss, with its own AppId GUID,
;  so both can be installed independently side by side.)
;
;  What it does:
;    1. The user picks the installer language (English/Deutsch/Srpski) on
;       the Welcome page — that same choice becomes the DEFAULT APP
;       LANGUAGE (written to language.cfg, which the app reads on first
;       launch via Localization/Lang.cs).
;    2. Copies the already-built application (.NET 8 self-contained or
;       framework-dependent — adjust the [Files] source below).
;    3. Downloads and silently installs the Ollama installer via curl.exe
;       (built into Windows 10 1803+ / Windows 11, no external
;       dependencies) — ONLY if not already present on the machine.
;    4. Detects GPU/RAM (via a short, PS 5.1-compatible PowerShell command
;       called from Exec) and picks an Ollama text model based on that —
;       same logic as the video editor installer, minus the vision model
;       (the audio editor's AI panel is text-only, see AnthropicService.cs).
;    5. Downloads and silently installs Python 3.12 (WITH pip and PATH
;       both enabled via the official silent-install switches) if no
;       working Python is found — this sidesteps the exact PATH/pip
;       problems that come up when someone installs Python by hand without
;       checking those boxes.
;    6. Runs "py -m pip install numpy demucs" so vocal/instrumental
;       separation (Services/DemucsService.cs) works out of the box — no
;       manual pip commands needed after install, unlike the very first
;       version of this app.
;    7. All errors are shown as understandable messages (MsgBox), not
;       technical stack traces — meant for beginner users too.
;
;  BUILD-TIME DEPENDENCIES (on your machine, not the end user's):
;    - Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;    - Nothing else — download logic runs through curl.exe, which already
;      ships with Windows 10 (1803+) and Windows 11, no external plugin
;      needed.
;
;  BEFORE COMPILING, ADJUST (still needed):
;    - SetupIconFile, if you have an .ico
;    - #define MyAppSourceDir below — verify it matches your current
;      build output folder (the project sits at
;      UltraAudioEditor\UltraAudioEditor\ in the repo, so the bin folder
;      is nested one level deeper than the video editor's).
;    - PYTHON_VERSION below: python.org does not offer a permanent
;      "always latest" download link the way FFmpeg's GitHub releases do
;      (see UltraVideoEditor.iss), so this is a specific, tested version
;      number that will need bumping by hand every so often. If it ever
;      404s, check https://www.python.org/downloads/windows/ for the
;      current stable release and update PYTHON_VERSION.
;
;  CI/GitHub Actions: MyAppVersion and MyAppSourceDir can be overridden
;  from outside via "ISCC /DMyAppVersion=... /DMyAppSourceDir=...
;  UltraAudioEditor.iss" without touching this file — the local default
;  (Debug path below) stays untouched for your manual testing.
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Audio Editor"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Ultra Creative Suite"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraAudioEditor.exe"
#define PythonVersion "3.12.7"

; Folder where your build output lives (.exe + DLLs). The Audio Editor's
; project folder is nested one level deeper than the video editor's
; (UltraAudioEditor\UltraAudioEditor\...), because that's how the .sln
; was created — double-check this against your actual checkout.
#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\UltraAudioEditor\UltraAudioEditor\bin\Debug\net8.0-windows"
#endif

[Setup]
; Own, unique GUID — different from the video editor's, so Windows treats
; them as two separate applications that can both be installed side by
; side instead of one "upgrading"/removing the other.
AppId={{B0B9E815-0FBD-43E1-8FE5-87B5168D2EFE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=UltraAudioEditorSetup-{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Show the language dialog instead of silently auto-detecting — the user
; CONSCIOUSLY chooses, since that choice also becomes the app's language.
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
UninstallDisplayIcon={app}\{#MyAppExeName}
; SetupIconFile=app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"
; Same shared, locally-tracked translation file as the video editor's
; installer (not part of the standard Inno Setup 6.x install) — kept next
; to this .iss so anyone who clones the repo can compile right away.
Name: "serbian"; MessagesFile: "Languages\SerbianLatin.isl"

[Messages]
english.WelcomeLabel2=This will install %1 on your computer, and set up everything needed for AI features and vocal/instrumental separation (Ollama, Python, Demucs).%n%nThe language you choose now will also be the default language of the app.
german.WelcomeLabel2=Dies installiert %1 auf Ihrem Computer und richtet alles ein, was für KI-Funktionen und Gesang-/Instrumental-Trennung (Ollama, Python, Demucs) benötigt wird.%n%nDie hier gewählte Sprache wird auch die Standardsprache der App.
serbian.WelcomeLabel2=Ovo ce instalirati %1 na vas racunar i podesiti sve sto je potrebno za AI funkcije i razdvajanje vokala/instrumentala (Ollama, Python, Demucs).%n%nJezik koji sada izaberete bice i podrazumevani jezik aplikacije.

[CustomMessages]
english.DesktopIconGroup=Additional icons:
german.DesktopIconGroup=Zusätzliche Symbole:
serbian.DesktopIconGroup=Dodatne ikone:

english.DesktopIconTaskName=Create a desktop icon
german.DesktopIconTaskName=Ein Desktopsymbol erstellen
serbian.DesktopIconTaskName=Napravi ikonu na radnoj povrsini

[Tasks]
; Unlike the video editor's optional Whisper task, EVERYTHING here
; (Ollama + Python + Demucs/numpy/torch) installs automatically, no
; opt-in checkbox — matches what was asked: the app should work
; out of the box, no separate manual pip/install steps afterward.
Name: "desktopicon"; Description: "{cm:DesktopIconTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"

[Files]
; *.pdb — debug symbols, not needed by end users.
; runtimes\ios*, runtimes\linux*, runtimes\osx*, runtimes\android*
;   — non-Windows native binaries some NuGet packages pull in for every
;     platform by default (this app only ever runs on Windows).
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]

// ────────────────────────────────────────────────────────────────────────
//  Language: installer -> app
// ────────────────────────────────────────────────────────────────────────

function AppLanguageCode(): String;
begin
  case ActiveLanguage() of
    'german':  Result := 'de';
    'serbian': Result := 'sr';
  else
    Result := 'en';
  end;
end;

// ────────────────────────────────────────────────────────────────────────
//  Status page for long-running steps (downloading/installing dependencies)
// ────────────────────────────────────────────────────────────────────────

var
  StatusPage: TOutputProgressWizardPage;
  GpuType: String;       // 'nvidia' | 'amd' | 'intel' | 'cpu'
  VramGB: Integer;
  RamGB: Integer;
  OllamaQueryModel: String;
  CurlExitCode: Integer; // last curl.exe ResultCode, for diagnostic MsgBoxes below
  PythonExe: String;     // resolved path/command that actually works ('py' or a full path), '' if none found

// ────────────────────────────────────────────────────────────────────────
//  GPU/RAM detection — identical approach to UltraVideoEditor.iss (kept in
//  sync manually since these are two independent .iss files; if you change
//  one, change the other too).
// ────────────────────────────────────────────────────────────────────────
procedure DetectGpuAndRam();
var
  ResultCode: Integer;
  InfoFile: String;
  Lines: TArrayOfString;
  PsCommand: String;
  P1, P2: Integer;
  Rest: String;
begin
  GpuType := 'cpu';
  VramGB := 0;
  RamGB := 8;

  InfoFile := ExpandConstant('{tmp}\gpuinfo.txt');

  PsCommand :=
    '$g = Get-CimInstance Win32_VideoController | Where-Object { $_.AdapterRAM -gt 0 } | Select-Object -First 1; ' +
    '$name = "none"; $vram = 0; ' +
    'if ($g) { $name = $g.Name; $vram = [math]::Round($g.AdapterRAM/1GB,0) }; ' +
    '$os = Get-CimInstance Win32_OperatingSystem; ' +
    '$ram = [math]::Round($os.TotalVisibleMemorySize/1MB,0); ' +
    '"$name|$vram|$ram" | Out-File -FilePath "' + InfoFile + '" -Encoding ASCII';

  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "' + PsCommand + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if LoadStringsFromFile(InfoFile, Lines) and (GetArrayLength(Lines) > 0) then
  begin
    if Pos('NVIDIA', Lines[0]) > 0 then GpuType := 'nvidia'
    else if Pos('GeForce', Lines[0]) > 0 then GpuType := 'nvidia'
    else if Pos('Radeon', Lines[0]) > 0 then GpuType := 'amd'
    else if Pos('AMD', Lines[0]) > 0 then GpuType := 'amd'
    else if Pos('Intel', Lines[0]) > 0 then GpuType := 'intel';

    P1 := Pos('|', Lines[0]);
    if P1 > 0 then
    begin
      Rest := Copy(Lines[0], P1 + 1, Length(Lines[0]) - P1);
      P2 := Pos('|', Rest);
      if P2 > 0 then
      begin
        VramGB := StrToIntDef(Copy(Rest, 1, P2 - 1), 0);
        RamGB := StrToIntDef(Copy(Rest, P2 + 1, Length(Rest) - P2), 8);
      end;
    end;
  end;
end;

// Same thresholds as UltraVideoEditor.iss's OllamaQueryModel choice — no
// vision model here, the audio editor's AI panel is text-only.
procedure ChooseOllamaModel();
begin
  OllamaQueryModel := 'qwen2.5:3b';

  if GpuType = 'nvidia' then
  begin
    if VramGB >= 8 then OllamaQueryModel := 'qwen2.5:14b'
    else if VramGB >= 4 then OllamaQueryModel := 'qwen2.5:7b'
    else OllamaQueryModel := 'qwen2.5:3b';
  end
  else if (GpuType = 'amd') or (GpuType = 'intel') then
    OllamaQueryModel := 'qwen2.5:7b'
  else // cpu only
  begin
    if RamGB >= 16 then OllamaQueryModel := 'qwen2.5:7b'
    else if RamGB >= 8 then OllamaQueryModel := 'qwen2.5:3b'
    else OllamaQueryModel := 'tinyllama';
  end;
end;

// ────────────────────────────────────────────────────────────────────────
//  Shared helpers (curl download + error reason) — same as
//  UltraVideoEditor.iss.
// ────────────────────────────────────────────────────────────────────────
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

// ────────────────────────────────────────────────────────────────────────
//  Python detection — mirrors DemucsService.cs's FindPython() candidate
//  order exactly (py launcher and known real install paths BEFORE bare
//  "python"/"python3", to dodge the Microsoft Store WindowsApps stub that
//  caused a real, confusing bug earlier in development: it "exists" and
//  passes --version, but has no pip/packages in it at all).
// ────────────────────────────────────────────────────────────────────────
function TryPython(Candidate: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Candidate, '--version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function FindWorkingPython(): String;
begin
  Result := '';
  if TryPython('py') then begin Result := 'py'; Exit; end;
  if TryPython(ExpandConstant('{localappdata}\Programs\Python\Python312\python.exe')) then
  begin Result := ExpandConstant('{localappdata}\Programs\Python\Python312\python.exe'); Exit; end;
  if TryPython(ExpandConstant('{localappdata}\Programs\Python\Python311\python.exe')) then
  begin Result := ExpandConstant('{localappdata}\Programs\Python\Python311\python.exe'); Exit; end;
  if TryPython('C:\Python312\python.exe') then begin Result := 'C:\Python312\python.exe'; Exit; end;
  if TryPython('C:\Python311\python.exe') then begin Result := 'C:\Python311\python.exe'; Exit; end;
end;

procedure InstallDependencies();
var
  ResultCode: Integer;
  OllamaExe: String;
begin
  OllamaExe := ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe');

  StatusPage.SetText('Detecting your hardware...', '');
  StatusPage.Show;
  try
    DetectGpuAndRam();
    ChooseOllamaModel();

    // ── Ollama ────────────────────────────────────────────────────────
    if not FileExists(OllamaExe) then
    begin
      StatusPage.SetText('Downloading Ollama...', 'This may take a moment (~100 MB)');
      if not DownloadFileCurl('https://ollama.com/download/OllamaSetup.exe',
                               ExpandConstant('{tmp}\OllamaSetup.exe')) then
        MsgBox('Could not download Ollama (' + CurlErrorReason(CurlExitCode) + '). AI features will be unavailable until you install it manually from ollama.com.', mbInformation, MB_OK)
      else
      begin
        StatusPage.SetText('Installing Ollama...', '');
        Exec(ExpandConstant('{tmp}\OllamaSetup.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(3000);
      end;
    end;

    if FileExists(OllamaExe) then
    begin
      StatusPage.SetText('Downloading AI model for your hardware (' + OllamaQueryModel + ')...',
                          'This can take several minutes depending on your internet speed.');
      Exec(OllamaExe, 'pull ' + OllamaQueryModel, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox('The AI model could not be downloaded right now (no internet connection?). ' +
               'The app will still work — go to Settings after install and run: ollama pull ' + OllamaQueryModel,
               mbInformation, MB_OK);
    end
    else
      MsgBox('Ollama could not be installed automatically. AI features will use cloud providers (Groq/Anthropic, if you add a key) until you install Ollama manually from ollama.com.', mbInformation, MB_OK);

    // ── Python + numpy + Demucs (vocal/instrumental separation) ────────
    StatusPage.SetText('Checking for Python...', '');
    PythonExe := FindWorkingPython();

    if PythonExe = '' then
    begin
      StatusPage.SetText('Downloading Python ' + '{#PythonVersion}' + '...', 'This may take a moment (~25 MB)');
      if not DownloadFileCurl('https://www.python.org/ftp/python/{#PythonVersion}/python-{#PythonVersion}-amd64.exe',
                               ExpandConstant('{tmp}\python_installer.exe')) then
        MsgBox('Could not download Python (' + CurlErrorReason(CurlExitCode) + '). ' +
               'Vocal/instrumental separation (Demucs) will be unavailable until you install Python manually from python.org — ' +
               'make sure to check "Add python.exe to PATH" and "pip" during setup.', mbInformation, MB_OK)
      else
      begin
        StatusPage.SetText('Installing Python...', 'This includes pip and adds Python to PATH automatically.');
        // InstallAllUsers=1 + PrependPath=1 + Include_pip=1: does, silently and
        // correctly, exactly what a person clicking through the installer by
        // hand has to remember to check themselves — this is exactly the step
        // that caused real confusion during development when done manually.
        Exec(ExpandConstant('{tmp}\python_installer.exe'),
             '/quiet InstallAllUsers=1 PrependPath=1 Include_pip=1 Include_test=0 Include_launcher=1',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(2000);
        PythonExe := FindWorkingPython();
      end;
    end;

    if PythonExe <> '' then
    begin
      StatusPage.SetText('Setting up vocal/instrumental separation (Demucs)...',
                          'This downloads PyTorch too — a large download (1-2 GB), please be patient.');
      // ensurepip first: covers the (rare but real) case of a Python install
      // that somehow has no pip yet, same fix used manually during testing.
      Exec(PythonExe, '-m ensurepip --upgrade', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(PythonExe, '-m pip install --upgrade numpy demucs', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox('Demucs could not be installed automatically (no internet connection, or a network hiccup during a large download). ' +
               'The app will still work — vocal/instrumental separation will be unavailable until you run this yourself in a terminal: ' +
               'py -m pip install numpy demucs', mbInformation, MB_OK);
    end
    else
      MsgBox('Python could not be installed automatically. Vocal/instrumental separation (Demucs) will be unavailable until you install Python manually from python.org (check "Add python.exe to PATH" and "pip"), then run: py -m pip install numpy demucs', mbInformation, MB_OK);

    // ── Language sync: installer -> app ──────────────────────────────────
    StatusPage.SetText('Finishing up...', '');
    SaveStringToFile(ExpandConstant('{app}\language.cfg'), AppLanguageCode(), False);

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
  StatusPage := CreateOutputProgressPage('Setting up Ultra Audio Editor', 'Please wait while dependencies are installed.');
end;
