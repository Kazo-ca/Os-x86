# Essai E14 — construit l'invité Debian i386 (recette E17 + outils console), l'exporte et produit
# l'arborescence 9p pour v86 : images/wine-fs.json + images/rootfs-flat/ (fichiers nommés par SHA-256).
# Prérequis : Docker Desktop, Python 3.14 (zstd intégré). Usage : pwsh -File build.ps1 [-SkipBuild]
param([switch]$SkipBuild)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$images = Join-Path $PSScriptRoot "images"
New-Item -ItemType Directory -Force $images | Out-Null
$tag = "e14-wine-console"
$tar = Join-Path $images "wine-rootfs.tar"
$flat = Join-Path $images "rootfs-flat"
$json = Join-Path $images "wine-fs.json"
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" } }
function FreeGB { [math]::Round((Get-PSDrive C).Free / 1GB, 1) }
if ((FreeGB) -lt 6) { throw "Moins de 6 Go libres sur C: ($(FreeGB) Go) : arrêt" }
$sw = [Diagnostics.Stopwatch]::StartNew()
if (-not $SkipBuild) {
  docker build --platform linux/386 --rm --tag $tag . 2>&1 | Tee-Object -FilePath (Join-Path $images "docker-build.log")
  if ($LASTEXITCODE -ne 0) { throw "docker build a échoué" }
}
Write-Host "build: $($sw.Elapsed)  libre: $(FreeGB) Go"
docker rm -f "$tag-c" 2>$null | Out-Null
docker create --platform linux/386 -t -i --name "$tag-c" $tag | Out-Null
docker export "$tag-c" -o $tar
docker rm -f "$tag-c" | Out-Null
Write-Host "export: $($sw.Elapsed)  tar=$([math]::Round((Get-Item $tar).Length/1MB,1)) MiB  libre: $(FreeGB) Go"
python -I .\strip-dockerenv.py $tar
Check "strip-dockerenv"
python -I .\fs2json.py --zstd --out $json $tar
Check "fs2json"
New-Item -ItemType Directory -Force $flat | Out-Null
python -I .\copy-to-sha256.py --zstd $tar $flat | Out-Null
Check "copy-to-sha256"
Remove-Item $tar -Force
$flatSize = (Get-ChildItem $flat | Measure-Object -Sum Length).Sum
Write-Host ("fs.json {0:N1} KiB ; flat : {1} fichiers, {2:N1} MiB ; total {3}  libre: {4} Go" -f ((Get-Item $json).Length/1KB), (Get-ChildItem $flat).Count, ($flatSize/1MB), $sw.Elapsed, (FreeGB))
