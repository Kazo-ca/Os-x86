# Essai E14 — télécharge les images FreeDOS 1.4 (données non fiables) dans images/dl/ et vérifie le SHA-256
# publié par freedos.org (verify.txt). Usage : pwsh -File download-freedos.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$dl = Join-Path $PSScriptRoot "images/dl"
New-Item -ItemType Directory -Force $dl | Out-Null
$files = @{
  "FD14-LiteUSB.zip" = "857dcd2ebf9d3d094320154db5fb5b830acba6fb98f981a95a0ca7ab3350338b"
  "FD14-LiveCD.zip"  = "2020ff6bb681967fd6eff8f51ad2e5cd5ab4421165948cef4246e4f7fcaf6339"
}
foreach ($name in $files.Keys) {
  $target = Join-Path $dl $name
  if (-not (Test-Path $target)) {
    Write-Host "téléchargement $name"
    curl.exe -sSL --fail -o $target "https://download.freedos.org/1.4/$name"
    if ($LASTEXITCODE -ne 0) { throw "curl $name a échoué" }
  }
  $h = (Get-FileHash -Algorithm SHA256 $target).Hash.ToLower()
  if ($h -ne $files[$name]) { Remove-Item $target; throw "SHA-256 incorrect pour $name : $h" }
  Write-Host "$name : SHA-256 ok ($([math]::Round((Get-Item $target).Length/1MB,1)) MiB)"
  Expand-Archive -Force $target (Join-Path $dl ($name -replace '\.zip$',''))
}
Get-ChildItem -Recurse $dl | Where-Object { -not $_.PSIsContainer } | Select-Object FullName, Length
