# Jalon 0 — où on en est (2026-11-12)

Résumé simple, tenu à jour à chaque fusion. Le détail est dans [jalon-0-execution.md](jalon-0-execution.md).

## Fait

**Conception** : terminée. 262 décisions numérotées, 17 séries de questions répondues, plan et registre validés.

**Kazo (dépôt Kazo-ca/Os, série `feature/base`)**
- 13 ADR (0243 à 0255) écrites et fusionnées.
- Code de l'ADR 0243, le type d'app « Feature » : fusionné (PR #449).
- Catalogue du Store avec le champ `kind` (256 B) : documentation fusionnée (Os-AppMarketPlace #20) ; le code est dans la PR #450.
- Code de l'ADR 0244, bibliothèques partagées, avec l'ADR d'ajustement 0256 : PR #450 prête, CI verte, **à fusionner par toi**.

**Os-x86 (dépôt Kazo-ca/Os-x86, série `j0/base`)**
- Chantier 1, squelette : solution, projets, hôte de développement, CI, identifiant `ca.kazo.x86`.
- Chantier 2, moteur : v86 vendorisé dans un Web Worker, console série, ports virtio-console, `IEngine`.
- Chantier 3, stockage : manifeste d'image, téléchargement vérifié par morceaux de 32 Mio avec reprise, dépôt dans l'OPFS, verrou, formats versionnés.
- Chantier 4, images : outil de paquetage zstd, recettes `linux-base` (Debian i386, 73 Mo), Alpine de test (15 Mo), FreeDOS (17 Mo), durcissement et son test, publication préparée.
- Chantier 5, agent : agent invité en C statique dans l'image, client C# du protocole, consoles, tests dans l'image Alpine.

## En cours

- **Chantier 6a**, disque `hda` lu depuis l'OPFS, couche d'écriture, démarrage de `linux-base` et de FreeDOS dans le moteur. Session arrêtée par la limite d'usage ; premiers fichiers écrits, rien de commité.
- **Chantier 6**, registre des VM, cycle de vie, budget de mémoire, profils, `x86 vm`, `x86 shell`. Même état.

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

**Kazo** : le code des ADR 0245 à 0255, un chantier par ADR, dans l'ordre 0245, 0246, 0248, 0250, 0247, 0249, 0254, 0251, 0252, 0253, 0255 ; puis une version publiée de Kazo pour y épingler Os-x86.

**À régler par toi**
- Fusionner Kazo #450.
- Activer GitHub Pages sur Os-x86 (source « GitHub Actions ») et la variable `OSX86_PUBLISH_PAGES=true`.
- Fournir l'origine R2 et les secrets `OSX86_R2_*` quand les vraies images seront prêtes.
- Reprendre les deux sessions du chantier 6 quand la limite d'usage sera levée (je m'en charge sur un mot de toi).

## Démonstration de fin de jalon 0

`x86 shell` ouvre un shell depuis le terminal ; `./hello.exe` écrit dans le terminal ; un programme DOS texte tourne ; une connexion réseau ouvre la fenêtre de permission ; un admin voit la page VM des Paramètres.
