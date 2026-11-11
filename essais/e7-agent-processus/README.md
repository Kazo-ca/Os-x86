# Essai E7 — agent et processus (prototype jetable)

Invité E17 (Debian trixie i386 + WineHQ 11.0, noyau `linux-virt` d'Alpine, racine 9p dans v86 0.5.473) + agent statique (`agent/e7-agent.c`, musl i386) sur un port virtio-console (hvc1 : JSON par ligne : `ping`, `list-processes`, `launch`, `kill` par pid/groupe/uid/cgroup, `list-windows`, `mounts`, `exec-in-prefix`, `open-console` sur hvc2, événements `exit`/`open-url`) ; deux bouteilles = deux usagers invités (`wine-b1`, `wine-b2`) avec préfixe copié ; cgroup v2 par lancement. Résultats : `docs/essais/e7-agent-processus.md`. Code jetable, ne pas réutiliser tel quel.

## Reconstruire l'image (Docker Desktop démarré, image `e17-wine-stable` de E17 présente, Python 3.14 ; ~8 min, ~400 Mio sur disque)
`pwsh -File build.ps1` → image Docker `e7-agent`, `images/e7-fs.json` + `images/rootfs-flat/`, disque ext4 `images/disk-b1.img` (256 Mio, peuplé de `disk-seed/`). `lib/` (v86) est servi depuis `../e17-wine-dedie/lib/` si absent ici.

## Mesure automatique
`npm install` (Playwright, canal Chrome installé) puis `node run.mjs --runs 3 --port 8144` → `results-<n>.json`, captures `images/e7-run<n>*.png`. Écritures hors compte (102) : `node run-writes.mjs --runs 2 --port 8148` → `results-writes.json`. Tableau « sans agent » (Alpine nue de E6, console série seule) : `node run-alpine.mjs --port 8146` → `results-alpine.json`. Tableau comparatif des runs : `python -I summarize.py 1 2 3`. Les scripts corrigés après la construction de l'image (`guest/`, `images/e7-rawsock`) sont injectés par 9p au boot par `run.mjs`.

## Pilotage à la main
`node drive.mjs --port 8142 --ctl 8143 --disk disk-b1.img` puis `POST http://localhost:8143/agent {"op":"ping"}`, `/cmd` (console série root), `/console` (console d'usager hvc2), `/fault?path=srv/share-b1` (partage disparu : Rlerror EIO), `/disk?name=x.img` (export du disque ext4), `/shot`, `/quit` (voir l'en-tête de `drive.mjs`). Lecture ext4 côté hôte : `MSYS_NO_PATHCONV=1 node ext4-ls.mjs images/x.img /`.
