# Chantier 8 (j0/8-feature) : note de passation (2026-11-17)

PR `j0/8-feature` → `j0/base`. Références : `docs/fr/vm.md` (registre, hôte), `docs/fr/stockage.md`
(état de chaque usager), miroirs anglais dans `docs/en/`.

## Ce que la suite peut prendre tel quel

- **Kazo 3.5.0** épinglé partout (sous-module `external/KazoOs` sur `v3.5.0`, paquets, docs).
- **La Feature** `ca.kazo.x86` (`src/OsX86.Feature/X86FeatureApp.cs`, `FeatureDefinition`, ADR 0260) :
  - enfant `ca.kazo.x86.cli` : la commande `x86`, sur le fil principal ;
  - enfant `ca.kazo.x86.host` : service caché, démarré à chaque ouverture de session sur le fil
    principal (`X86HostService`) ;
  - permissions `x86.programs.launch`, `x86.vm.use` (normales), `x86.vm.destroy`, `x86.vm`
    (dangereuses, `x86.vm` couvre les deux autres) sous le préfixe `x86` (`X86Permissions`) ;
  - rôle `x86:vm-manager` déclaré, `Grants` vide : son usage est pour le chantier 7 ;
  - aucune dépendance.
- **`Ix86` par Kazo** : `ProvidedServices` donne `X86FeatureService`. Une app l'obtient par
  `KazoFeatures.GetServiceAsync<Ix86>()`, et null sans la Feature : testé avec le vrai
  `FeatureServiceResolver` (`FeatureResolutionTests`).
  - Chaque appel vérifie la permission de l'app appelante. `GetVms`, `StartVm`, `StopVm`,
    `OpenConsole` et l'événement exigent `x86.vm.use`, `RunProgram` exige `x86.programs.launch`,
    `Detect` n'exige rien.
  - Chaque appel agit pour `Caller.UserId`, et un usager ne voit pas les VM d'un autre.
  - `OsX86.Interface` est une bibliothèque partagée (ADR 0261).
- **Hôte de la page** (288 A) : `X86Host.EnsureInstalled(IJSRuntime)` appelle
  `VmHost.TryConfigure`. Elle installe le moteur v86 et le stockage OPFS si aucun hôte ne l'a fait.
  - Elle est appelée par le service `host` et par la commande, que Kazo construit avec
    l'`IJSRuntime` de la page (constructeur `(AppManifest, IJSRuntime)`).
  - `X86Runtime.Services` refait les services faits sans hôte dès qu'un hôte arrive. Le catalogue
    d'images de la Feature installée est vide tant que l'hébergement des images n'est pas réglé.
- **État de chaque usager** (287 A) : `UserStateStore` (`IVmRegistryStore` et `INetworkStore`) range
  `registry.json`, `network-rules.json` et `network-journal.json` sous `osx86/users/<guid>/`.
  - Le worker de stockage écrit ces fichiers par les messages `read-user-file` et
    `write-user-file`, avec une écriture journalisée (`<nom>.tmp`).
  - `KazoVmRegistryStore` et `KazoNetworkStore` (`~/.config/ca.kazo.x86/`) sont supprimés.
- **KazoStorage** (ADR 0250, 289 A) : `FeatureStorageGuard`.
  - Au premier `x86 vm create`, il demande la persistance, écrit le marqueur
    `osx86/.kazoos-marker` et l'enregistre, puis déclare les octets de tout `osx86/`.
  - Le premier `x86` après une éviction ou un retrait de la protection le dit une fois sur la
    sortie d'erreur, dans la langue de l'OS (`Resources/Strings.resx` et `Strings.fr.resx`).
- **Ressources web** (290 B) : v86, les workers du moteur et du stockage sont dans
  `src/OsX86.Feature/wwwroot`, servis à `_content/OsX86.Feature/`. Le paquet `.kazoapp` les contient.

## Choix faits et provenance

| # | Décision | Provenance |
| --- | --- | --- |
| 287 | **A** : état de chaque usager dans l'OPFS, `osx86/users/<guid>/` | L'usager, dans la session du chantier : « Je suis d'accord avec tes recommendations » |
| 288 | **A** : enfant Service `ca.kazo.x86.host` ; la commande installe aussi | Même réponse que 287 |
| 289 | **A** : avis d'éviction une fois par `x86` ; liste vide sans erreur ; avis du retrait de protection | L'usager, dans la session du chantier : « 289 . a) mais le message d'erreur est localisé à la langue utilisé par l'OS » |
| 290 | **B** : `wwwroot` déplacé dans `OsX86.Feature` | L'usager, même message : « 290. B » |
| 291 | **B** : contournement de Kazo-ca/Os#621 dans l'hôte de développement seulement | Relayée par la session d'orchestration : « 291. b » |

