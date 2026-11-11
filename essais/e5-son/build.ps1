# Essai E5 — construit l'image invité (Docker, plateforme linux/386), l'exporte
# et produit l'arborescence 9p pour v86 (images/alpine-fs.json + images/alpine-rootfs-flat/).
# Copie de essais/e1-wine-v86/build.ps1 (tag e5-son, WAV généré, tar supprimé à la fin : C: est presque plein).
# Prérequis : Docker Desktop démarré, Python 3.14 (zstd intégré) dans le PATH.
# Usage : pwsh -File build.ps1 [-NoZstd] [-SkipBuild] [-KeepTar]
param([switch]$NoZstd, [switch]$SkipBuild, [switch]$KeepTar)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$images = Join-Path $PSScriptRoot "images"
New-Item -ItemType Directory -Force $images | Out-Null
$tar = Join-Path $images "alpine-rootfs.tar"
$flat = Join-Path $images "alpine-rootfs-flat"
$json = Join-Path $images "alpine-fs.json"
[string[]]$zstd = if ($NoZstd) { @() } else { @("--zstd") }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" } }

$sw = [Diagnostics.Stopwatch]::StartNew()
if (-not (Test-Path (Join-Path $PSScriptRoot "guest\tone.wav"))) { python -I .\gen-wav.py .\guest\tone.wav 10; Check "gen-wav" }
if (-not $SkipBuild) {
  docker build --platform linux/386 --rm --tag e5-son . 2>&1 | Tee-Object -FilePath (Join-Path $images "docker-build.log")
  if ($LASTEXITCODE -ne 0) { throw "docker build a échoué" }
}
Write-Host "build: $($sw.Elapsed)"

docker rm -f e5-son-c 2>$null | Out-Null
docker create --platform linux/386 -t -i --name e5-son-c e5-son | Out-Null
docker export e5-son-c -o $tar
docker rm -f e5-son-c | Out-Null
Write-Host "export: $($sw.Elapsed)  tar=$([math]::Round((Get-Item $tar).Length/1MB,1)) MiB"

python -I .\strip-dockerenv.py $tar
Check "strip-dockerenv"

python -I .\fs2json.py @zstd --out $json $tar
Check "fs2json"
Write-Host "fs2json: $($sw.Elapsed)"
New-Item -ItemType Directory -Force $flat | Out-Null
python -I .\copy-to-sha256.py @zstd $tar $flat | Out-Null
Check "copy-to-sha256"
Write-Host "copy-to-sha256: $($sw.Elapsed)"
if (-not $KeepTar) { Remove-Item $tar }

$flatSize = (Get-ChildItem $flat | Measure-Object -Sum Length).Sum
$n = (Get-ChildItem $flat).Count
Write-Host ("fs.json: {0:N1} MiB ; flat: {1} fichiers, {2:N1} MiB" -f ((Get-Item $json).Length/1MB), $n, ($flatSize/1MB))
Write-Host ("libre sur C: {0:N1} GiB" -f ((Get-PSDrive C).Free/1GB))
