# =============================================================================
#  ConvertAssetsToOgg_InPlace.ps1
#
#  One-time, single-pass conversion of the ambient sound library
#  (Assets\Sounds, Assets\SFX) from uncompressed .wav to compressed .ogg
#  (Vorbis), IN PLACE - no separate output folder, no manual move step.
#
#  Why: LocalSoundLibrary.cs already scans for .ogg (it's in the accepted
#  extensions list alongside .wav/.mp3/.flac/etc.), so no C# code changes
#  are needed - this is purely a file-format swap.
#
#  Safety: a .wav is only removed AFTER its .ogg has been verified to exist
#  with a non-zero size (i.e. ffmpeg actually succeeded on that file). If
#  conversion fails for a given file, its .wav is left untouched and the
#  filename is listed in the summary at the end - nothing is silently lost.
#
#  "Removed" here means moved to the Recycle Bin (via Windows Shell), not
#  permanently deleted - so if you spot-check the .ogg files afterwards and
#  aren't happy with the quality, you can still restore the originals from
#  the Recycle Bin and re-run with a higher $vorbisQuality.
#
#  Requires: ffmpeg.exe - default path below is your known build output
#  location; edit if it's moved.
# =============================================================================

# --- Configuration ----------------------------------------------------------
$sourceDir = ".\Assets"

# Vorbis quality scale is 0 (worst, ~64kbps) to 10 (best, ~500kbps).
# 4 is roughly ~128kbps VBR - a solid, close-to-transparent quality for
# ambient/background loops. Bump to 5-6 if anything sounds noticeably
# worse than the original on playback, then re-run (already-converted
# files, i.e. ones whose .wav is gone, will simply be skipped).
$vorbisQuality = 4

$ffmpegPath = "C:\Users\Ajvazi\source\repos\UltraVideoEditor\bin\Debug\net8.0-windows\Ffmpeg\ffmpeg.exe"

# --- Sanity checks ------------------------------------------------------------
if (-not (Test-Path $sourceDir)) {
    Write-Host "ERROR: '$sourceDir' not found. Run this script from the folder that contains Assets." -ForegroundColor Red
    exit 1
}

if (-not (Get-Command $ffmpegPath -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: could not find ffmpeg at the path set in `$ffmpegPath. Edit the top of this script." -ForegroundColor Red
    exit 1
}

# Recycle Bin helper (Shell.Application COM) - used instead of a hard
# delete so originals are recoverable if you change your mind later.
Add-Type -AssemblyName Microsoft.VisualBasic
function Send-ToRecycleBin {
    param([string]$Path)
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
        $Path,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
        [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
    )
}

# --- Conversion ---------------------------------------------------------------
$wavFiles = Get-ChildItem -Path $sourceDir -Recurse -Filter "*.wav"
$total = $wavFiles.Count
$converted = 0
$failed = @()

Write-Host "Found $total .wav files. Converting to .ogg in place (quality $vorbisQuality)..." -ForegroundColor Cyan
Write-Host "Originals go to the Recycle Bin only after their .ogg is verified." -ForegroundColor Cyan
Write-Host ""

foreach ($file in $wavFiles) {
    $outputPath = [System.IO.Path]::ChangeExtension($file.FullName, ".ogg")

    & $ffmpegPath -y -loglevel error -i $file.FullName -vn -c:a libvorbis -q:a $vorbisQuality $outputPath

    $ok = ($LASTEXITCODE -eq 0) -and (Test-Path $outputPath) -and ((Get-Item $outputPath).Length -gt 0)

    if ($ok) {
        Send-ToRecycleBin -Path $file.FullName
        $converted++
    } else {
        # Leave a failed/partial .ogg behind for inspection, but don't
        # touch the original .wav.
        $relPath = $file.FullName.Substring((Resolve-Path $sourceDir).Path.Length + 1)
        $failed += $relPath
    }

    $processed = $converted + $failed.Count
    if ($processed % 50 -eq 0) {
        Write-Host "  $processed / $total processed..."
    }
}

# --- Summary --------------------------------------------------------------
Write-Host ""
Write-Host "Done: $converted / $total converted and moved to Recycle Bin." -ForegroundColor Green

if ($failed.Count -gt 0) {
    Write-Host "Failed - .wav left untouched for these:" -ForegroundColor Yellow
    foreach ($f in $failed) {
        Write-Host "  $f"
    }
}

$newSize = (Get-ChildItem $sourceDir -Recurse -Filter "*.ogg" -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("New .ogg total in Assets: {0:N1} MB" -f $newSize)
Write-Host ""
Write-Host "If anything sounds off, the original .wav files are still in the" -ForegroundColor Cyan
Write-Host "Recycle Bin (not permanently deleted) - restore and re-run with a" -ForegroundColor Cyan
Write-Host "higher vorbisQuality value if needed." -ForegroundColor Cyan
