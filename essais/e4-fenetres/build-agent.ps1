# Essai E4 — compile seulement l'agent (étape "build" du Dockerfile, gcc + Xlib i386) et dépose
# le binaire dans images/e4agent ; run.mjs l'injecte dans l'invité par 9p au démarrage.
# Usage : pwsh -File build-agent.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force images | Out-Null
docker build --platform linux/386 --target build --tag e4-fenetres-build . | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker build (étape build) a échoué" }
docker rm -f e4-fenetres-build-c 2>$null | Out-Null
docker create --platform linux/386 --name e4-fenetres-build-c e4-fenetres-build | Out-Null
docker cp e4-fenetres-build-c:/src/e4agent images/e4agent
docker rm -f e4-fenetres-build-c | Out-Null
Write-Host ("images/e4agent : {0} octets" -f (Get-Item images/e4agent).Length)
