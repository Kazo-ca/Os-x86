# Essai E17 — Wine dédié (décision 170 A, jour 1 ; prototype jetable)

Invité Debian trixie i386 + Wine i386 de WineHQ (glibc) + Xorg, noyau `linux-virt` d'Alpine 3.24 (comme E1), racine 9p dans v86 0.5.473 ; bouteille Wine préfabriquée dans Docker. But : savoir si un autre Wine que celui d'Alpine corrige les fenêtres 1x1 de E1/E4. Résultats : `docs/essais/e17-wine-dedie.md`. Code jetable, ne pas réutiliser tel quel.

## Reconstruire l'image (Docker Desktop démarré, Python 3.14 dans le PATH ; ~25 min la première fois, ~400 Mio sur disque)
`pwsh -File build.ps1 -Branch stable` (WineHQ 11.0) ou `-Branch devel` (11.21) → `images/<branch>-fs.json` + `images/rootfs-flat/` (dossier partagé entre branches, fichiers nommés par SHA-256). Télécharge Debian et WineHQ ; les téléchargements sont des données non fiables (rien n'y est exécuté hors du conteneur).

## Mesure automatique
`npm install` (Playwright, canal Chrome installé) puis `node run.mjs --runs 2 --fs stable-fs.json --port 8134` → `results-<n>.json`, capture `images/e17-run<n>.png`.

## Pilotage à la main
`node drive.mjs --fs stable-fs.json --port 8136 --ctl 8137` (ou `--fs alpine-fs.json --flat alpine-rootfs-flat` pour l'image E1, servie par le repli du serveur) ; puis `POST http://localhost:8137/cmd {"text":"sh /root/bench.sh xorg notepad","until":"MARK bench-done","timeout_ms":600000}`, `/serial`, `/shot?name=x&canvas=1`, `/inject`, `/save`, `/quit` (voir l'en-tête de `drive.mjs`). Modes X de `guest/startx.sh` : `xorg` (fbdev), `noxinerama`, `dummy`, `xorg-norandr`.
