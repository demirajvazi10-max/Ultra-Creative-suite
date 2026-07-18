; ══════════════════════════════════════════════════════════════════════════
;  UltraVideoEditor.iss
;  Inno Setup installer for Ultra Video Editor
;  (The audio editor has its own separate .iss, with its own AppId GUID,
;  so both can be installed independently side by side.)
;
;  What it does:
;    1. The user picks the installer language (English/Deutsch/Srpski) on
;       the Welcome page — that same choice becomes the DEFAULT APP
;       LANGUAGE (written to language.cfg, which the app reads on first
;       launch).
;    2. Copies the already-built application (.NET 8 self-contained or
;       framework-dependent — adjust the [Files] source below).
;    3. Downloads FFmpeg, VLC, and the Ollama installer via curl.exe
;       (built into Windows 10 1803+ / Windows 11, no external
;       dependencies) — ONLY what isn't already present on the machine.
;    4. Detects GPU/RAM (via a short, PS 5.1-compatible PowerShell command
;       called from Exec — not a separate .ps1 file) and picks an Ollama
;       model based on that.
;    5. Installs Ollama silently (/VERYSILENT /SUPPRESSMSGBOXES
;       /NORESTART — Inno Setup convention, not NSIS's /S) and "pulls"
;       the chosen model.
;    6. All errors are shown as understandable messages (MsgBox), not
;       technical stack traces — meant for beginner users too.
;
;  BUILD-TIME DEPENDENCIES (on your machine, not the end user's):
;    - Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;    - Nothing else — download logic runs through curl.exe, which already
;      ships with Windows 10 (1803+) and Windows 11, no external plugin
;      needed.
;
;  BEFORE COMPILING, ADJUST (still needed):
;    - AppId GUID (generate your own: Tools > Generate GUID in the Inno
;      Setup IDE)
;    - SetupIconFile, if you have an .ico
;    - #define MyAppSourceDir is already filled in (see the Debug/Release
;      note below), but verify it matches your current build.
;
;  CI/GitHub Actions: MyAppVersion and MyAppSourceDir can be overridden
;  from outside via "ISCC /DMyAppVersion=... /DMyAppSourceDir=...
;  UltraVideoEditor.iss" without touching this file — the local default
;  (Debug path above) stays untouched for your manual testing.
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Video Editor"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Ultra Creative Suite"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraVideoEditor.exe"

; Folder where your build output lives (.exe + DLLs).
;
; NOTE: the path given below is a Debug build
; (bin\Debug\net8.0-windows) — that's fine for testing the installer
; itself, but for a REAL release the recommendation is to do Build >
; Rebuild in the Release configuration before final packaging: a Debug
; build is unoptimized (slower for the end user) and carries extra .pdb
; debug symbols that nobody outside of you needs. When you switch to
; Release, this is the only line you need to change (just "Debug" ->
; "Release" in the path below).
#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\UltraVideoEditor\bin\Debug\net8.0-windows"
#endif

[Setup]
; IMPORTANT: this is still the same placeholder GUID from when this
; script was called UltraCreativeSuite.iss. Now that the Video and Audio
; editors are becoming TWO SEPARATE installers, generate a NEW, unique
; GUID here (Tools > Generate GUID in the Inno Setup IDE) — the audio
; editor needs its OWN, different GUID. If both installers share the
; same AppId, Windows will treat them as the SAME application (the
; second one will "upgrade" / remove the first instead of both living
; side by side).
AppId={{A1B2C3D4-E5F6-4A7B-9C8D-1234567890AB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=UltraVideoEditorSetup-{#MyAppVersion}
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
; SerbianLatin.isl does NOT ship with the standard Inno Setup 6.x
; install (it's an "unofficial" translation, only available in source
; form on GitHub) — so we keep it locally, in Languages\ next to this
; .iss file, instead of referencing a "compiler:..." path that doesn't
; exist on every machine. This also has the advantage that the file is
; tracked in git alongside the .iss, so anyone who clones the repo can
; compile right away without manually hunting down the translation.
Name: "serbian"; MessagesFile: "Languages\SerbianLatin.isl"

[Messages]
; Short messages shown during dependency download/install, translated
; for all three installer languages (this does not touch the app's own
; LanguageManager — this is ONLY text inside the installer wizard).
english.WelcomeLabel2=This will install %1 on your computer, and set up the AI features (FFmpeg, VLC, Ollama) needed for video editing.%n%nThe language you choose now will also be the default language of the app.
german.WelcomeLabel2=Dies installiert %1 auf Ihrem Computer und richtet die KI-Funktionen (FFmpeg, VLC, Ollama) ein, die für die Videobearbeitung benötigt werden.%n%nDie hier gewählte Sprache wird auch die Standardsprache der App.
serbian.WelcomeLabel2=Ovo ce instalirati %1 na vas racunar i podesiti AI funkcije (FFmpeg, VLC, Ollama) potrebne za video montazu.%n%nJezik koji sada izaberete bice i podrazumevani jezik aplikacije.

[CustomMessages]
english.WhisperTaskName=Install AI transcription/subtitles feature (~1.3 GB download)
german.WhisperTaskName=KI-Transkriptions-/Untertitelfunktion installieren (~1,3 GB Download)
serbian.WhisperTaskName=Instaliraj AI transkripciju/titlove (preuzimanje ~1.3 GB)

english.DesktopIconGroup=Additional icons:
german.DesktopIconGroup=Zusätzliche Symbole:
serbian.DesktopIconGroup=Dodatne ikone:

english.DesktopIconTaskName=Create a desktop icon
german.DesktopIconTaskName=Ein Desktopsymbol erstellen
serbian.DesktopIconTaskName=Napravi ikonu na radnoj povrsini

[Tasks]
; Optional, unchecked by default: the transcription/subtitle engine
; (Faster-Whisper-XXL) is ~1.3 GB on its own — bigger than the rest of the
; installer combined — so unlike FFmpeg/VLC/Ollama it is NOT downloaded
; silently for everyone. The user consciously opts in here. See
; InstallDependencies below for the actual download/extract logic.
Name: "whisper"; Description: "{cm:WhisperTaskName}"; Flags: unchecked

; Standard Inno pattern: desktop icon is opt-in via checkbox, Start Menu
; entry stays unconditional (see [Icons] below — only the desktop one
; carries "Tasks: desktopicon").
Name: "desktopicon"; Description: "{cm:DesktopIconTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"

[Files]
; Excludes explained:
;   *.pdb                    — debug symbols, not needed by end users.
;   ffmpeg.exe / ffplay.exe / ffprobe.exe
;                             — these get downloaded separately during
;                               install (see InstallDependencies below).
;                               Any copies present here are just local
;                               testing leftovers and must not be bundled.
;   _xxl_data                — the ENTIRE unpacked Faster-Whisper-XXL
;                               standalone bundle (torch, cudnn/cublas/
;                               cusparse/cufft/cusolver, onnxruntime,
;                               ctranslate2, scipy, numpy, llvmlite, model
;                               assets — hundreds of files, several GB) from
;                               a local test run of faster-whisper-xxl.exe.
;                               Trying to exclude these one filename at a
;                               time (an earlier version of this script did
;                               that) is a losing battle — new files show up
;                               with every different CUDA/cuDNN version.
;                               Excluding the whole folder by name is exact
;                               and version-proof: Inno matches a
;                               no-backslash pattern against the name at ANY
;                               depth with recursesubdirs, so this skips
;                               "_xxl_data" wherever it appears, no matter
;                               what's inside it.
;   faster-whisper-xxl.exe   — the standalone exe from that same local test
;                               run, sitting directly in the build output
;                               root. The app finds and calls an externally
;                               installed Whisper executable at runtime (see
;                               AITranscription.cs -> FindWhisperExecutable);
;                               the REAL copy now gets installed by the
;                               optional "whisper" task below (into
;                               {app}\Whisper\), so this root-level one is
;                               just a leftover and must not be bundled too.
;   runtimes\ios*, runtimes\linux*, runtimes\osx*, runtimes\android*
;                             — non-Windows native binaries that NuGet
;                               packages (Magick.NET, onnxruntime, etc.)
;                               pull in for every platform by default. A
;                               plain local "dotnet build" (not publish -r
;                               win-x64) keeps all of them; this app only
;                               ever runs on Windows, so none are needed.
;                               These use a backslash in the pattern, which
;                               Inno matches as a path relative to the
;                               source folder (not a bare filename), so it
;                               only strips these specific runtime
;                               subfolders, not any legitimately-named
;                               "linux..." file elsewhere.
;   _models                  — downloaded AI model weights from that same
;                               local test run (Faster-Whisper-XXL stores
;                               models it fetches in a "_models" folder next
;                               to itself by default). These get pulled on
;                               whoever's machine actually runs the app, same
;                               as the Ollama models — never belongs bundled.
;   Assets                   — the ambient sound/SFX library (Assets\Sounds,
;                               Assets\SFX). Left out of the installer on
;                               purpose: even after wav->ogg conversion this
;                               is still a sizeable, fully optional add-on
;                               with no effect on core editing. It's
;                               distributed separately (see release notes /
;                               README for the download link) instead of
;                               being bundled or auto-downloaded here.
;                               LocalSoundLibrary.cs already Directory.Exists
;                               -checks before scanning, so the app runs
;                               fine with this folder simply absent.
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,ffmpeg.exe,ffplay.exe,ffprobe.exe,_xxl_data,_models,faster-whisper-xxl.exe,Assets,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]

// ────────────────────────────────────────────────────────────────────────
//  Language: installer -> app
// ────────────────────────────────────────────────────────────────────────

// Maps the internal Inno language name (from [Languages] Name:) to the
// two-letter code the app's LanguageManager expects (en/sr/de) — same
// codes as in MainWindow.xaml.cs (_currentLanguage) and language.cfg.
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
  OllamaVisionModel: String;
  CurlExitCode: Integer; // last curl.exe ResultCode, for diagnostic MsgBoxes below

// ────────────────────────────────────────────────────────────────────────
//  GPU/RAM detection
//
//  Deliberately ONE short PowerShell command called through Exec (not a
//  separate .ps1 file) — avoids a whole class of problems we ran into
//  earlier (PS 5.1 vs 7 compatibility), since every token here is
//  verified to work on plain Windows PowerShell 5.1 (no "?.", no "??",
//  no ternary "? :"). The result is written to a plain text file
//  "gpuinfo.txt" in the TEMP folder, which Pascal Script then reads —
//  no JSON parsing, just 3 lines of text.
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

  // Everything on one line, no "?." / "??" / inline ternary — works on
  // PS 5.1 too. We deliberately round VRAM/RAM to a WHOLE number
  // (Round(...,0)) — not to one decimal like the old .ps1 — because
  // Pascal Script's StrToIntDef doesn't parse decimal notation ("8.5"),
  // and here we only need thresholds (>=4, >=8, >=16).
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
    // Lines[0] looks like: "NVIDIA GeForce RTX 3060|12|32"
    if Pos('NVIDIA', Lines[0]) > 0 then GpuType := 'nvidia'
    else if Pos('GeForce', Lines[0]) > 0 then GpuType := 'nvidia'
    else if Pos('Radeon', Lines[0]) > 0 then GpuType := 'amd'
    else if Pos('AMD', Lines[0]) > 0 then GpuType := 'amd'
    else if Pos('Intel', Lines[0]) > 0 then GpuType := 'intel';

    // Parse "name|vram|ram" using only Pos/Copy (no LastDelimiter, which
    // isn't guaranteed to be available in Inno's Pascal Script).
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

// Same logic as in the old UltraInstaller.ps1 — just rewritten in Pascal.
procedure ChooseOllamaModels();
begin
  OllamaQueryModel := 'qwen2.5:3b';
  OllamaVisionModel := 'moondream';

  if GpuType = 'nvidia' then
  begin
    if VramGB >= 8 then begin OllamaQueryModel := 'qwen2.5:14b'; OllamaVisionModel := 'qwen2.5vl:7b'; end
    else if VramGB >= 4 then begin OllamaQueryModel := 'qwen2.5:7b'; OllamaVisionModel := 'qwen2.5vl:7b'; end
    else begin OllamaQueryModel := 'qwen2.5:3b'; OllamaVisionModel := 'moondream'; end;
  end
  else if GpuType = 'amd' then
  begin
    OllamaQueryModel := 'qwen2.5:7b'; OllamaVisionModel := 'qwen2.5vl:7b';
  end
  else if GpuType = 'intel' then
  begin
    OllamaQueryModel := 'qwen2.5:7b'; OllamaVisionModel := 'moondream';
  end
  else // cpu only
  begin
    if RamGB >= 16 then begin OllamaQueryModel := 'qwen2.5:7b'; OllamaVisionModel := 'moondream'; end
    else if RamGB >= 8 then begin OllamaQueryModel := 'qwen2.5:3b'; OllamaVisionModel := 'moondream'; end
    else begin OllamaQueryModel := 'tinyllama'; OllamaVisionModel := 'moondream'; end;
  end;
end;

// ────────────────────────────────────────────────────────────────────────
//  Dependency installation — called from CurStepChanged(ssPostInstall)
// ────────────────────────────────────────────────────────────────────────

// Recursively searches RootDir for FileNameToFind and copies the FIRST
// match found to DestPath. Pascal Script has no built-in
// "Get-ChildItem -Recurse", so this manually walks subfolders via
// FindFirst/FindNext.
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

// Downloads a file via curl.exe (built into Windows 10 1803+/Windows 11
// — no external dependencies, unlike the Inno Download Plugin we tried
// first and ran into trouble with around idp.dll distribution).
// Returns True if the file is actually present on disk after the call.
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

// Turns curl's numeric exit code into a short, human-readable reason, so the
// MsgBoxes below can tell the user (and us, when they screenshot it) whether
// this was really "no internet" (exit 6/7/28) or something else entirely,
// most commonly a stale/dead URL returning 404 (exit 22, since we pass -f).
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

// Recursively searches RootDir for a file named FileNameToFind and returns
// the path of the FOLDER that directly contains it (not the file itself).
// Used for Faster-Whisper-XXL: unlike ffmpeg.exe (statically self-contained),
// faster-whisper-xxl.exe is a PyInstaller bundle that needs every DLL sitting
// next to it in the same folder — so we need the folder, to copy it whole.
function FindContainingFolder(RootDir, FileNameToFind: String): String;
var
  FindRec: TFindRec;
  FullPath, SubResult: String;
begin
  Result := '';
  if FindFirst(RootDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          FullPath := RootDir + '\' + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            SubResult := FindContainingFolder(FullPath, FileNameToFind);
            if SubResult <> '' then
              Result := SubResult;
          end
          else if CompareText(FindRec.Name, FileNameToFind) = 0 then
            Result := RootDir;
        end;
      until (not FindNext(FindRec)) or (Result <> '');
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Recursively copies every file and subfolder from SrcDir into DestDir,
// preserving structure. Pascal Script has no built-in "copy folder"
// function, so this walks it manually via FindFirst/FindNext — same
// principle as Copy-Item -Recurse in the old .ps1.
procedure CopyDirRecursive(SrcDir, DestDir: String);
var
  FindRec: TFindRec;
  SrcPath, DestPath: String;
begin
  ForceDirectories(DestDir);
  if FindFirst(SrcDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          SrcPath := SrcDir + '\' + FindRec.Name;
          DestPath := DestDir + '\' + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CopyDirRecursive(SrcPath, DestPath)
          else
            FileCopy(SrcPath, DestPath, False);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// VideoLAN does not publish a version-less direct download link (every
// filename embeds the version, e.g. "vlc-3.0.23-win64.exe"), and that
// version changes over time — which is exactly what broke the previous
// hardcoded "vlc-3.0.21-win64.exe" link (real current version had already
// moved to 3.0.23, so curl got a 404). Instead of hardcoding a version, we
// download the "last/win64" folder's HTML directory listing and pull the
// current filename out of it with plain Pos/Copy — no regex needed, and
// this never goes stale again.
function GetLatestVlcFilename(): String;
var
  ListingPath: String;
  Content: AnsiString;
  SearchFrom, StartPos, TailPos: Integer;
begin
  Result := '';
  ListingPath := ExpandConstant('{tmp}\vlc_listing.html');
  if DownloadFileCurl('https://get.videolan.org/vlc/last/win64/', ListingPath) then
  begin
    if LoadStringFromFile(ListingPath, Content) then
    begin
      // Walk through every "vlc-" occurrence in the listing, not just the
      // first one — the directory is alphabetical, so the first "vlc-"
      // entry is usually "vlc-<ver>-win64-debugsym.7z", not the .exe we
      // want. Keep advancing until a "vlc-" is followed by "-win64.exe"
      // within a short window (VLC version strings are always short, e.g.
      // "3.0.23"), or we run out of content.
      SearchFrom := 1;
      while SearchFrom <= Length(Content) do
      begin
        StartPos := Pos('vlc-', Copy(Content, SearchFrom, Length(Content) - SearchFrom + 1));
        if StartPos = 0 then Break;
        StartPos := SearchFrom + StartPos - 1; // turn relative into absolute position
        TailPos := Pos('-win64.exe', Copy(Content, StartPos, 30));
        if TailPos > 0 then
        begin
          Result := Copy(Content, StartPos, TailPos + 9); // +9 = Length('-win64.exe') - 1
          Break;
        end;
        SearchFrom := StartPos + 4; // move past this "vlc-" and keep looking
      end;
    end;
  end;
end;

procedure InstallDependencies();
var
  ResultCode: Integer;
  OllamaExe: String;
  FfmpegDestDir: String;
  VlcPath, VlcPath86: String;
  VlcFilename: String;
  WhisperSrcFolder: String;
begin
  FfmpegDestDir := ExpandConstant('{app}\Ffmpeg');
  VlcPath   := ExpandConstant('{pf}\VideoLAN\VLC\vlc.exe');
  VlcPath86 := ExpandConstant('{pf32}\VideoLAN\VLC\vlc.exe');
  OllamaExe := ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe');

  StatusPage.SetText('Detecting your hardware...', '');
  StatusPage.Show;
  try
    DetectGpuAndRam();
    ChooseOllamaModels();

    // ── Download whatever is missing, one at a time, via curl.exe ───────
    if not FileExists(FfmpegDestDir + '\ffmpeg.exe') then
    begin
      StatusPage.SetText('Downloading FFmpeg...', 'This may take a moment (~100 MB)');
      // Uses BtbN's "latest" release tag with a fixed asset name — this URL
      // is permanent and always points at the newest build, unlike a dated
      // "autobuild-2024-12-01-12-55" tag, which gets purged after a while
      // (BtbN only keeps the last 14 daily builds + one build per month for
      // two years) and then 404s forever, which is exactly what happened.
      if not DownloadFileCurl('https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip',
                               ExpandConstant('{tmp}\ffmpeg.zip')) then
        MsgBox('Could not download FFmpeg (' + CurlErrorReason(CurlExitCode) + '). Download it manually from ffmpeg.org and place ffmpeg.exe in: ' + FfmpegDestDir, mbInformation, MB_OK);
    end;

    if not (FileExists(VlcPath) or FileExists(VlcPath86)) then
    begin
      StatusPage.SetText('Downloading VLC...', 'This may take a moment (~45 MB)');
      // VideoLAN doesn't offer a version-less direct link, so we resolve
      // the current filename from the directory listing first (see
      // GetLatestVlcFilename above) instead of hardcoding a version number
      // that will eventually go stale, like "vlc-3.0.21-win64.exe" did.
      VlcFilename := GetLatestVlcFilename();
      if VlcFilename = '' then
        VlcFilename := 'vlc-3.0.23-win64.exe'; // fallback if the listing couldn't be parsed
      if not DownloadFileCurl('https://get.videolan.org/vlc/last/win64/' + VlcFilename,
                               ExpandConstant('{tmp}\vlc_installer.exe')) then
        MsgBox('Could not download VLC (' + CurlErrorReason(CurlExitCode) + '). You can install it manually from videolan.org afterwards.', mbInformation, MB_OK);
    end;

    if not FileExists(OllamaExe) then
    begin
      StatusPage.SetText('Downloading Ollama...', 'This may take a moment (~100 MB)');
      if not DownloadFileCurl('https://ollama.com/download/OllamaSetup.exe',
                               ExpandConstant('{tmp}\OllamaSetup.exe')) then
        MsgBox('Could not download Ollama (' + CurlErrorReason(CurlExitCode) + '). AI features will be unavailable until you install it manually from ollama.com.', mbInformation, MB_OK);
    end;

    // ── Unpack/install what was just downloaded ──────────────────────────
    if not FileExists(FfmpegDestDir + '\ffmpeg.exe') then
    begin
      StatusPage.SetText('Installing FFmpeg...', '');
      ForceDirectories(FfmpegDestDir);
      ForceDirectories(ExpandConstant('{tmp}\ffmpeg_extract'));
      // tar.exe ships built into Windows from version 1803 onward and
      // can unpack .zip — replaces Expand-Archive from the old .ps1.
      Exec(ExpandConstant('{sys}\tar.exe'),
           '-xf "' + ExpandConstant('{tmp}\ffmpeg.zip') + '" -C "' + ExpandConstant('{tmp}\ffmpeg_extract') + '"',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      // The FFmpeg zip has one subfolder (e.g.
      // ffmpeg-...-win64-gpl-7.1\bin\). FindFirst/FindNext recursively
      // looks for ffmpeg.exe/ffprobe.exe — same principle as
      // "Get-ChildItem -Recurse -Filter ffmpeg.exe" in the old .ps1,
      // just in Pascal Script.
      CopyFirstMatchRecursive(ExpandConstant('{tmp}\ffmpeg_extract'), 'ffmpeg.exe', FfmpegDestDir + '\ffmpeg.exe');
      CopyFirstMatchRecursive(ExpandConstant('{tmp}\ffmpeg_extract'), 'ffprobe.exe', FfmpegDestDir + '\ffprobe.exe');

      if not FileExists(FfmpegDestDir + '\ffmpeg.exe') then
        MsgBox('FFmpeg could not be installed automatically. Download it manually from ffmpeg.org and place ffmpeg.exe in: ' + FfmpegDestDir, mbInformation, MB_OK);
    end;

    if not (FileExists(VlcPath) or FileExists(VlcPath86)) then
    begin
      StatusPage.SetText('Installing VLC...', '');
      Exec(ExpandConstant('{tmp}\vlc_installer.exe'), '/S', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if not (FileExists(VlcPath) or FileExists(VlcPath86)) then
        MsgBox('VLC could not be confirmed installed. You can install it manually from videolan.org — the app will still work, but video preview needs VLC.', mbInformation, MB_OK);
    end;

    if not FileExists(OllamaExe) then
    begin
      StatusPage.SetText('Installing Ollama...', '');
      Exec(ExpandConstant('{tmp}\OllamaSetup.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(3000);
      if not FileExists(OllamaExe) then
      begin
        MsgBox('Ollama could not be installed automatically. AI features will be unavailable until you install it manually from ollama.com. Everything else will still work.', mbInformation, MB_OK);
        Exit; // no point trying to "pull" without ollama.exe
      end;
    end;

    // ── Model pull (Exec, not a file at a URL — this is a CLI call to Ollama) ──
    StatusPage.SetText('Downloading AI model for your hardware (' + OllamaQueryModel + ')...',
                        'This can take several minutes depending on your internet speed.');
    Exec(OllamaExe, 'pull ' + OllamaQueryModel, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      MsgBox('The AI model could not be downloaded right now (no internet connection?). ' +
             'The app will still work — go to Help menu after install and run: ollama pull ' + OllamaQueryModel,
             mbInformation, MB_OK);

    StatusPage.SetText('Downloading vision AI model (' + OllamaVisionModel + ')...', '');
    Exec(OllamaExe, 'pull ' + OllamaVisionModel, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // ── Optional: AI transcription/subtitles engine (Faster-Whisper-XXL) ──
    // Opt-in only (see [Tasks]) — at ~1.3 GB it's bigger than everything
    // else in this installer combined, so it is not downloaded silently.
    //
    // The archive is .7z, not .zip, so tar.exe (used for FFmpeg above)
    // can't unpack it. Instead we bootstrap with 7zr.exe — a small,
    // standalone, no-install-needed .exe from 7-zip.org that can extract
    // .7z archives by itself (this sidesteps the chicken-and-egg problem
    // of needing a 7z tool to unpack a 7z-packaged 7z tool).
    if IsTaskSelected('whisper') then
    begin
      StatusPage.SetText('Downloading 7-Zip extractor...', '');
      if DownloadFileCurl('https://www.7-zip.org/a/7zr.exe', ExpandConstant('{tmp}\7zr.exe')) then
      begin
        StatusPage.SetText('Downloading AI transcription engine (Faster-Whisper-XXL)...',
                            'This is a large download (~1.3 GB) and can take a while.');
        if DownloadFileCurl('https://github.com/Purfview/whisper-standalone-win/releases/download/Faster-Whisper-XXL/Faster-Whisper-XXL_r245.4_windows.7z',
                             ExpandConstant('{tmp}\whisper.7z')) then
        begin
          StatusPage.SetText('Extracting AI transcription engine...', 'This may take a few minutes.');
          ForceDirectories(ExpandConstant('{tmp}\whisper_extract'));
          Exec(ExpandConstant('{tmp}\7zr.exe'),
               'x "' + ExpandConstant('{tmp}\whisper.7z') + '" -o"' + ExpandConstant('{tmp}\whisper_extract') + '" -y',
               '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

          // faster-whisper-xxl.exe needs every DLL sitting next to it, so we
          // find its folder and copy the WHOLE thing, not just the .exe.
          WhisperSrcFolder := FindContainingFolder(ExpandConstant('{tmp}\whisper_extract'), 'faster-whisper-xxl.exe');
          if WhisperSrcFolder <> '' then
          begin
            StatusPage.SetText('Installing AI transcription engine...', '');
            CopyDirRecursive(WhisperSrcFolder, ExpandConstant('{app}\Whisper'));
          end;

          if not FileExists(ExpandConstant('{app}\Whisper\faster-whisper-xxl.exe')) then
            MsgBox('AI transcription could not be installed automatically. You can download it manually from: ' +
                   'https://github.com/Purfview/whisper-standalone-win/releases and extract it into: ' +
                   ExpandConstant('{app}\Whisper'), mbInformation, MB_OK);
        end
        else
          MsgBox('Could not download the AI transcription engine (no internet connection, or the download was interrupted — it is a large file). ' +
                 'You can install it later from: https://github.com/Purfview/whisper-standalone-win/releases', mbInformation, MB_OK);
      end
      else
        MsgBox('Could not download the extraction tool needed for AI transcription setup. ' +
               'You can install the transcription feature manually later — see the app''s Help menu.', mbInformation, MB_OK);
    end;

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
  StatusPage := CreateOutputProgressPage('Setting up Ultra Video Editor', 'Please wait while dependencies are installed.');
end;
