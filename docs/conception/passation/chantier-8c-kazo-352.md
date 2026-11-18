# Chantier 8c (j0/8c-kazo-352) : note de passation (2026-11-18)

PR `j0/8c-kazo-352` → `j0/base`. Suite du chantier 8b ([note](chantier-8b-kazo-351.md)), décidée par
293 A : épingler Kazo 3.5.2, qui corrige Kazo-ca/Os#625 (PR Kazo #629, ADR 0286 de Kazo).

## Ce qui change

- **Kazo 3.5.2** épinglé : sous-module `external/KazoOs` sur `v3.5.2`. Le manifeste de la Feature et
  de ses enfants demande le SDK `3.5.2` (généré par la build).
- **Le service `ca.kazo.x86.host` démarre à la connexion**, sans commande `x86` préalable. Kazo 3.5.2
  démarre tout service automatique activé du catalogue, en chargeant son code à ce moment ; un
  service manuel ou désactivé ne charge rien.
- **Nouveau test** `Ix86_has_an_emulator_once_the_host_service_started_without_any_x86_command`
  (`tests/OsX86.Tests/X86HostTests.cs`). Le gestionnaire que `Ix86` utilise
  (`X86Runtime.Services.Manager`) n'a pas d'émulateur avant le service
  (`UnavailableEngineFactory`, d'où `engine-unavailable`). Il a celui de la page dès que le service
  a démarré, sans commande `x86` (`JsEngineFactory`).
- **Docs** :
  - la limite notée au chantier 8b est retirée de `vm.md` et remplacée par le démarrage à la
    connexion ;
  - `features.md`, `README.md` et le changelog parlent de 3.5.2 ;
  - la note du chantier 8b renvoie ici.

## Vérifié

- `dotnet build OsX86.sln -c Release -warnaserror` : 0 avertissement.
- Tests `unit` : 918 réussis, 4 ignorés ; couverture des lignes de 89,56 % (seuil 80 %).
- Hôte de développement sur Kazo 3.5.2, session invitée neuve :
  - `systemctl status ca.kazo.x86.host`, première commande tapée, montre `active (running)` depuis la
    connexion ;
  - `x86 --version` répond ensuite `Os-x86 0.1.0`.

## Pas prouvé par ces essais

- Le démarrage du service par l'orchestrateur de Kazo n'a pas de test `unit` dans Os-x86. Il
  faudrait rebâtir un noyau Kazo complet, et Kazo le teste lui-même
  (`AppLifecycleOrchestratorTests.LazyServices`). C'est l'hôte de développement qui le montre.
- Dans l'hôte de développement, `Program.cs` installe son propre hôte avant tout. Le chemin où
  `Ix86` dépend du service n'y est donc pas observable. Il l'est par le test `unit` ci-dessus et
  par `X86HostTests`.
- La Feature installée par kazopkg ou le Store n'a pas été essayée dans un navigateur.

## Piège

Les paquets `Kazo.Os.*` 3.5.2 sont arrivés sur nuget.org les uns après les autres, quelques minutes
après la publication. Une restauration faite trop tôt garde « introuvable » dans le cache HTTP de
NuGet : relancer avec `dotnet restore --no-http-cache`.
