# Essai E4 — fenêtre par fenêtre (prototype jetable, question 112 / D26)

Invité Alpine 3.24 x86 (base de E1 + xprop, xwd, xclock, xeyes, xmessage) dans v86 0.5.473 ; un agent C (`guest/e4agent.c`, XComposite + XDamage + XGetImage) écrit chaque fenêtre X11 sur la racine 9p, que la page (`boot.html`) lit avec `emu.read_file` et affiche dans une div déplaçable par fenêtre. Résultats : `docs/essais/e4-fenetres.md`.

## Reconstruire
- `pwsh -File build.ps1` : image Docker (couches de E1 réutilisées) → `images/alpine-fs.json` + `images/alpine-rootfs-flat/` (seuls les fichiers absents de `../e1-wine-v86/images/` sont gardés ; `server.mjs` sert le reste depuis E1 en lecture seule). Arrêt si moins de 4 Go libres.
- `pwsh -File build-agent.ps1` : compile seulement l'agent → `images/e4agent` (injecté dans l'invité par 9p au démarrage par `run.mjs`).

## Ouvrir à la main
1. `node server.mjs 8094` puis http://localhost:8094/boot.html?fs=alpine-fs.json (boot ~70 s) ; 2. console série : `sh /root/demo.sh composite 20` (X sans gestionnaire, xterm + xclock + xeyes + xmessage, agent) ; 3. « Démarrer » en mode composite ou découpage : une fenêtre HTML par fenêtre X, glissable par sa barre, clics renvoyés à l'invité (`xdotool`).

## Mesure automatique
`node run.mjs --runs 2 --port 8094 [--hz 20] [--poll 50] [--headed]` → `results-<n>.json`, captures `images/e4-run<n>-*.png`. `node drive.mjs --ctl 8097 [--state state-e4.bin]` : pilotage interactif par HTTP (voir l'en-tête du fichier).
