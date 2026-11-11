# Essai E1 — maquette Wine dans v86 (prototype jetable, question 101)

Alpine Linux 3.24 x86 (32 bits) + Wine 11.0 + Xorg/Xvfb, démarré dans v86 (0.5.473) depuis une racine 9p.
Résultats : `docs/essais/e1-wine-v86.md`. Code jetable, ne pas réutiliser tel quel.

## Reconstruire l'image (Docker Desktop démarré, Python 3.14 dans le PATH)
`pwsh -File build.ps1` → `images/alpine-fs.json` + `images/alpine-rootfs-flat/` (fichiers `.bin.zst`, ~5 min).

## Ouvrir la page à la main
1. `node server.mjs` (sert sur http://localhost:8086 avec les en-têtes COOP/COEP) ;
2. ouvrir http://localhost:8086/boot.html dans Chrome/Edge : le boot jusqu'au prompt `localhost:~#` prend ~1 min ;
3. bouton « Lancer le bench (Xvfb) » ou « (Xorg sur VGA) » : lance `sh /root/bench.sh` dans l'invité (wineboot, `wine cmd`, Notepad), les `MARK` s'affichent sous la console ;
4. « Save state » / « Restore state » / « Download state » ; « Charger un état » recharge un fichier `.bin` ;
5. le champ texte envoie une commande à la console série (ex. `wine notepad &`). Paramètres d'URL : `?mem=1024`, `?state=state.bin`.

## Mesure automatique
`node run.mjs --runs 2 --xmode xvfb|xorg --channel chrome|msedge [--headed]` → `results-<n>.json`.
