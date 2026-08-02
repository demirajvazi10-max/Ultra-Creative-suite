; ══════════════════════════════════════════════════════════════════════════
;  UltraStudio.iss
;  Inno Setup installer for Ultra Studio
;  (Own AppId GUID — installs independently alongside Video/Audio Editor.)
;
;  What it does automatically:
;    1. Language choice at Welcome page becomes the app's default language.
;    2. Copies the built application.
;    3. Downloads/installs Ollama + a vision AI model sized to the detected
;       hardware (GPU/RAM) — for AI image description and suggestions.
;    4. OPTIONAL (checkbox, unchecked by default — see rationale below):
;       downloads SAM (Segment Anything) ONNX models for precise object
;       extraction ("extract the child from this photo").
;
;  Why SAM is opt-in, not automatic like Ollama:
;    Everything else in this installer (Ollama, the vision model) is REQUIRED
;    for the app's core AI features to work at all. SAM is different — it's
;    needed for exactly one feature (Extract Object), the rest of the app
;    (adjustments, AI description/suggestions, save/export) works completely
;    fine without it. Matches the same reasoning already used for the
;    optional Whisper download in UltraVideoEditor.iss: a meaningful extra
;    download (real size varies by exact export/quantization used — check
;    the source below rather than trust a hardcoded number here) shouldn't
;    be forced on someone who may never use that one feature.
;
;  SAM model source (quantized ViT-B, encoder+decoder in one zip):
;    https://huggingface.co/vietanhdev/segment-anything-onnx-models
;  If this ever 404s, check that page for the current filename/URL and
;  update SAM_MODEL_URL below.
;
;  BUILD-TIME DEPENDENCIES: Inno Setup 6.x. Downloads run through curl.exe
;  and PowerShell's Expand-Archive (both ship with Windows 10 1803+ / 11).
;
;  BEFORE COMPILING, ADJUST:
;    - SetupIconFile, if you have an .ico
;    - #define MyAppSourceDir below if your build output path differs
;
;  CI/GitHub Actions: MyAppVersion and MyAppSourceDir can be overridden via
;  "ISCC /DMyAppVersion=... /DMyAppSourceDir=... UltraStudio.iss".
; ══════════════════════════════════════════════════════════════════════════

#define MyAppName "Ultra Studio"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Ultra Creative Suite"
#define MyAppURL "https://github.com/demirajvazi10-max/Ultra-Creative-suite"
#define MyAppExeName "UltraStudio.exe"
#define SamModelUrl "https://huggingface.co/vietanhdev/segment-anything-onnx-models/resolve/main/sam_vit_b_01ec64_quant.zip"

#ifndef MyAppSourceDir
  #define MyAppSourceDir "C:\Users\Ajvazi\source\repos\UltraStudio\UltraStudio\bin\Debug\net8.0-windows"
#endif

