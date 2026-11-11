# Essai E5 — son (SB16) dans v86 (prototype jetable, ligne E5 des études prévues)

Image de E1 (Alpine 3.24 x86, Wine 11, Xorg) + `alsa-utils`, PipeWire/WirePlumber et un WAV de test (`gen-wav.py` : sinus 440 Hz, escalier de 8 niveaux à gauche pour détecter les rejoues, niveau constant à droite). v86 0.5.473 ; `snd-sb16` est déjà un module de `linux-virt`.
Résultats : `docs/essais/e5-son.md`. Code jetable, ne pas réutiliser tel quel.

## Reconstruire l'image (Docker Desktop démarré, Python 3.14 dans le PATH)
`pwsh -File build.ps1` → `images/alpine-fs.json` + `images/alpine-rootfs-flat/` (~6 min ; les couches de E1 sont reprises du cache Docker ; le tar est supprimé à la fin).

## Ouvrir la page à la main
1. `node server.mjs` (sert sur http://localhost:8733 avec COOP/COEP) ;
2. http://localhost:8733/boot.html?mode=main (v86 + haut-parleur natif dans le fil principal) ou `?mode=worker[&guard=1]` (v86 dans `vm-worker.js`, blocs SB16 envoyés directement du worker à l'AudioWorklet `audio-worklet.js` par un MessagePort ; `guard=1` = garde anti-requêtes périmées, équivalent de la PR v86 #1661) ; `?state=state-snd.bin` restaure l'état (boot + `snd-sb16` chargé) ; `?mem=256` ;
3. bouton « Démarrer l'audio (geste) » (AudioContext.resume), puis « aplay tone.wav » (`sh /root/sound.sh aplay`) ou « PipeWire » (`sh /root/sound.sh pipewire`, après avoir créé `/etc/pipewire/pipewire.conf.d/sb16.conf`, voir `PW_CONF` dans `run.mjs`) ; « Charger le fil principal 10 s » bloque la page 300 ms sur 500 ; « Rapport audio » affiche les compteurs du worklet.

## Mesure automatique
`node run.mjs --runs 2 [--reuse-state] [--scenarios main-idle,worker-idle,...] [--channel chrome|msedge] [--headed]` → `results-boot.json`, `results-<n>.json` (scénarios : `main-idle`, `main-mainload`, `main-gesture`, `worker-idle`, `worker-mainload`, `worker-guestload`, `worker-stall`, `worker-stall-guard`, `worker-stall-guard-lead`, `worker-gesture`, `worker-pipewire`, `main-pipewire`). `images/probe*.mjs` : sondes de mise au point (autoplay), jetables.
