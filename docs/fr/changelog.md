> [English version](../en/changelog.md)

# Changelog d'Os-x86

## Non publié (0.1.0, jalon 0)

- Squelette du dépôt sur le modèle `kazoos-solution` de Kazo OS : sous-module Kazo OS épinglé sur `v3.2.1`, projets `OsX86.Interface`, `OsX86`, `OsX86.Feature`, hôte `OsX86.Os`, tests, CI.
- Commande `x86` (`--version`, `vm list`) et contrat `Ix86` (version, liste des VM).
- Moteur (chantier 2) : v86 0.5.473 vendorisé dans `src/OsX86/wwwroot/v86` (BSD-2, SeaBIOS LGPL-3, script `tools/v86/vendor-v86.py`, correctif #1661 porté), Web Worker `v86-worker.js` et module hôte `v86-host.js`, abstraction `IEngine` avec `V86Engine` (états arrêté, démarrage, en marche, en pause, arrêt en cours ; erreurs à code), ports série et virtio-console lus en brut ou par lignes et écrits, pont OffscreenCanvas, image Alpine de test (`images/alpine-test`, ISO officiel vérifié, jamais commise) lue depuis l'OPFS, tests `unit` (couverture 80 % exigée par la CI) et test `guest` (Playwright, `guest-tests.yml`). Voir [`moteur.md`](moteur.md).
