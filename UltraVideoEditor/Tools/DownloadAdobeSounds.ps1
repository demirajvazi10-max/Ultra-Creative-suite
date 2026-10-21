# =============================================================================
#  DownloadAdobeSounds.ps1  (Ultra Video Editor 1.4, by Demir Ajvazi)
#
#  Downloads the ambient sound library the AI music video creator uses directly
#  from Adobe (Adobe Audition's free sound effects, Adobe's own server and
#  licence), converts every sound to OGG and keeps only the OGG files.
#
#  Why from Adobe: Adobe's licence does not allow the sound files to be shared
#  as a standalone package, so Ultra Video Editor does not host them. Every user
#  downloads them from Adobe under Adobe's terms:
#  https://www.adobe.com/products/audition/offers/AdobeAuditionDLCSFX.html
#
#  Used by the installer (optional task) and by Help -> Download the sound
#  library. Prints one "STEP ..." / "PROGRESS ..." / "DONE ..." line at a time
#  so the program can announce the progress to a screen reader.
#
#  Download ~3.7 GB in total (one category at a time, each deleted after use),
#  about 350 MB stays on disk.
# =============================================================================
param(
    [string]$Dest = (Join-Path $env:LOCALAPPDATA "UltraVideoEditor\Sounds"),
    [string]$Ffmpeg = "",
    [string[]]$Categories = @("weather", "animals", "human_elements", "liquid_water", "ambience_1", "ambience_2"),
    [int]$Quality = 4
)

$ErrorActionPreference = "Continue"
$base = "https://download.adobe.com/pub/adobe/audition/offers/adobeauditiondlcsfx"
$names = @{
    "weather" = "Weather"; "animals" = "Animals"; "human_elements" = "Human Elements";
    "liquid_water" = "Liquid and Water"; "ambience_1" = "Ambience 1"; "ambience_2" = "Ambience 2"
}

# --- FFmpeg: given, next to the program, or on PATH -------------------------
if (-not $Ffmpeg -or -not (Test-Path $Ffmpeg)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    foreach ($c in @((Join-Path $here "..\Ffmpeg\ffmpeg.exe"), (Join-Path $here "..\ffmpeg.exe"))) {
        if (Test-Path $c) { $Ffmpeg = (Resolve-Path $c).Path; break }
    }
    if ((-not $Ffmpeg -or -not (Test-Path $Ffmpeg)) -and (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) {
        $Ffmpeg = (Get-Command ffmpeg.exe).Source
    }
}
if (-not $Ffmpeg -or -not (Test-Path $Ffmpeg)) { Write-Output "ERROR FFmpeg not found"; exit 2 }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$tmpRoot = Join-Path $env:TEMP ("UVE_AdobeSounds_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$total = 0
$failedCats = @()

try {
    $i = 0
    foreach ($cat in $Categories) {
        $i++
        $label = if ($names.ContainsKey($cat)) { $names[$cat] } else { $cat }
        $catDest = Join-Path $Dest $label
        $marker = Join-Path $catDest ".complete"
        if (Test-Path $marker) { Write-Output "STEP $i/$($Categories.Count) $label already downloaded"; continue }

        Write-Output "STEP $i/$($Categories.Count) Downloading $label from Adobe"
        $zip = Join-Path $tmpRoot "$cat.zip"
        # curl.exe is part of Windows 10/11; --retry and -C - continue an interrupted download
        & curl.exe -L --fail --retry 3 --retry-delay 5 -C - -s -o $zip "$base/$cat.zip"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $zip)) { Write-Output "ERROR download $label"; $failedCats += $label; continue }

        Write-Output "STEP $i/$($Categories.Count) Unpacking $label"
        $ex = Join-Path $tmpRoot $cat
        New-Item -ItemType Directory -Force -Path $ex | Out-Null
        & tar.exe -xf $zip -C $ex
        Remove-Item -Force $zip -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { Write-Output "ERROR unpack $label"; $failedCats += $label; continue }

        $files = @(Get-ChildItem -Path $ex -Recurse -File | Where-Object { $_.Extension -match '^\.(wav|aif|aiff|mp3|flac)$' })
        New-Item -ItemType Directory -Force -Path $catDest | Out-Null
        $n = 0
        foreach ($f in $files) {
            $n++
            $out = Join-Path $catDest ([System.IO.Path]::GetFileNameWithoutExtension($f.Name) + ".ogg")
            if (-not (Test-Path $out)) {
                & $Ffmpeg -nostdin -y -loglevel error -i $f.FullName -vn -c:a libvorbis -q:a $Quality $out
            }
            if ($n % 25 -eq 0 -or $n -eq $files.Count) { Write-Output "PROGRESS $label $n/$($files.Count)" }
        }
        $total += $n
        Remove-Item -Recurse -Force $ex -ErrorAction SilentlyContinue
        Set-Content -Path $marker -Value (Get-Date -Format s)
    }
}
finally {
    Remove-Item -Recurse -Force $tmpRoot -ErrorAction SilentlyContinue
}

$mb = [math]::Round(((Get-ChildItem $Dest -Recurse -Filter *.ogg -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum) / 1MB)
if ($failedCats.Count -gt 0) { Write-Output ("DONE-PARTIAL $total $mb " + ($failedCats -join ", ")); exit 1 }
Write-Output "DONE $total $mb"
exit 0
