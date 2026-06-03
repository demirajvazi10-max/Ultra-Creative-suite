# ══════════════════════════════════════════════════════════════════════════════
#  UltraInstaller.ps1
#  Ultra Creative Suite — Automatski Installer
#
#  Sta radi:
#    1. Provjera administratorskih prava
#    2. Detektuje GPU (NVIDIA/AMD/Intel/CPU only)
#    3. Provjera sta vec postoji (FFmpeg, Ollama, Whisper, VLC)
#    4. Skida samo ono sto nedostaje
#    5. Na osnovu GPU-a bira i skida odgovarajuci Ollama model
#    6. Kreira desktop precicu
#
#  Pokretanje: Desni klik -> Pokreni kao administrator
#  Ili iz cmd: powershell -ExecutionPolicy Bypass -File UltraInstaller.ps1
# ══════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # Ubrzava Invoke-WebRequest

# ── Verzija i putanje ─────────────────────────────────────────────────────────
$AppName    = "Ultra Creative Suite"
$AppDir     = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppExe     = Join-Path $AppDir "UltraVideoEditor.exe"
$FfmpegDir  = Join-Path $AppDir "Ffmpeg"
$FfmpegExe  = Join-Path $FfmpegDir "ffmpeg.exe"
$TempDir    = Join-Path $env:TEMP "UltraInstaller"

# ── Boje za konzolu ───────────────────────────────────────────────────────────
function Write-Step   { param($msg) Write-Host "`n▶ $msg" -ForegroundColor Cyan }
function Write-OK     { param($msg) Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Warn   { param($msg) Write-Host "  ⚠ $msg" -ForegroundColor Yellow }
function Write-Info   { param($msg) Write-Host "  · $msg" -ForegroundColor Gray }
function Write-Fail   { param($msg) Write-Host "  ✗ $msg" -ForegroundColor Red }

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 0 — Administratorska prava
# ══════════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║       Ultra Creative Suite — Installer               ║" -ForegroundColor Magenta
Write-Host "║       Pristupacni kreativni alat za sve korisnike    ║" -ForegroundColor Magenta
Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Fail "Installer zahtjeva administratorska prava."
    Write-Info "Desni klik na install.bat -> 'Pokreni kao administrator'"
    Read-Host "`nPritisnite Enter za izlaz"
    exit 1
}

# Kreiraj temp folder
if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir | Out-Null }

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 1 — Detektuj GPU
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Detektujem hardver..."

$gpuType    = "cpu"      # Podrazumjevano: bez GPU-a
$vramGB     = 0
$gpuName    = "Nije detektovan"

try {
    $gpus = Get-CimInstance Win32_VideoController | Where-Object { $_.AdapterRAM -gt 0 }

    foreach ($gpu in $gpus) {
        $name = $gpu.Name
        $vram = [math]::Round($gpu.AdapterRAM / 1GB, 1)

        Write-Info "GPU: $name ($vram GB VRAM)"

        if ($name -match "NVIDIA|GeForce|Quadro|RTX|GTX") {
            $gpuType = "nvidia"
            $vramGB  = $vram
            $gpuName = $name
            break
        }
        elseif ($name -match "AMD|Radeon|RX ") {
            $gpuType = "amd"
            $vramGB  = $vram
            $gpuName = $name
            break
        }
        elseif ($name -match "Intel") {
            $gpuType = "intel"
            $vramGB  = $vram
            $gpuName = $name
        }
    }
}
catch {
    Write-Warn "Nije moguce detektovati GPU — koristim CPU mod"
}

# ── Odabir Ollama modela na osnovu GPU-a i VRAM-a ────────────────────────────
#
#  Logika:
#   NVIDIA >= 8GB VRAM  → qwen2.5:14b   (puni model, odlicni rezultati)
#   NVIDIA 4-8GB VRAM   → qwen2.5:7b    (dobar balans)
#   NVIDIA < 4GB VRAM   → qwen2.5:3b    (mali model)
#   AMD / Intel GPU     → qwen2.5:7b    (konzervativno, GPU support varira)
#   CPU only            → qwen2.5:3b    (kvantizovano, prihvatljiva brzina)
#
#  Vision model (za AI opis klipova):
#   >= 6GB VRAM         → qwen2.5vl:7b
#   < 6GB ili CPU       → minovar/moondream (manji, brzi)
#

