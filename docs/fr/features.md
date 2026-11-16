> [English version](../en/features.md)

# Fonctionnalités d'Os-x86

Tenu à chaque PR (plan, section 12). Les numéros entre parenthèses renvoient aux décisions du [registre](../conception/registre-reconcilie.md) ; les chantiers, à [`jalon-0-execution.md`](../conception/jalon-0-execution.md).

## Disponibles

| Fonctionnalité | Depuis | Note |
| --- | --- | --- |
| Commande `x86` : `--version`, `vm list`, `create`, `start`, `stop`, `remove`, `rename`, `info`, `shell` (shell de l'invité par l'agent, ligne par ligne, Ctrl+C, code de sortie, repli série), `profile list`, `show`, `import`, `export` ; codes de sortie stables | 0.1.0 | Empaquetée comme commande de terminal en attendant le type d'app « Feature » de Kazo OS (ADR 0243) ; chantier 6, [`vm.md`](vm.md) ; dans Kazo, la commande ne démarre pas encore de VM (pas d'émulateur avant le chantier 8) |
| Paquet de contrats `Os-x86.Interface` : `Ix86` (version, liste et état des VM, événement de changement d'état, démarrage, arrêt, console `IVmConsole`, erreurs à code `X86Exception`, refus de budget typé) | 0.1.0 | Générique, aucun type Wine (29 C) ; API publique suivie dans `PublicAPI.Unshipped.txt` |
| Images invitées : outil de paquetage (`pack-image.py`, zstd par trames, morceaux de 32 Mio, manifeste format 1), recettes linux-base (Debian trixie i386, noyau `linux-virt` d'Alpine, init, durcissement 210 A), Alpine de test et FreeDOS, test d'image (22 contrôles R134.5), sources et licences, workflow `images.yml` (Pages et R2 préparés) | 0.1.0 | Chantier 4, [`images.md`](images.md) ; 201 A, 202 A, 210 A ; racine en disque ext4 (`hda`), 9p réservé au dossier partagé ; démarrage de linux-base au chantier 6 |
| Moteur v86 dans un Web Worker : `IEngine` (démarrage, arrêt, pause, états), console série et virtio-console (flux brut et par lignes), pont OffscreenCanvas, v86 0.5.473 vendorisé avec le correctif #1661, image Alpine de test, tests `unit` et `guest` | 0.1.0 | Chantier 2, [`moteur.md`](moteur.md) ; D3, 187 A ; interne à x86-Lib, pas encore exposé par `Ix86` |
| Stockage : manifeste d'image (format 1, SHA-256 par morceau de 32 Mio, empreinte attendue), Web Worker de téléchargement et de vérification (`SubtleCrypto`, écriture OPFS par `SyncAccessHandle`, reprise après coupure et rechargement, réessais, annulation, progression), dépôt d'images dans l'OPFS (liste, suppression, occupation, `estimate()`), verrou d'écriture par `navigator.locks`, `flush()` périodique, métadonnées de format versionné, test `guest` de 800 Mio avec pannes | 0.1.0 | Chantier 3, [`stockage.md`](stockage.md) ; 169, 177 A, 188, 203 A, E11 ; interne à x86-Lib, pas encore exposé par `Ix86` |
| VM : registre par identifiant stable d'usager (format 1, `~/.config/ca.kazo.x86/vms/<guid>.json`), cycle de vie (démarrage à la demande, compteur d'applications, arrêt après un délai de grâce de 5 s, démarrage à la main, arrêt forcé), limite de mémoire (moitié de la mémoire annoncée, refus typé avec les VM compatibles), lancements et consoles suivis comme enfants de la VM, surveillance de l'agent par ping (20 s, deux échecs), repli sur la console série pour une image sans agent, profils `.x86profile` (format 1, `alpine-test` et `linux-base` livrés) ; tests `unit` et `guest` | 0.1.0 | Chantier 6, [`vm.md`](vm.md) ; 165 A, 166 A, 183 A, 190 A, 207 A, 212 A, 216 A ; linux-base par défaut depuis le chantier 6b |
| Agent invité (C statique de 109 Kio, JSON par ligne sur `hvc0`, un cgroup v2 par lancement, `kill` par identifiant de lancement, `mounts`, `exec-in-prefix`, `open-url`, consoles par pty multiplexées sur `hvc1` avec Ctrl+C et code de sortie, fermeture 208 A) et son client C# (`AgentClient`, `LaunchId`, `ConsoleHandle`, erreurs typées) ; installé dans linux-base et dans l'image Alpine de test ; tests `unit` et `guest` | 0.1.0 | Chantier 5, [`agent.md`](agent.md) ; 205 A, 206 A, 208 A, 213 A, 216 A ; interne à x86-Lib, pas encore exposé par `Ix86` ; `list-windows` attend un serveur X (chantier 9) |
| Disques des invités (chantier 6a) : `hda`/`hdb` lus dans `image.bin` de l'OPFS, trames zstd décompressées à la demande par le décodeur de `v86.wasm`, cache de trames borné (32 Mio) ; couche d'écriture persistante (fichier par disque, table de blocs de 4 Kio, métadonnées versionnées, un seul écrivain, `flush()` 5 s et à l'arrêt) ou jetable ; taille et suppression par `ImageStore` ; démarrage décrit par la section `boot` du manifeste (`ImageBoot`), règle « même version majeure » de v86 ; écran texte VGA lu ; linux-base démarre avec son agent et ses 22 contrôles R134.5, FreeDOS jusqu'à son invite | 0.1.0 | [`moteur.md`](moteur.md) ; 169, 177 A, 211 A, E11 ; interne à x86-Lib, pas encore exposé par `Ix86` |
| `linux-base` par défaut (chantier 6b) : `x86 vm create` sans profil prend `linux-base` ; image téléchargée et vérifiée au premier démarrage depuis l'origine du catalogue des images connues (adresse du manifeste et empreinte attendue, configuration, jamais en dur ; progression dans `x86 shell` et `x86 vm start`), puis démarrée depuis l'OPFS ; une couche d'écriture persistante par VM, supprimée avec elle ; `x86 shell` sans préfixe ouvre la console série root ; l'hôte de développement donne à la commande moteur, stockage et packs locaux | 0.1.0 | [`vm.md`](vm.md#linux-base-par-défaut-chantier-6b), [`stockage.md`](stockage.md#catalogue-des-images-connues-chantier-6b) ; 169, 203 A, 212 A ; premier `x86 shell` en 16 s |

## En cours (jalon 0, programmes console)

| Fonctionnalité | Chantier | Décisions |
| --- | --- | --- |
| Pages admin, rôle, journal personnel | 7 | 137, 140 B, 156, 160 |
| Type « Feature », permissions déclarées, `GetServiceAsync<Ix86>()` | 8 | D33, D10, 176 A |
| Image Wine et bouteille gabarit | 9 | 152 A, 187 A, 204 A |
| Préfixes, détection par en-tête, `./hello.exe` console dans le terminal | 10 | 162 A, 193, 200 B |
| Programme DOS texte dans le terminal | 11 | 111, 214 A, 215 A, 217 A |
| Réseau privé, relais, fenêtre de permission, page des règles | 12 | 161, 174 A, 176 A |
| BoxedWine minimal (dépôt séparé) | 13 | 51 D, 62 B |

## Prévues

Jalons 1 à 4 du [plan](../conception/plan-2026-11-11.md), section 8 : BoxedWine et administration complètes, Linux et DOS graphiques, son, entrées, Windows graphique (J3), fin de V1.
