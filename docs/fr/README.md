> [English version](../en/README.md)

# Documentation d'Os-x86

| Document | Contenu |
| --- | --- |
| [`features.md`](features.md) | Les fonctionnalités : disponibles, en cours, prévues |
| [`changelog.md`](changelog.md) | Les changements par version |
| [`images.md`](images.md) | Les images invitées : recettes (linux-base, Alpine de test, FreeDOS), format de paquetage et manifeste, test d'image, sources et licences, publication, tailles |
| [`agent.md`](agent.md) | L'agent invité et son client C# : protocole (JSON par ligne, événements), cycle de vie d'un lancement (cgroup), consoles par pty multiplexées, délais, mesures, garanties et limites |
| [`vm.md`](vm.md) | Les VM : registre par usager, cycle de vie et délai de grâce, limite de mémoire, surveillance de l'agent, profils `.x86profile`, commandes `x86 vm`, `x86 shell`, `x86 profile` et leurs codes de sortie, ce qu'`Ix86` expose |
| [`moteur.md`](moteur.md) | Le moteur v86 dans un Web Worker : `IEngine`, protocole, v86 vendorisé et correctifs, image Alpine de test, tests, sources et licences |
| [`../conception/`](../conception/) | La conception : plan approuvé, registre des décisions, séries de questions, ordre du jalon 0 (français seulement) |
| [`../essais/`](../essais/) | Les rapports des prototypes de mesure (français seulement) |

Chaque PR qui livre, change ou prévoit une fonctionnalité met à jour `features.md` et `changelog.md` dans les deux langues ; un changement de l'API publique met à jour `src/OsX86.Interface/PublicAPI.Unshipped.txt`.

## Version de Kazo OS

Le sous-module `external/KazoOs` est épinglé sur l'étiquette **`v3.4.0`** de Kazo OS (avant : `v3.2.1`, décision 220 A). Son `wasm2/KazoOs/build/Versions.props` donne la version de tous les paquets `Kazo.Os.*` du dépôt et le `sdkCompatibility.minimumVersion` du manifeste de `OsX86.Feature`.

Règle de suivi (décision 266 A, 2026-11-15) : le dépôt suit les préliminaires de Kazo OS qui apportent une ADR de la série d'Os-x86 (`vX.Y.Z-preview.N`), puis la version stable dès sa publication sur nuget.org ; 3.4.0 a été publiée le jour même de la décision, le dépôt est donc passé directement de `v3.2.1` à `v3.4.0`. Changer de version, c'est déplacer le sous-module sur une autre étiquette (`git -C external/KazoOs checkout v<version>`) par une PR à part. La CI (`.github/scripts/check-kazoos-submodule.sh`) refuse un sous-module modifié ou qui n'est pas exactement sur l'étiquette `v<KazoOsVersion>` ; une étiquette préliminaire est acceptée telle quelle, puisque la version lue dans `Versions.props` en porte le suffixe.

Un build `Debug` réécrit des manifestes générés dans le sous-module : avant de pousser, `git -C external/KazoOs checkout -- .` les remet à l'état de l'étiquette.