$ollamaQueryModel  = "qwen2.5:3b"    # default
$ollamaVisionModel = "moondream"      # default

switch ($gpuType) {
    "nvidia" {
        if     ($vramGB -ge 8) { $ollamaQueryModel = "qwen2.5:14b";  $ollamaVisionModel = "qwen2.5vl:7b" }
        elseif ($vramGB -ge 4) { $ollamaQueryModel = "qwen2.5:7b";   $ollamaVisionModel = "qwen2.5vl:7b" }
        else                   { $ollamaQueryModel = "qwen2.5:3b";   $ollamaVisionModel = "moondream" }
    }
    "amd"    { $ollamaQueryModel = "qwen2.5:7b";   $ollamaVisionModel = "qwen2.5vl:7b" }
    "intel"  { $ollamaQueryModel = "qwen2.5:7b";   $ollamaVisionModel = "moondream" }
    default  { $ollamaQueryModel = "qwen2.5:3b";   $ollamaVisionModel = "moondream" }
}

Write-OK "GPU: $gpuName ($gpuType, $vramGB GB VRAM)"
Write-OK "Izabrani AI modeli: $ollamaQueryModel + $ollamaVisionModel"

# Spremi GPU info za aplikaciju
$configPath = Join-Path $AppDir "installer_config.json"
@{
    gpu_type           = $gpuType
    gpu_name           = $gpuName
    vram_gb            = $vramGB
    ollama_query_model = $ollamaQueryModel
    ollama_vision_model= $ollamaVisionModel
    install_date       = (Get-Date -Format "yyyy-MM-dd HH:mm")
} | ConvertTo-Json | Set-Content $configPath -Encoding UTF8

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 2 — FFmpeg
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Provjeravamo FFmpeg..."

if (Test-Path $FfmpegExe) {
    Write-OK "FFmpeg vec postoji: $FfmpegExe"
}
else {
    Write-Info "FFmpeg nije pronadjen. Preuzimam..."

    $ffmpegZip = Join-Path $TempDir "ffmpeg.zip"
    $ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"

    try {
        Write-Info "Preuzimanje FFmpeg (ovo moze potrajati ~100MB)..."
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip -UseBasicParsing

        Write-Info "Raspakovavam FFmpeg..."
        $ffmpegExtract = Join-Path $TempDir "ffmpeg_extract"
        Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegExtract -Force

        # FFmpeg zip ima podfolder — nadji ffmpeg.exe
        $ffmpegBin = Get-ChildItem -Path $ffmpegExtract -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1

        if ($ffmpegBin) {
            if (-not (Test-Path $FfmpegDir)) { New-Item -ItemType Directory -Path $FfmpegDir | Out-Null }
            Copy-Item $ffmpegBin.FullName $FfmpegExe

            # Kopiraj i ffprobe ako postoji
            $ffprobeBin = Get-ChildItem -Path $ffmpegExtract -Recurse -Filter "ffprobe.exe" | Select-Object -First 1
            if ($ffprobeBin) {
                Copy-Item $ffprobeBin.FullName (Join-Path $FfmpegDir "ffprobe.exe")
            }

            Write-OK "FFmpeg instaliran: $FfmpegExe"
        }
        else {
            Write-Fail "FFmpeg.exe nije pronadjen u ZIP-u — preuzmi rucno sa ffmpeg.org"
        }
    }
    catch {
        Write-Fail "Greska pri preuzimanju FFmpeg: $_"
        Write-Info "Preuzmi rucno sa: https://ffmpeg.org/download.html"
        Write-Info "Postavi ffmpeg.exe u: $FfmpegDir"
    }
}

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 3 — VLC
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Provjeravamo VLC..."

$vlcPath = "${env:ProgramFiles}\VideoLAN\VLC\vlc.exe"
$vlcPath86 = "${env:ProgramFiles(x86)}\VideoLAN\VLC\vlc.exe"

