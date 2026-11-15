# Jalon 0 — où on en est (2026-11-15)

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
- Tout ce code est dans `main` de Kazo et publié en `v3.4.0-preview.2` à `.4`.

**Os-x86 (dépôt Kazo-ca/Os-x86, série `j0/base`)**
- Chantier 1, squelette : solution, projets, hôte de développement, CI, identifiant `ca.kazo.x86`.
- Chantier 2, moteur : v86 vendorisé dans un Web Worker, console série, ports virtio-console, `IEngine`.
- Chantier 3, stockage : manifeste d'image, téléchargement vérifié par morceaux de 32 Mio avec reprise, dépôt dans l'OPFS, verrou, formats versionnés.
- Chantier 4, images : outil de paquetage zstd, recettes `linux-base` (Debian i386, 73 Mo), Alpine de test (15 Mo), FreeDOS (17 Mo), durcissement et son test, publication préparée.
- Chantier 5, agent : agent invité en C statique dans l'image, client C# du protocole, consoles, tests dans l'image Alpine.

## En cours

- **Chantier 6a**, disque `hda` lu depuis l'OPFS, couche d'écriture, démarrage de `linux-base` et de FreeDOS dans le moteur : repris le 2026-11-14.
- **Chantier 6**, registre des VM, cycle de vie, budget de mémoire, profils, `x86 vm`, `x86 shell` : repris le 2026-11-14.
- **Kazo, code de l'ADR 0246**, permissions déclarées par un fournisseur : lancé le 2026-11-15, branche `feature/17-provider-declared-permissions-code`.

## Reste à faire

**Os-x86, étape a (Linux)**
- Finir 6a et 6, puis brancher `linux-base` comme image par défaut.
- Chantier 7, pages d'administration et rôle (attend le code Kazo des ADR 0247, 0249, 0254).
- Chantier 8, passage au vrai type Feature, permissions, interface enregistrée, installation depuis le Store (attend le code Kazo des ADR 0245, 0246, 0248, 0250).

**Os-x86, étape b (Windows et DOS console)**
- Chantier 9, image Wine (peut commencer tout de suite, ne dépend que du chantier 4).
- Chantier 10, préfixes et détection, `./hello.exe` dans le terminal (attend 9 et le code Kazo des ADR 0251, 0252).
- Chantier 11, FreeDOS au terminal.
- Chantier 12, réseau privé et relais, fenêtre de permission.
- Chantier 13, BoxedWine minimal (autre dépôt).

**Kazo** : le code des ADR 0248 à 0255, un chantier par ADR, dans l'ordre 0248, 0250, 0247, 0249, 0254, 0251, 0252, 0253, 0255 ; puis une version publiée de Kazo pour y épingler Os-x86.

**À régler par toi**
- Répondre à la série 18 (263 à 266), dont l'épinglage de Kazo dans Os-x86.
- Activer GitHub Pages sur Os-x86 (source « GitHub Actions ») et la variable `OSX86_PUBLISH_PAGES=true`.
- Fournir l'origine R2 et les secrets `OSX86_R2_*` quand les vraies images seront prêtes.

## Démonstration de fin de jalon 0

`x86 shell` ouvre un shell depuis le terminal ; `./hello.exe` écrit dans le terminal ; un programme DOS texte tourne ; une connexion réseau ouvre la fenêtre de permission ; un admin voit la page VM des Paramètres.
