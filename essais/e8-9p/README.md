# Essai E8 — 9p : vitesse et durcissement (prototype jetable, question 107 / D22)

Réutilise l'image Alpine + Wine de E1 (`../e1-wine-v86/images/`, servie en lecture seule, jamais modifiée) dans v86 0.5.473, plus un disque ext4 de 512 Mio (`images/disk.img`, `hda` async) pour comparer 9p et disque.
Résultats : `docs/essais/e8-9p.md`. Code jetable, ne pas réutiliser tel quel.

## Préparer le disque (Docker Desktop démarré, image `alpine:3.24`)
`docker run --rm -v "$PWD/images:/out" alpine:3.24 sh -c 'apk add e2fsprogs && truncate -s 512M /out/disk.img && mkfs.ext4 -F /out/disk.img'`

## Ouvrir la page à la main
`node server.mjs` (http://localhost:8098, en-têtes COOP/COEP, Range) puis http://localhost:8098/e8.html : boot ~1 min, boutons « bench 9p », « bench disque », « harden » ;
`window.E8` expose `hostStat/hostList/hostRead` (FS en mémoire de v86), `faultOn(chemin, eio|throw|hang)` (lecteur déconnecté) et `raw(id, champs)` (messages 9P2000.L forgés).

## Mesure automatique
`node run.mjs --runs 2 [--wine] [--no-harden] [--no-2g]` (Playwright pris dans `../e1-wine-v86/node_modules`, Chrome installé, headless, profil temporaire supprimé) → `results-<n>.json`.