if ((Test-Path $vlcPath) -or (Test-Path $vlcPath86)) {
    Write-OK "VLC je vec instaliran"
}
else {
    Write-Info "VLC nije pronadjen. Preuzimam..."

    $vlcInstaller = Join-Path $TempDir "vlc_installer.exe"
    $vlcUrl = "https://get.videolan.org/vlc/last/win64/vlc-3.0.21-win64.exe"

    try {
        Write-Info "Preuzimanje VLC (~45MB)..."
        Invoke-WebRequest -Uri $vlcUrl -OutFile $vlcInstaller -UseBasicParsing

        Write-Info "Instaliram VLC (tiha instalacija)..."
        Start-Process -FilePath $vlcInstaller -ArgumentList "/S" -Wait

        if ((Test-Path $vlcPath) -or (Test-Path $vlcPath86)) {
            Write-OK "VLC instaliran"
        }
        else {
            Write-Warn "VLC instalacija nije potvrdjena — mozda treba restart"
        }
    }
    catch {
        Write-Fail "Greska pri instalaciji VLC: $_"
        Write-Info "Preuzmi rucno sa: https://www.videolan.org/vlc/"
    }
}

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 4 — Ollama
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Provjeravamo Ollama..."

$ollamaExe = "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe"
$ollamaInstalled = Test-Path $ollamaExe

if (-not $ollamaInstalled) {
    # Provjeri i PATH
    try {
        $ollamaCheck = & ollama --version 2>$null
        if ($LASTEXITCODE -eq 0) { $ollamaInstalled = $true }
    } catch {}
}

if ($ollamaInstalled) {
    Write-OK "Ollama je vec instalirana"
}
else {
    Write-Info "Ollama nije pronadjena. Preuzimam..."

    $ollamaInstaller = Join-Path $TempDir "OllamaSetup.exe"
    $ollamaUrl = "https://ollama.com/download/OllamaSetup.exe"

    try {
        Write-Info "Preuzimanje Ollama (~100MB)..."
        Invoke-WebRequest -Uri $ollamaUrl -OutFile $ollamaInstaller -UseBasicParsing

        Write-Info "Instaliram Ollama..."
        Start-Process -FilePath $ollamaInstaller -ArgumentList "/SILENT" -Wait

        Write-OK "Ollama instalirana"
    }
    catch {
        Write-Fail "Greska pri instalaciji Ollama: $_"
        Write-Info "Preuzmi rucno sa: https://ollama.com/download"
    }
}

# ── Skidanje Ollama modela ────────────────────────────────────────────────────
Write-Step "Preuzimam AI modele za tvoj hardver..."
Write-Info "Query model:  $ollamaQueryModel"
Write-Info "Vision model: $ollamaVisionModel"
Write-Info "Ovo moze potrajati duze (ovisno o brzini interneta)..."

# Pokreni Ollama server u pozadini ako ne radi
try {
    $ollamaRunning = $false
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:11434" -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        $ollamaRunning = $true
    } catch {}

    if (-not $ollamaRunning) {
        Write-Info "Pokrecam Ollama server..."
        Start-Process -FilePath "ollama" -ArgumentList "serve" -WindowStyle Hidden
        Start-Sleep -Seconds 4
    }

    # Skini query model
    Write-Info "Preuzimam $ollamaQueryModel ..."
    $pullResult = & ollama pull $ollamaQueryModel 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-OK "Model $ollamaQueryModel preuzet"
    } else {
        Write-Warn "Problem sa preuzimanjem $ollamaQueryModel : $pullResult"
    }

    # Skini vision model
    Write-Info "Preuzimam $ollamaVisionModel ..."
    $pullResult = & ollama pull $ollamaVisionModel 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-OK "Model $ollamaVisionModel preuzet"
    } else {
        Write-Warn "Problem sa preuzimanjem $ollamaVisionModel : $pullResult"
    }
}
catch {
    Write-Warn "Nije moguce preuzeti modele automatski: $_"
    Write-Info "Pokreni rucno nakon instalacije:"
    Write-Info "  ollama pull $ollamaQueryModel"
    Write-Info "  ollama pull $ollamaVisionModel"
}

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 5 — Faster-Whisper (transkripcija)
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Provjeravamo Faster-Whisper (transkripcija)..."