Questions et réponses : [série 25](../serie-25-envoyee.md) et le journal des décisions. Pour 291 B,
le journal parle d'un `IAppDescriptorLoader` propre à l'hôte de développement. Ce n'était pas
possible : Kazo utilise directement le registre concret, scellé. L'hôte précharge donc les
descripteurs des enfants dans ce registre (`FeatureChildPreload`), ce qui a le même effet.

Sans question, d'après les décisions existantes :
- les permissions de 176 A et leur usage par opération ;
- `network.real.access` pas encore déclarée : le relais de Kazo n'est pas codé ;
- le rôle de 281 A ;
- la bibliothèque partagée de l'ADR 0261.

## Pièges trouvés

- **Kazo 3.5.0 ne lance pas l'enfant d'une Feature installée** (Kazo-ca/Os#621). Le catalogue est
  construit avec le seul manifeste de l'enfant (`catalog.feature.parent.missing`). Contournement
  **provisoire** (291 B) : `FeatureChildPreload` et `FeatureChildDescriptors` dans `src/OsX86.Os/`.
  Ils découvrent les enfants avec leur Feature et les ajoutent aux descripteurs. Seul l'hôte de
  développement le fait. À retirer quand Os-x86 s'épinglera sur Kazo 3.5.1.
- **Dossier privé exact** : Kazo compare l'identifiant exact de l'app. Celui de la commande est
  donc `~/.config/ca.kazo.x86.cli/`, d'où 287.
- **KOSAPP0100** : `ConfigureAwaitOptions` est absent du runtime rogné de Kazo ; `AgentClient` ne
  l'utilise plus.
- **Ressources web** : `KazoOsPack` n'emballe que le `wwwroot` du projet de l'app (ADR 0221), d'où 290.
- **Revue du code** :
  - écriture d'un fichier d'usager non atomique : corrigé par le journal `.tmp` ;
  - `VmHost.Current` lu trois fois dans `X86Runtime.Services` : corrigé, une seule lecture.

## Vérifié

- `dotnet build OsX86.sln -c Release -warnaserror` : 0 avertissement.
- Tests `unit` : 918 réussis, 4 ignorés ; couverture des lignes de 89,56 % (seuil 80 %).
- `vendor-v86.py --check` passe.
- `UserFileStorageTests` (test `guest`) dans Chromium sans tête :
  - écriture et relecture ;
  - écriture coupée, rendue par le journal ;
  - chemins, noms et textes hors règle refusés.
- Dans l'hôte de développement (session invitée) :
  - `systemctl status ca.kazo.x86.host` montre le service actif depuis la connexion ;
  - `x86 vm create essai --profile alpine-test` réussit (il échouait avec `CapabilityDenied`) ;
  - le registre est dans `osx86/users/<32 hex>/registry.json`, avec le marqueur à côté ;
  - après un rechargement, `x86 vm list` montre toujours la VM ;
  - `x86 vm remove` la supprime.
- Le paquet `.kazoapp` contient les manifestes (`entrypoints/cli.json`, `entrypoints/host.json`),
  `lib/` avec le satellite `fr`, `OsX86.Interface` partagée et `wwwroot`.

## Ce qui reste

- **Retirer le contournement de #621** au passage à Kazo 3.5.1, et le dire dans la PR de ce passage.
- **Non vérifié dans un navigateur** : `X86Host` dans une Feature installée par kazopkg ou le Store.
  L'hôte de développement installe le sien avant, et le service le garde. Cette voie n'est couverte
  que par les tests `unit` (`X86HostTests`).
- **Lecture des fichiers de l'usager** (`x86 profile import|export`, programmes DOS ou Windows dans
  `~/`) : la commande ne déclare que `filesystem.private.*`, et son dossier privé est maintenant
  `~/.config/ca.kazo.x86.cli/`. La permission à déclarer reste à décider (une question à poser).
- **Permissions privées de la Feature** : elles ne servent plus à l'état, gardées en attendant ce
  choix.
- **Test de bout en bout de l'éviction** (276 A, « avec le chantier 8 ») : pas fait.
- **Images de la Feature installée** : catalogue vide tant que l'hébergement n'est pas réglé.
- Restent pour plus tard, comme prévu : `navigator.deviceMemory`, VM et enfants comme processus de
  Kazo (207 A), publication sur Os-AppMarketPlace (à demander avant).
