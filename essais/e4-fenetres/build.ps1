# Essai E4 — construit l'image invité (Docker, linux/386), l'exporte et produit l'arborescence 9p
# (images/alpine-fs.json + images/alpine-rootfs-flat/). Dérivé de essais/e1-wine-v86/build.ps1.
# Pour ménager le disque, les fichiers .zst déjà présents dans essais/e1-wine-v86/images/alpine-rootfs-flat/
# (même hash) sont supprimés d'ici : server.mjs les sert depuis E1 en repli (lecture seule).
# Usage : pwsh -File build.ps1 [-SkipBuild]
param([switch]$SkipBuild)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$images = Join-Path $PSScriptRoot "images"
$e1flat = Join-Path $PSScriptRoot "..\e1-wine-v86\images\alpine-rootfs-flat"
New-Item -ItemType Directory -Force $images | Out-Null
$tar = Join-Path $images "alpine-rootfs.tar"
$flat = Join-Path $images "alpine-rootfs-flat"
$json = Join-Path $images "alpine-fs.json"
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" } }
function Free() { [math]::Round((Get-PSDrive C).Free / 1GB, 2) }

$sw = [Diagnostics.Stopwatch]::StartNew()
Write-Host "libre avant: $(Free) Go"
if ((Free) -lt 4) { throw "moins de 4 Go libres, arrêt" }
if (-not $SkipBuild) {
  docker build --platform linux/386 --rm --tag e4-fenetres . 2>&1 | Tee-Object -FilePath (Join-Path $images "docker-build.log")
  if ($LASTEXITCODE -ne 0) { throw "docker build a échoué" }
}
Write-Host "build: $($sw.Elapsed)  libre: $(Free) Go"
if ((Free) -lt 4) { throw "moins de 4 Go libres, arrêt" }

docker rm -f e4-fenetres-c 2>$null | Out-Null
docker create --platform linux/386 -t -i --name e4-fenetres-c e4-fenetres | Out-Null
docker export e4-fenetres-c -o $tar
docker rm -f e4-fenetres-c | Out-Null
Write-Host "export: $($sw.Elapsed)  tar=$([math]::Round((Get-Item $tar).Length/1MB,1)) MiB  libre: $(Free) Go"

python -I .\strip-dockerenv.py $tar
Check "strip-dockerenv"
python -I .\fs2json.py --zstd --out $json $tar
Check "fs2json"
New-Item -ItemType Directory -Force $flat | Out-Null
python -I .\copy-to-sha256.py --zstd $tar $flat | Out-Null
Check "copy-to-sha256"
Remove-Item $tar
Write-Host "copy-to-sha256: $($sw.Elapsed)"

# Dédoublonnage contre E1 (même hash = même contenu) : on ne garde ici que les fichiers nouveaux.
$removed = 0; $kept = 0
foreach ($f in Get-ChildItem $flat) {
  if (Test-Path (Join-Path $e1flat $f.Name)) { Remove-Item $f.FullName; $removed++ } else { $kept++ }
}
$flatSize = (Get-ChildItem $flat | Measure-Object -Sum Length).Sum
Write-Host ("fs.json: {0:N1} KiB ; flat: {1} fichiers propres à E4 ({2:N1} MiB), {3} déjà dans E1 ; libre: {4} Go" -f ((Get-Item $json).Length/1KB), $kept, ($flatSize/1MB), $removed, (Free))
