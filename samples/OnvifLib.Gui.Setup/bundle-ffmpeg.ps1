# Downloads the pinned LGPL ffmpeg.exe next to a win-x64 publish folder.
# Used by the WiX project (and the GUI AfterPublish on Windows) so an install
# never starts with "ffmpeg not found".
param(
  [Parameter(Mandatory = $true)][string]$DestinationDir,
  [Parameter(Mandatory = $true)][string]$Url,
  [Parameter(Mandatory = $true)][string]$Sha256,
  [string]$CacheDir = ''
)

$ErrorActionPreference = 'Stop'
$DestinationDir = $DestinationDir.Trim().Trim("'", '"').TrimEnd('\', '/')
$DestinationDir = [IO.Path]::GetFullPath($DestinationDir)

$destBin = Join-Path $DestinationDir 'ffmpeg\ffmpeg.exe'
if (Test-Path -LiteralPath $destBin) {
  Write-Host "ffmpeg already present: $destBin"
  exit 0
}

if (-not $CacheDir) {
  $CacheDir = Join-Path (Split-Path $DestinationDir -Parent) 'ffmpeg-cache'
}
$CacheDir = [IO.Path]::GetFullPath($CacheDir)
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
$archive = Join-Path $CacheDir 'ffmpeg-win64-lgpl.zip'

function Get-Sha256Hex([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $stream = [IO.File]::OpenRead($Path)
    try {
      return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
      $stream.Dispose()
    }
  }
  finally {
    $sha.Dispose()
  }
}

$expected = $Sha256.ToLowerInvariant()
$needDownload = $true
if (Test-Path -LiteralPath $archive) {
  Write-Host "Checking cached archive..."
  $cached = Get-Sha256Hex $archive
  if ($cached -eq $expected) {
    Write-Host "Using cached ffmpeg archive: $archive"
    $needDownload = $false
  }
  else {
    Write-Host "Cached archive checksum mismatch; re-downloading."
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
  }
}

if ($needDownload) {
  $curl = Get-Command curl.exe -ErrorAction Stop
  Write-Host "Downloading ffmpeg (LGPL)..."
  # Resume + retries: GitHub drops mid-transfer on slow links (curl 18).
  & $curl.Source --fail --location --retry 8 --retry-all-errors --retry-delay 3 --continue-at - --output $archive $Url
  if ($LASTEXITCODE -ne 0) {
    throw "curl failed to download ffmpeg (exit $LASTEXITCODE)."
  }
}

$actual = Get-Sha256Hex $archive
if ($actual -ne $expected) {
  Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
  throw "ffmpeg archive checksum mismatch (expected $expected, got $actual). Deleted the cache; rebuild to download again."
}

$stage = Join-Path $CacheDir 'stage'
if (Test-Path -LiteralPath $stage) {
  Remove-Item -LiteralPath $stage -Recurse -Force
}
Expand-Archive -LiteralPath $archive -DestinationPath $stage -Force
$exe = Get-ChildItem -LiteralPath $stage -Recurse -Filter 'ffmpeg.exe' |
  Select-Object -First 1
if (-not $exe) {
  throw 'The archive downloaded, but it did not contain ffmpeg.exe.'
}

$outDir = Join-Path $DestinationDir 'ffmpeg'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -LiteralPath $exe.FullName -Destination (Join-Path $outDir 'ffmpeg.exe')
@(
  'Bundled ffmpeg is an LGPL build from BtbN/FFmpeg-Builds (n7.1.x).'
  'Source and licenses: https://github.com/BtbN/FFmpeg-Builds'
  'The OnvifLib.Gui app itself is MIT; this binary is LGPL.'
) | Set-Content -LiteralPath (Join-Path $outDir 'README.txt') -Encoding utf8

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "ffmpeg ready: $(Join-Path $outDir 'ffmpeg.exe')"
