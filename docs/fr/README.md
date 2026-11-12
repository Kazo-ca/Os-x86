> [English version](../en/README.md)

# Documentation d'Os-x86

| Document | Contenu |
| --- | --- |
| [`features.md`](features.md) | Les fonctionnalités : disponibles, en cours, prévues |
| [`changelog.md`](changelog.md) | Les changements par version |
| [`images.md`](images.md) | Les images invitées : recettes (linux-base, Alpine de test, FreeDOS), format de paquetage et manifeste, test d'image, sources et licences, publication, tailles |
| [`agent.md`](agent.md) | L'agent invité et son client C# : protocole (JSON par ligne, événements), cycle de vie d'un lancement (cgroup), consoles par pty multiplexées, délais, mesures, garanties et limites |
| [`moteur.md`](moteur.md) | Le moteur v86 dans un Web Worker : `IEngine`, protocole, v86 vendorisé et correctifs, image Alpine de test, tests, sources et licences |
| [`../conception/`](../conception/) | La conception : plan approuvé, registre des décisions, séries de questions, ordre du jalon 0 (français seulement) |
| [`../essais/`](../essais/) | Les rapports des prototypes de mesure (français seulement) |

Chaque PR qui livre, change ou prévoit une fonctionnalité met à jour `features.md` et `changelog.md` dans les deux langues ; un changement de l'API publique met à jour `src/OsX86.Interface/PublicAPI.Unshipped.txt`.