$whisperPaths = @(
    (Join-Path $AppDir "faster-whisper-xxl.exe"),
    (Join-Path $AppDir "Whisper\faster-whisper-xxl.exe"),
    "$env:LOCALAPPDATA\Programs\faster-whisper-xxl\faster-whisper-xxl.exe"
)

$whisperFound = $whisperPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($whisperFound) {
    Write-OK "Faster-Whisper pronadjen: $whisperFound"
}
else {
    Write-Info "Faster-Whisper nije pronadjen. Preuzimam..."

    $whisperZip = Join-Path $TempDir "faster-whisper-xxl.zip"
    $whisperUrl = "https://github.com/Purfview/whisper-standalone-win/releases/download/faster-whisper-xxl/Faster-Whisper-XXL_r194.5_windows.zip"

    try {
        Write-Info "Preuzimanje Faster-Whisper (~1GB, ovo ce dugo trajati)..."
        Invoke-WebRequest -Uri $whisperUrl -OutFile $whisperZip -UseBasicParsing

        Write-Info "Raspakovavam Faster-Whisper..."
        $whisperExtract = Join-Path $TempDir "whisper_extract"
        Expand-Archive -Path $whisperZip -DestinationPath $whisperExtract -Force

        $whisperExe = Get-ChildItem -Path $whisperExtract -Recurse -Filter "faster-whisper-xxl.exe" | Select-Object -First 1

        if ($whisperExe) {
            $whisperDestDir = Join-Path $AppDir "Whisper"
            if (-not (Test-Path $whisperDestDir)) { New-Item -ItemType Directory -Path $whisperDestDir | Out-Null }

            # Kopiraj cijeli folder (Whisper ima DLL-ove)
            Copy-Item (Split-Path $whisperExe.FullName) $whisperDestDir -Recurse -Force
            Write-OK "Faster-Whisper instaliran u: $whisperDestDir"
        }
        else {
            Write-Warn "faster-whisper-xxl.exe nije pronadjen u ZIP-u"
        }
    }
    catch {
        Write-Warn "Greska pri preuzimanju Faster-Whisper: $_"
        Write-Info "Preuzmi rucno sa: https://github.com/Purfview/whisper-standalone-win/releases"
        Write-Info "Postavi faster-whisper-xxl.exe u: $(Join-Path $AppDir 'Whisper')"
    }
}

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 6 — Desktop precica
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Kreiram desktop precicu..."

try {
    $desktopPath = [Environment]::GetFolderPath("Desktop")
    $shortcutPath = Join-Path $desktopPath "Ultra Creative Suite.lnk"

    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath     = $AppExe
    $shortcut.WorkingDirectory = $AppDir
    $shortcut.Description    = "Ultra Creative Suite - Pristupacni kreativni alat"
    $shortcut.Save()

    Write-OK "Desktop precica kreirana"
}
catch {
    Write-Warn "Nije moguce kreirati desktop precicu: $_"
}

# ══════════════════════════════════════════════════════════════════════════════
#  KORAK 7 — Ciscenje temp fajlova
# ══════════════════════════════════════════════════════════════════════════════
Write-Step "Cistim privremene fajlove..."
try {
    Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-OK "Temp fajlovi obrisani"
} catch {}

# ══════════════════════════════════════════════════════════════════════════════
#  ZAVRSNO — Izvjestaj
# ══════════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║         Instalacija zavrsena!                        ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  GPU:           $gpuName" -ForegroundColor White
Write-Host "  AI Query:      $ollamaQueryModel" -ForegroundColor White
Write-Host "  AI Vision:     $ollamaVisionModel" -ForegroundColor White
Write-Host ""
Write-Host "  Pokreni: $AppExe" -ForegroundColor Cyan
Write-Host "  Ili koristite desktop precicu." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Podrska za screen readere: JAWS i NVDA" -ForegroundColor Yellow
Write-Host "  GitHub: https://github.com/demirajvazi10-max/Ultra-Creative-suite" -ForegroundColor Yellow
Write-Host ""

Read-Host "Pritisnite Enter za izlaz"
