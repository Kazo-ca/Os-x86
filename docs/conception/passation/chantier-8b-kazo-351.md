# Chantier 8b (j0/8b-kazo-351) : note de passation (2026-11-18)

PR `j0/8b-kazo-351` → `j0/base`. Suite du chantier 8 ([note](chantier-8-feature.md)), décidée par
291 B : épingler Kazo 3.5.1 et retirer le contournement provisoire de Kazo-ca/Os#621.

## Ce qui change

- **Kazo 3.5.1** épinglé : sous-module `external/KazoOs` sur `v3.5.1`. Le manifeste de la Feature et
  de ses enfants demande le SDK `3.5.1` (généré par la build).
- **Contournement retiré** : `src/OsX86.Os/FeatureChildDescriptors.cs`, `FeatureChildPreload.cs`,
  leur appel dans `Program.cs`, leur lien de compilation dans `OsX86.Tests.csproj` et
  `FeatureChildDescriptorsTests` (qui affirmait que Kazo refusait encore l'enfant seul).
- **Nouveau test** `tests/OsX86.Tests/FeatureChildLaunchTests.cs`. Il passe par le vrai registre
  paresseux de Kazo (`BuildKnownLazyAppDescriptorRegistry`, paquet `Kazo.Os.Platform.Blazor`
  ajouté aux tests) :
  - `ca.kazo.x86.cli` est trouvé avec sa Feature et `x86 --version` répond ;
  - `ca.kazo.x86.host` est trouvé avec sa Feature, et la Feature elle-même n'est pas chargée.

## Vérifié

- `dotnet build OsX86.sln -c Release -warnaserror` : 0 avertissement.
- Tests `unit` : 917 réussis, 4 ignorés ; couverture des lignes de 89,58 % (seuil 80 %).
- Hôte de développement sur Kazo 3.5.1, session invitée, sans contournement :
  - `x86 --version` répond `Os-x86 0.1.0` ;
  - `x86 vm list` répond.

## Piège trouvé : le service `host` ne démarre plus à la connexion

> **Corrigé au chantier 8c** : Kazo 3.5.2 (Kazo-ca/Os#625, ADR 0286) démarre ce service à la
> connexion. Voir [la note du chantier 8c](chantier-8c-kazo-352.md).

`systemctl status ca.kazo.x86.host` montre `inactive (dead)` après la connexion, alors qu'il était
actif au chantier 8.

- Cause, dans Kazo : `AppLifecycleOrchestrator.AutomaticServiceCandidates` ne démarre que les
  services dont le descripteur est déjà chargé. L'assemblage de la Feature n'est chargé qu'au
  premier lancement d'un de ses enfants. Au chantier 8, c'est le contournement qui chargeait les
  descripteurs avant la connexion, d'où le service actif.
- Effet :
  - la commande `x86` installe l'hôte elle-même (288 A) ;
  - l'hôte de développement installe le sien avant tout ;
  - mais une app qui appelle `Ix86` avant toute commande `x86` reçoit `engine-unavailable`.
- Ce défaut touchait déjà une Feature installée par kazopkg ou le Store, où le contournement
  n'existait pas.
- La suite reste à décider : une question est à poser, et peut-être une issue Kazo à ouvrir
  (demander avant). Documenté dans `docs/fr/vm.md` et `docs/en/vm.md`.

## Remarque

À l'ouverture de la session invitée, l'avis d'éviction de 289 A s'est affiché : l'OPFS de l'hôte de
développement n'avait plus le marqueur `osx86/.kazoos-marker`, ni de registre. Ce chantier n'a pas
cherché pourquoi : l'avis est le comportement prévu quand le marqueur manque.
