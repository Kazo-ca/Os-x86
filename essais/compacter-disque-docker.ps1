# À lancer dans un PowerShell « Exécuter en tant qu'administrateur ».
# Arrête Docker Desktop et WSL, compacte le disque virtuel de Docker, relance Docker Desktop.
# Les conteneurs marqués « restart » (ex. valoria-preprod-*) repartent d'eux-mêmes.
$vhd = Join-Path $env:LOCALAPPDATA 'Docker\wsl\disk\docker_data.vhdx'
if (-not (Test-Path $vhd)) { throw "Introuvable : $vhd" }
"{0:N1} Go avant" -f ((Get-Item $vhd).Length / 1GB)
# ext4 ne remet pas à zéro les blocs libérés : sans ce trim, diskpart ne reprend rien.
wsl -d docker-desktop -e fstrim -v /mnt/docker-desktop-disk
Get-Process 'Docker Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 8
wsl --shutdown
Start-Sleep 5
$script = Join-Path $env:TEMP 'compact-vhd.txt'
@("select vdisk file=`"$vhd`"", 'attach vdisk readonly', 'compact vdisk', 'detach vdisk') | Set-Content $script -Encoding ASCII
diskpart /s $script
"{0:N1} Go après" -f ((Get-Item $vhd).Length / 1GB)
Start-Process (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe')
Get-PSDrive C | Select-Object @{n = 'LibreGo'; e = { [math]::Round($_.Free / 1GB, 2) } }
