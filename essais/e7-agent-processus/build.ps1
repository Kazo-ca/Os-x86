# Essai E7 — construit l'image (Docker, linux/386, à partir de e17-wine-stable), l'exporte et produit
# l'arborescence 9p pour v86 : images/e7-fs.json + images/rootfs-flat/ (fichiers nommés par SHA-256).
# Produit aussi le disque ext4 de 256 Mio d'une bouteille (images/disk-b1.img) avec e2fsprogs d'Alpine.
# Prérequis : Docker Desktop démarré, image e17-wine-stable (essai E17), Python 3.14 dans le PATH.
# Usage : pwsh -File build.ps1 [-SkipBuild] [-SkipDisk]
param([switch]$SkipBuild, [switch]$SkipDisk)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$images = Join-Path $PSScriptRoot "images"
New-Item -ItemType Directory -Force $images | Out-Null
$tag = "e7-agent"
$tar = Join-Path $images "e7-rootfs.tar"
$flat = Join-Path $images "rootfs-flat"
$json = Join-Path $images "e7-fs.json"
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" } }
function FreeGB { [math]::Round((Get-PSDrive C).Free / 1GB, 1) }
if ((FreeGB) -lt 8) { throw "Moins de 8 Go libres sur C: ($(FreeGB) Go) : arrêt" }

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
Write-Host "fs2json: $($sw.Elapsed)"
New-Item -ItemType Directory -Force $flat | Out-Null
python -I .\copy-to-sha256.py --zstd $tar $flat | Out-Null
Check "copy-to-sha256"
Remove-Item $tar -Force
Write-Host "copy-to-sha256: $($sw.Elapsed)  libre: $(FreeGB) Go"
$flatSize = (Get-ChildItem $flat | Measure-Object -Sum Length).Sum
$n = (Get-ChildItem $flat).Count
Write-Host ("fs.json {0:N1} KiB ; flat : {1} fichiers, {2:N1} MiB" -f ((Get-Item $json).Length/1KB), $n, ($flatSize/1MB))

if (-not $SkipDisk) {
  # Disque ext4 de 256 Mio d'une bouteille, peuplé (mkfs.ext4 -d) d'un drive_c minimal, uid 1001.
  $seed = Join-Path $PSScriptRoot "disk-seed"
  docker run --rm -v "${images}:/out" -v "${seed}:/seed:ro" alpine:3.24 sh -c 'apk add -q e2fsprogs && rm -f /out/disk-b1.img && truncate -s 256M /out/disk-b1.img && mkfs.ext4 -q -F -d /seed -E root_owner=1001:1001 /out/disk-b1.img && ls -l /out/disk-b1.img' 2>&1 | Tee-Object -FilePath (Join-Path $images "mkdisk.log")
  Check "mkfs.ext4"
}
Write-Host "terminé : $($sw.Elapsed)  libre: $(FreeGB) Go"
