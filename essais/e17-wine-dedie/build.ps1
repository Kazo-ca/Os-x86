# Essai E17 — construit l'invité Debian i386 + WineHQ (Docker, plateforme linux/386), l'exporte
# et produit l'arborescence 9p pour v86 : images/<branch>-fs.json + images/rootfs-flat/ (partagé
# entre les branches : les fichiers sont nommés par leur SHA-256, un fichier commun n'est stocké qu'une fois).
# Prérequis : Docker Desktop démarré, Python 3.14 (zstd intégré) dans le PATH.
# Usage : pwsh -File build.ps1 [-Branch stable|devel|staging] [-SkipBuild] [-NoZstd]
param([string]$Branch = "stable", [switch]$SkipBuild, [switch]$NoZstd)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$versions = @{ stable = "11.0.0.0~trixie-1"; devel = "11.21~trixie-1"; staging = "11.21~trixie-1" }
if (-not $versions.ContainsKey($Branch)) { throw "Branch inconnue : $Branch" }
$images = Join-Path $PSScriptRoot "images"
New-Item -ItemType Directory -Force $images | Out-Null
$tag = "e17-wine-$Branch"
$tar = Join-Path $images "$Branch-rootfs.tar"
$flat = Join-Path $images "rootfs-flat"
$json = Join-Path $images "$Branch-fs.json"
[string[]]$zstd = if ($NoZstd) { @() } else { @("--zstd") }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" } }
function FreeGB { [math]::Round((Get-PSDrive C).Free / 1GB, 1) }
if ((FreeGB) -lt 6) { throw "Moins de 6 Go libres sur C: ($(FreeGB) Go) : arrêt" }

$sw = [Diagnostics.Stopwatch]::StartNew()
if (-not $SkipBuild) {
  docker build --platform linux/386 --rm --tag $tag --build-arg "WINE_BRANCH=$Branch" --build-arg "WINE_VERSION=$($versions[$Branch])" . 2>&1 | Tee-Object -FilePath (Join-Path $images "docker-build-$Branch.log")
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
python -I .\fs2json.py @zstd --out $json $tar
Check "fs2json"
Write-Host "fs2json: $($sw.Elapsed)"
New-Item -ItemType Directory -Force $flat | Out-Null
python -I .\copy-to-sha256.py @zstd $tar $flat | Out-Null
Check "copy-to-sha256"
Remove-Item $tar -Force
Write-Host "copy-to-sha256: $($sw.Elapsed)  libre: $(FreeGB) Go"

$flatSize = (Get-ChildItem $flat | Measure-Object -Sum Length).Sum
$n = (Get-ChildItem $flat).Count
Write-Host ("{0}: fs.json {1:N1} KiB ; flat (toutes branches) : {2} fichiers, {3:N1} MiB" -f $Branch, ((Get-Item $json).Length/1KB), $n, ($flatSize/1MB))
