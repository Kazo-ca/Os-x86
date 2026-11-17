# Jalon 0 — où on en est (2026-11-17)

**Repris le 2026-11-14**, après une pause du 12 au 14 novembre. Les sessions de travail tournent sur Opus 5.5 ; Fable est réservé aux analyses.

Résumé simple, tenu à jour à chaque fusion. Le détail est dans [jalon-0-execution.md](jalon-0-execution.md).

## Fait

**Conception** : terminée. 262 décisions numérotées, 17 séries de questions répondues, plan et registre validés.

**Kazo (dépôt Kazo-ca/Os, série `feature/base`)**
- 13 ADR écrites et fusionnées (les trois premières renumérotées 0260 à 0262 par Kazo, les autres 0246 à 0255), plus l'ajustement 0256.
- Code de l'ADR 0260 (ex-0243), le type d'app « Feature » : fusionné (PR #449).
- Catalogue du Store avec le champ `kind` (256 B) : documentation fusionnée (Os-AppMarketPlace #20), code fusionné avec #450.
- Code de l'ADR 0261 (ex-0244), bibliothèques partagées, avec l'ADR d'ajustement 0256 (série 17) : fusionné (PR #450), avec le code du champ `kind`.
- Code de l'ADR 0262 (ex-0245), services des Features et `GetServiceAsync<T>()` : fusionné (PR #541).
- Code de l'ADR 0246, permissions déclarées par un fournisseur : fusionné (PR #570, série 19).
- Code de l'ADR 0248, résolution des dépendances par le Store et kazopkg : fusionné (PR #588, série 20).
- Code de l'ADR 0250, persistance et éviction du stockage : fusionné (PR #594, série 21).
- Code de l'ADR 0247, rôles déclarés par une Feature, avec l'ajustement 0283 (`KazoUser.IsInRole`) : fusionné (PR #604, séries 22 et 23). Issue de suivi : Kazo-ca/Os#607.
- Kazo 3.5.0 publiée le 2026-11-17 (Kazo #613 puis #615) : code des ADR 0246, 0248, 0250, 0247 et 0283, plus les fournisseurs de fichiers tiers (ADR 0282, 0284). C'est la version que le chantier 8 épinglera.
- Tout ce code est dans `main` de Kazo et publié en `v3.4.0-preview.2` à `.4`.

**Os-x86 (dépôt Kazo-ca/Os-x86, série `j0/base`)**
- Chantier 1, squelette : solution, projets, hôte de développement, CI, identifiant `ca.kazo.x86`.
- Chantier 2, moteur : v86 vendorisé dans un Web Worker, console série, ports virtio-console, `IEngine`.
- Chantier 3, stockage : manifeste d'image, téléchargement vérifié par morceaux de 32 Mio avec reprise, dépôt dans l'OPFS, verrou, formats versionnés.
- Chantier 4, images : outil de paquetage zstd, recettes `linux-base` (Debian i386, 73 Mo), Alpine de test (15 Mo), FreeDOS (17 Mo), durcissement et son test, publication préparée.
- Chantier 5, agent : agent invité en C statique dans l'image, client C# du protocole, consoles, tests dans l'image Alpine.
- Chantier 1c, Kazo épinglé sur `v3.4.0` stable (266 A) : fusionné (PR #10).
- Chantier 10b, une ligne tapée arrive toujours au programme Windows (console Wine en mode brut à l'ouverture) : fusionné (PR #17).
- Chantier 12, réseau privé entre VM, espace réseau par préfixe, règles, limites et journal, `x86 rules`, scénario de la fenêtre avec un relais de test : fusionné (PR #16).
- Chantier 11, programmes DOS texte au terminal par `x86 run` (VM FreeDOS sans agent, touche jusqu'à l'écran 48 ms) : fusionné (PR #15).
- Chantier 10, préfixes, détection du type de programme, `x86 detect`, `x86 run` avec console, code de sortie et Ctrl+C (premier `x86 run` 37 s téléchargement compris, second 3,4 s) : fusionné (PR #14).
- Chantier 9, image Wine (WineHQ 11.0 i386, Mono, Gecko, bouteille gabarit, préchargement ; premier programme 13 à 17 s après l'invite ; 349 Mio) : fusionné (PR #12).
- Chantier 6c, connexion de l'agent qui accepte une réponse tardive, tests VM stables : fusionné (PR #13).
- Chantier 6b, `linux-base` image par défaut de `x86 vm create` et `x86 shell`, source d'images configurable, couche d'écriture par VM : fusionné (PR #11).
- Chantier 6, registre des VM par usager, cycle de vie, budget de mémoire, profils `.x86profile`, `x86 vm`, `x86 shell` par l'agent : fusionné (PR #9).
- Chantier 6a, disque `hda` depuis l'OPFS : `linux-base` démarre avec son agent (22 contrôles sur 22), couche d'écriture, FreeDOS démarre : fusionné (PR #8).

## En cours

- **Chantier 8**, branche `j0/8-feature` : la commande `x86` passe au type Feature (Kazo 3.5.0 épinglé, permissions et rôle déclarés, `Ix86` résolu par `GetServiceAsync`, persistance KazoStorage). Lancé le 2026-11-17 ; ses questions sont la [série 25](serie-25-envoyee.md) (287 à 291).
- **Kazo, code de l'ADR 0249**, section « Fonctionnalités » des Paramètres : PR Kazo #617 ouverte vers `feature/base`, ADR d'ajustement 0285 ; ses questions sont la [série 24](serie-24-envoyee.md) (283 à 286).
- **Kazo, PR #620** : `main` (3.5.0) fusionnée dans `beta`, qui passe en `3.6.0-preview.1`.

## Reste à faire

**Os-x86, étape a (Linux)**
- Chantier 7, pages d'administration et rôle (attend le code Kazo des ADR 0249 et 0254).

**Os-x86, étape b (Windows et DOS console)**
- Chantier 10, suite : `./hello.exe` tapé dans le shell de Kazo (attend le code Kazo des ADR 0251 et 0252).
- Chantier 11, suite : programme DOS tapé dans le shell de Kazo (attend le code Kazo des ADR 0251 et 0252).
- Chantier 12, suite : relais réel de Kazo et fenêtre de permission (attend le code Kazo des ADR 0255 et 0246 dans une version publiée).
- Chantier 13, BoxedWine minimal (autre dépôt).

**Kazo** : le code des ADR 0249 et 0251 à 0255, un chantier par ADR, dans l'ordre 0249 (en cours), 0254, 0251, 0252, 0253, 0255 ; puis une version publiée de Kazo pour y épingler Os-x86.

**À régler par toi**
- Répondre à la série 24 (283 à 286), puis fusionner Kazo #617.
- Activer GitHub Pages sur Os-x86 (source « GitHub Actions ») et la variable `OSX86_PUBLISH_PAGES=true`.
- Fournir l'origine R2 et les secrets `OSX86_R2_*` quand les vraies images seront prêtes.

## Démonstration de fin de jalon 0

`x86 shell` ouvre un shell depuis le terminal ; `./hello.exe` écrit dans le terminal ; un programme DOS texte tourne ; une connexion réseau ouvre la fenêtre de permission ; un admin voit la page VM des Paramètres.
