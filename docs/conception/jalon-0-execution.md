# Jalon 0 — ordre d'exécution (2026-11-11)

Détaille la ligne J0 de la section 8 du [plan](plan-2026-11-11.md) en chantiers, branches et dépendances, selon les règles de Kazo (`CONTRIBUTING-fr.md`, `AGENTS.md` : ADR avant le code, série `<nom>/base` + un chantier par branche, numéros d'ADR libres à partir de 0243, apps externes sur le modèle `kazoos-solution` avec Kazo en sous-module épinglé sur un tag). Rien ici n'est une décision de conception : tout vient du plan et des séries 1 à 14.

## 1. Trois dépôts, trois séries

| Dépôt | Série | Contenu | Bloque |
|---|---|---|---|
| Kazo-ca/Os | `feature/base` | ADR lot 1 (13 ADR, 0243 à 0255), puis le code qui les met en œuvre, un chantier par ADR | la Feature installable, les pages admin, le Terminal qui affiche un invité, la route du shell, le relais par identité |
| Kazo-ca/Os-x86 | `j0/base` | le moteur et la Feature (chantiers 1 à 12 ci-dessous) | — |
| Kazo-ca/Os-App-BoxedWine | `j0/base` | BoxedWine minimal (chantier 13), étape b | — |

Ce qui ne dépend pas de Kazo commence tout de suite dans Os-x86 (chantiers 1 à 6) ; ce qui en dépend (7, 8, 10 à 13) attend l'ADR correspondante **et** son code dans une version publiée de Kazo (sous-module déplacé sur ce tag).

## 2. Kazo : ADR lot 1 → numéros

| # plan | ADR Kazo | Titre | Nourrit |
|---|---|---|---|
| 1 | 0243 | Type d'app « Feature » | tout |
| 2 | 0244 | Bibliothèques d'app et versions partagées (#419) | `Os-x86.Interface`, `OsX86`, `BwLib` |
| 3 | 0245 | Registre d'interfaces et résolution asynchrone | `GetServiceAsync<Ix86>()` |
| 4 | 0246 | Permissions déclarées par un fournisseur | `x86.programs.launch`, `x86.vm`, `x86.vm.use`, `x86.vm.destroy`, `network.real.access` |
| 5 | 0247 | Rôles déclarés par une Feature | pages admin, `sudo x86 admin` |
| 6 | 0248 | Résolution des dépendances (Store, kazopkg) | BoxedWine dépend d'Os-x86 |
| 7 | 0249 | Section « Fonctionnalités » des Paramètres | pages VM et utilisateurs |
| 8 | 0250 | Persistance et éviction du stockage | OPFS, `persist()`, avertissement |
| 9 | 0251 | Route du shell vers les apps à capacité d'exécution (240 D, 252 à 255 A ; fichier `0251-shell-route-to-execute-capable-apps.md`) | `./setup.exe`, `./hello.exe`, DOS, exception du Terminal |
| 10 | 0252 | Le Terminal affiche un programme invité | `x86 shell`, console Wine, DOS texte (215 A, 216 A) |
| 11 | 0253 | Expéditeur marqué | journal |
| 12 | 0254 | Événements par usager | journal personnel (137) |
| 13 | 0255 | Relais : identités et décisions d'une Feature | réseau par bouteille (174 A) |

Chaque ADR suit le format de Kazo (statut, contexte, décision, options écartées, conséquences, question posée), en français avec miroir anglais, et porte la mention « Aucun jeu sur ce repository » là où Kazo l'exige. Les numéros sont réservés dans la mémoire de Kazo au premier commit de la série.

## 3. Os-x86 : chantiers de la série `j0/base`

Étape a (Linux) :

| # | Branche | Contenu | Dépend de | Fini quand |
|---|---|---|---|---|
| 1 | `j0/1-scaffold` | Solution `kazoos-solution` (sous-module Kazo sur un tag), projets `OsX86.Interface`, `OsX86`, `OsX86.Feature` (commande `x86` en attendant le type Feature), hôte de développement, tests, CI, `docs/fr` et `docs/en`, `features.md`, changelog, suivi de l'API publique | — | `dotnet build` et `dotnet test` verts ; l'hôte démarre avec la commande `x86 --version` |
| 2 | `j0/2-engine-v86` | v86 vendorisé (BSD, avis, correctifs locaux dont #1661), Web Worker, pont OffscreenCanvas, ports série et virtio-console, état d'exécution, abstraction `IEngine` | 1 | une image Alpine de test démarre dans le worker depuis l'OPFS, console série lue et écrite |
| 3 | `j0/3-storage` | OPFS (`SyncAccessHandle`), manifeste d'images (SHA-256 par morceau de 32 Mio), Web Worker de téléchargement (203 A : `SubtleCrypto`, reprise, `cache: 'no-store'`), formats versionnés (section 13), verrou d'écriture, `flush()` | 1 | tests unitaires du manifeste et de la reprise ; une image de 800 Mio se télécharge, se vérifie et se reprend après coupure |
| 4 | `j0/4-images` | Recette Docker → 9p/zst : Linux de base (Debian trixie i386 + noyau `linux-virt` d'Alpine + init + agent, 201 A, dégraissage), Alpine de test, FreeDOS ; collecte des sources ; durcissement (210 A) et son test ; publication des images de test sur GitHub Pages, des images réelles sur R2 (202 A, origine à obtenir) | — (en parallèle de 1 à 3) | images construites par la CI, manifestes publiés, test d'image vert |
| 5 | `j0/5-agent` | Agent invité en C statique (205 A : JSON par ligne, `id`, événements, cgroup par lancement 206 A, console par pty 213 A, `open-url`), client C# du protocole, tests d'intégration en console série | 2, 4 | `launch`, `kill`, `list-processes`, `mounts`, `open-console` passent les tests d'E7 depuis le C# |
| 6 | `j0/6-vm-and-command` | Registre des VM par usager (GUID, 190), cycle de vie (démarrage à la demande, arrêt à la dernière application, 166), limite de mémoire (165), `x86 vm`, `x86 shell` par l'agent (repli série pour une image sans agent, 212 A), profils `.x86profile` (183) | 2, 3, 5 | `x86 shell` ouvre un shell dans l'invité depuis le terminal (sortie ligne par ligne tant que l'ADR 0252 n'est pas livrée) |
| 7 | `j0/7-admin` | Rôle, pages VM et utilisateurs dans Paramètres → Fonctionnalités, authentification de l'OS par opération, `sudo x86 admin`, journal personnel (137) et journal d'administration | Kazo 0243, 0247, 0249, 0254 livrées | un admin voit la page VM et change la limite de mémoire ; un usager voit son journal |
| 8 | `j0/8-feature` | Passage de la commande `x86` au type Feature, permissions déclarées, interface enregistrée, résolution `GetServiceAsync<Ix86>()`, dépendances, persistance | Kazo 0243 à 0246, 0248, 0250 | la Feature s'installe depuis le Store, `Ix86` se résout depuis une app, null sans elle |

Étape b (Windows et DOS console) :

| # | Branche | Contenu | Dépend de | Fini quand |
|---|---|---|---|---|
| 9 | `j0/9-wine-image` | Image Wine (WineHQ 11.0 i386, Mono, Gecko, bouteille gabarit en fichiers, préchargement des fichiers chauds, 204 A), objectif 250 Mio zstd | 4 | boot à froid puis `wine cmd` ; premier programme < 60 s après l'invite (mesuré, sinon 152 reposée) |
| 10 | `j0/10-prefixes-and-detection` | Préfixes (200 B : `Prefix`, `PrefixId`, `--prefix`), usager invité par préfixe, choix du préfixe par Os-x86 (193), détection par en-tête puis extension (162 A), `x86 detect`, `x86 run`, `./hello.exe` console dans le terminal par pty (213 A), code de sortie (110), Ctrl+C (216 A) | 5, 6, 9 ; Kazo 0251, 0252 | `./hello.exe` écrit dans le terminal et rend son code |
| 11 | `j0/11-dos` | FreeDOS au terminal par la voie VGA à 33 ms (214 A), accord Échap (215 A), signal `screen-set-size` (217 A), message et arrêt sans BoxedWine | 4, 6 ; Kazo 0251, 0252 | un programme DOS texte tourne dans le terminal |
| 12 | `j0/12-network` | Commutateur Ethernet privé, espace réseau par préfixe, adaptateur relais (identité par flux), décisions, limites, journal, fenêtre de permission, page des règles, `x86 rules` (174 A, 176 A) | 5, 6 ; Kazo 0246, 0255 | une connexion réelle ouvre la fenêtre ; « Autoriser » passe, « Refuser » bloque, le journal l'enregistre |
| 13 | BoxedWine `j0/base` | App minimale : association `.exe`/`.com`/`.bat`/`.cmd`/`.msi`, lancement par `Ix86`, console, tutoriel ; `BoxedWine.Lib` (`IBwLib`) ; correction de la fiche du dépôt (§12) | 8, 10 ; Kazo 0248 | `./setup.exe` au terminal arrive dans BoxedWine qui le lance |

Démonstration de fin de J0 (section 8 du plan) : `x86 shell` ouvre un shell depuis le terminal ; `./hello.exe` écrit dans le terminal ; un programme DOS texte tourne ; une connexion réseau ouvre la fenêtre, « Autoriser » passe, « Refuser » bloque, le journal l'enregistre ; un admin voit la page VM des Paramètres.

## 4. Conventions de chaque PR

- Branche nommée par son contenu ; PR vers `j0/base` ; `j0/base` vers la branche principale à la fin du jalon, par toi.
- Tests étiquetés `unit`, `guest`, `e2e`, `perf` et déclenchés par chemins (section 11 du plan) ; couverture visée 80 % sur le C#.
- Documentation en français dans `docs/fr`, miroir en `docs/en`, `features.md`, changelog et `PublicAPI.Unshipped.txt` d'`Os-x86.Interface` tenus dans la même PR.
- Aucune donnée personnelle, aucun chemin de machine dans le dépôt ; le contenu téléchargé (v86, images, sources) est traité comme non fiable.
- Pas de contenu lié à un jeu dans les PR vers Kazo (ADR 0135 de Kazo).

## 5. Ce qui reste à obtenir de toi

Répondu le 2026-11-11 (219 à 221, A partout) : la série d'ADR `feature/base` dans Kazo-ca/Os puis son code (219 A) ; Kazo épinglé sur v3.2.1, 3.3.0 à sa publication (220 A) ; `j0/base` depuis `conception-os-x86` (221 A). À obtenir ensuite auprès de Kazo : l'origine d'hébergement R2 (202 A), l'accord pour un Web Worker JS et `cache: 'no-store'` (203 A).

**État de la série Kazo (2026-11-11)** : les 13 ADR sont écrites (PR [#436](https://github.com/Kazo-ca/Os/pull/436) à [#448](https://github.com/Kazo-ca/Os/pull/448) vers `feature/base`, [#435](https://github.com/Kazo-ca/Os/pull/435) réserve les numéros), rien n'est fusionné. Leurs 28 questions au mainteneur sont la [série 15](serie-15-envoyee.md) (222 à 250). Réponses reçues le même jour, en deux lots : 225 A avec précision, 233 B, 245 B (le point 10 de la section 6 du plan reste tel quel : le Terminal dépend de `Os-x86.Interface` ; ADR 0252 réécrite), puis **240 D** (Os-x86 détecte le type du programme, le shell demande aux apps à capacité d'exécution ; précisions 252 à 255 répondues A le même jour ; ADR 0251 réécrite et renommée `0251-shell-route-to-execute-capable-apps.md`), 222 A (identifiants `ca.kazo.x86`, changés dans le squelette par la branche `j0/1b-ids`), 251 A et A partout ailleurs. Le chantier 1 d'Os-x86 est fusionné dans `j0/base` ([#2](https://github.com/Kazo-ca/Os-x86/pull/2)) ; le chantier 1b (identifiants `ca.kazo.x86`, [#3](https://github.com/Kazo-ca/Os-x86/pull/3)) attend ta fusion.

**Les 13 ADR sont fusionnées dans `feature/base` le 2026-11-11** (sur ton accord « Oui, fusionne » ; [#436](https://github.com/Kazo-ca/Os/pull/436) à [#448](https://github.com/Kazo-ca/Os/pull/448), dans l'ordre, tête `6f266277` ; les lignes réservées par [#435](https://github.com/Kazo-ca/Os/pull/435) dans l'index des ADR et `features.md` remplacées ligne par ligne par la vraie entrée ; vérifié après coup : aucune ligne réservée restante, 30 fichiers sans lien cassé, aucun mot lié à un jeu ni donnée personnelle). Deux faits du code de Kazo relevés en écrivant 0251 : Kazo n'appelle jamais la classe d'une app installée sans la lancer, donc une app candidate qui ne peut pas exécuter le programme **refuse au lancement** (`AppLaunchResult.Refused`, candidat suivant, sinon 126) et il n'y a pas de `CanExecuteAsync` ; le détecteur de format est un type nommé chargé sans contexte d'app, sur le précédent des composants de l'ADR 0148 ; Kazo n'a pas de table extension → type MIME (`xdg-mime query filetype` prend le `MediaType` du premier `FileHandler` qui déclare l'extension, sinon `application/octet-stream`). Prochaine étape dans Kazo : le code des 13 ADR, un chantier par ADR.

**Chantiers en cours (2026-11-11, lancés en parallèle sur ton accord)** : Os-x86 chantier 2, PR [#4](https://github.com/Kazo-ca/Os-x86/pull/4) `j0/2-engine-v86` vers `j0/base` (v86 0.5.473 vendorisé avec le correctif #1661, worker et `IEngine`, image Alpine de test depuis l'ISO officiel, 146 tests `unit` à 89,7 % de couverture, test `guest` vert en local et en CI ; non vérifié : chargement dans Kazo, Firefox et Safari, son) ; Kazo `feature/14-feature-app-type-code`, PR [#449](https://github.com/Kazo-ca/Os/pull/449) vers `feature/base` (section « Conséquences » de l'ADR 0243 en entier ; sept échecs de tests préexistants propres à Windows, identiques sans le chantier ; non vérifié : E2E, publication trimmée, Paramètres dans le navigateur ; `ProvidedServices` relève de l'ADR 0245). Les trois questions de ce chantier sont la [série 16](serie-16-envoyee.md) (256 à 258), répondues le même jour : 256 B (`kind` dans le `catalog.json` du Store dès maintenant, bouton « Installer pour moi » caché pour une Feature ; correctif sur #449 et PR dans Os-AppMarketPlace), 257 A, 258 A. 256 B appliquée le même jour : commit `dcc249c0` sur #449 et PR [Os-AppMarketPlace#20](https://github.com/Kazo-ca/Os-AppMarketPlace/pull/20) (doc du format du catalogue), toutes deux en attente de ta fusion. Les deux PR attendent ta fusion ; #4 contient la base de #3.
