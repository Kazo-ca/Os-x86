# Essai E6 — reseau et permissions par bouteille (prototype jetable)

Alpine 3.24 x86 dans v86 (0.5.473), un espace de noms reseau par bouteille (b1, b2),
sortie reelle par un relais WebSocket local qui termine Ethernet/IP/TCP et applique les
limites facon Kazo (4 flux/identite, 300 s, ports 80/443). Pas de Wine ni d'Internet reel :
les programmes de test sont `curl`/`nc` (a la place de programmes Windows), les cibles sont
sur 127.0.0.1. Resultats : `docs/essais/e6-reseau.md`. Code jetable, ne pas reutiliser tel quel.

## Reconstruire l'image (Docker Desktop demarre, Python 3.14 dans le PATH)
`pwsh -File build.ps1` -> `images/alpine-fs.json` + `images/alpine-rootfs-flat/` (~5-10 min).

## Mesure automatique
`npm install` (une fois), puis `node run.mjs --runs 2` : lance le serveur statique (8096),
la cible locale (8080/8443), le relais (9099), pilote v86 par Playwright (Chrome installe,
headless), ecrit `results-<n>.json`. Ouvrir a la main : `node server.mjs` + `node relay.mjs`
+ `node target.mjs`, puis `http://localhost:8096/boot.html?relay=9099`.
