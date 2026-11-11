# Essais (code jetable de mesure)

Prototypes jetables prévus par [docs/conception/etudes-prevues.md](../docs/conception/etudes-prevues.md) et autorisés par la réponse 101 (2026-11-10). Rien ici n'est du code de production : aucune PR, aucune garantie, pas de revue de style. Chaque sous-dossier a un `README.md` qui dit comment relancer l'essai ; les résultats sont dans [docs/essais/](../docs/essais/).

| Dossier | Essai | Résultats |
|---|---|---|
| `e1-wine-v86/` | E1 : maquette Wine dans v86 (démarrage, état, mémoire) | `docs/essais/e1-wine-v86.md` |
| `e2-stockage/` | E2 : stockage des disques (OPFS contre IndexedDB) | `docs/essais/e2-stockage.md` |
| `e3-distribution/` | E3 : distribution des images (hébergeurs CORS/Range, téléchargement reprenable par morceaux vers l'OPFS, SHA-256 .NET contre `SubtleCrypto`, COOP/COEP, cache HTTP) | `docs/essais/e3-distribution.md` |
| `e5-son/` | E5 : son SB16 dans v86 (pont worker → AudioWorklet, saccades #1660/#1661, latence, CPU, PipeWire) | `docs/essais/e5-son.md` |
| `e4-fenetres/` | E4 : fenêtre par fenêtre (agent XComposite dans l'invité contre découpage du canvas) | `docs/essais/e4-fenetres.md` |
| `e6-reseau/` | E6 : réseau et permissions par bouteille (netns par bouteille, relais WebSocket local avec limites 4 flux/300 s/80-443, confinement, 1 VM contre VM séparées) | `docs/essais/e6-reseau.md` |
| `e8-9p/` | E8 : 9p contre disque (issue #1451, installation d'un programme, montage) et durcissement du serveur 9p (liens, noms, tailles, déconnexion) | `docs/essais/e8-9p.md` |

Les images, dépendances et résultats bruts volumineux sont ignorés par git (voir les `.gitignore` des sous-dossiers).
| `e17-wine-dedie/` | Wine dédié (170 A, jour 1) : invité Debian i386 + Wine i386 de WineHQ contre les fenêtres 1x1 de E1 (Notepad, winecfg, winemine ; options Xorg sur l'image E1) | `docs/essais/e17-wine-dedie.md` |