[Setup]
AppId={{4F2C7A1E-9B3D-4E6A-8C5F-2D1A6B8E9F4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputBaseFilename=UltraStudioSetup-{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
UninstallDisplayIcon={app}\{#MyAppExeName}
; SetupIconFile=app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"
Name: "serbian"; MessagesFile: "Languages\SerbianLatin.isl"

[Messages]
english.WelcomeLabel2=This will install %1 on your computer, and set up everything needed for AI image description and suggestions (Ollama).%n%nThe language you choose now will also be the default language of the app.
german.WelcomeLabel2=Dies installiert %1 auf Ihrem Computer und richtet alles ein, was für KI-Bildbeschreibungen und Vorschläge (Ollama) benötigt wird.%n%nDie hier gewählte Sprache wird auch die Standardsprache der App.
serbian.WelcomeLabel2=Ovo ce instalirati %1 na vas racunar i podesiti sve sto je potrebno za AI opis slike i predloge (Ollama).%n%nJezik koji sada izaberete bice i podrazumevani jezik aplikacije.

[CustomMessages]
english.DesktopIconGroup=Additional icons:
german.DesktopIconGroup=Zusätzliche Symbole:
serbian.DesktopIconGroup=Dodatne ikone:
english.DesktopIconTaskName=Create a desktop icon
german.DesktopIconTaskName=Ein Desktopsymbol erstellen
serbian.DesktopIconTaskName=Napravi ikonu na radnoj povrsini
english.SamTaskName=Download AI object-extraction models (Extract Object feature, extra download)
german.SamTaskName=KI-Objekterkennungsmodelle herunterladen (Funktion "Objekt extrahieren", zusätzlicher Download)
serbian.SamTaskName=Preuzmi AI modele za izdvajanje objekata (funkcija Extract Object, dodatni download)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"
Name: "downloadsam"; Description: "{cm:SamTaskName}"; GroupDescription: "{cm:DesktopIconGroup}"; Flags: unchecked

[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,runtimes\ios*,runtimes\linux*,runtimes\osx*,runtimes\android*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]

function AppLanguageCode(): String;
begin
  case ActiveLanguage() of
    'german':  Result := 'de';
    'serbian': Result := 'sr';
  else
    Result := 'en';
  end;
end;

var
  StatusPage: TOutputProgressWizardPage;
  GpuType: String;
  VramGB: Integer;
  RamGB: Integer;
  OllamaVisionModel: String;
  CurlExitCode: Integer;

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
    if (Pos('NVIDIA', Lines[0]) > 0) or (Pos('GeForce', Lines[0]) > 0) then GpuType := 'nvidia'
    else if (Pos('Radeon', Lines[0]) > 0) or (Pos('AMD', Lines[0]) > 0) then GpuType := 'amd'
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

// Qwen2.5-VL dolazi u vise velicina (3b/7b/32b/72b) — biramo po istoj logici
// kao tekstualni model u ostatku Ultra paketa, samo za vision varijantu.
procedure ChooseVisionModel();
begin
  OllamaVisionModel := 'qwen2.5vl:3b';
  if GpuType = 'nvidia' then
  begin
    if VramGB >= 24 then OllamaVisionModel := 'qwen2.5vl:32b'
    else if VramGB >= 8 then OllamaVisionModel := 'qwen2.5vl:7b'
    else OllamaVisionModel := 'qwen2.5vl:3b';
  end
  else if (GpuType = 'amd') or (GpuType = 'intel') then
    OllamaVisionModel := 'qwen2.5vl:3b'
  else // cpu only — vision modeli su teski bez GPU-a, ostani na najmanjem
    OllamaVisionModel := 'qwen2.5vl:3b';
end;

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
    22: Result := 'server returned an HTTP error (file moved or no longer exists — please report this)';
    28: Result := 'connection timed out';
  else
    Result := 'curl exit code ' + IntToStr(Code);
  end;
end;

procedure InstallDependencies();
var
  ResultCode: Integer;
  OllamaExe: String;
  SamZip, SamExtractDir, ModelsDir, PsExtract: String;
begin
  OllamaExe := ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe');

  StatusPage.SetText('Detecting your hardware...', '');
  StatusPage.Show;
  try
    DetectGpuAndRam();
    ChooseVisionModel();

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
      StatusPage.SetText('Downloading AI vision model for your hardware (' + OllamaVisionModel + ')...',
                          'This can take several minutes depending on your internet speed.');
      Exec(OllamaExe, 'pull ' + OllamaVisionModel, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox('The AI model could not be downloaded right now (no internet connection?). ' +
               'The app will still work — go to a terminal after install and run: ollama pull ' + OllamaVisionModel,
               mbInformation, MB_OK);
    end
    else
      MsgBox('Ollama could not be installed automatically. AI description/suggestions will be unavailable until you install Ollama manually from ollama.com.', mbInformation, MB_OK);

    // ── SAM (opciono, samo ako je korisnik cekirao) ─────────────────────
    if IsTaskSelected('downloadsam') then
    begin
      StatusPage.SetText('Downloading object-extraction AI models...', 'Size varies by export — please be patient.');
      SamZip := ExpandConstant('{tmp}\sam_models.zip');
      if not DownloadFileCurl('{#SamModelUrl}', SamZip) then
        MsgBox('Could not download the object-extraction models (' + CurlErrorReason(CurlExitCode) + '). ' +
               'The "Extract Object" feature will show download instructions when you try to use it — ' +
               'everything else in the app works normally without this.', mbInformation, MB_OK)
      else
      begin
        StatusPage.SetText('Installing object-extraction AI models...', '');
        ModelsDir := ExpandConstant('{userappdata}\UltraStudio\Models');
        SamExtractDir := ExpandConstant('{tmp}\sam_extract');
        ForceDirectories(ModelsDir);
        ForceDirectories(SamExtractDir);

        // Expand-Archive dolazi ugradjeno sa PowerShell 5.1+ (Windows 10/11) —
        // nema potrebe za dodatnim unzip alatom.
        PsExtract := 'Expand-Archive -Path "' + SamZip + '" -DestinationPath "' + SamExtractDir + '" -Force';
        Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "' + PsExtract + '"',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

        // Fajlovi u zip-u se zovu "encoder.onnx"/"decoder.onnx" (vidi izvor u
        // komentaru na vrhu) — preimenuj u imena koja app.exe ocekuje.
        if FileExists(SamExtractDir + '\encoder.onnx') then
          FileCopy(SamExtractDir + '\encoder.onnx', ModelsDir + '\sam_encoder.onnx', False);
        if FileExists(SamExtractDir + '\decoder.onnx') then
          FileCopy(SamExtractDir + '\decoder.onnx', ModelsDir + '\sam_decoder.onnx', False);

        if not (FileExists(ModelsDir + '\sam_encoder.onnx') and FileExists(ModelsDir + '\sam_decoder.onnx')) then
          MsgBox('The object-extraction models downloaded but could not be installed correctly. ' +
                 'You can install them manually — see the app''s README for instructions.', mbInformation, MB_OK);
      end;
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
  StatusPage := CreateOutputProgressPage('Setting up Ultra Studio', 'Please wait while dependencies are installed.');
end;
