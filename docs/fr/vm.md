> [English version](../en/vm.md)

# Les VM, leur cycle de vie et la commande `x86`

Jalon 0, chantier 6 (`j0/6-vm-and-command`). Ce chantier pose la couche VM au-dessus du moteur
(chantier 2), du stockage (chantier 3) et de l'agent invité (chantier 5) : le registre des VM par
usager, leur cycle de vie, la limite de mémoire, les profils `.x86profile`, et les commandes
`x86 vm`, `x86 shell` et `x86 profile` ([ordre du jalon](../conception/jalon-0-execution.md)).
Décisions : 165 A (plusieurs VM, limite de mémoire), 166 A (ce qu'est une « application »),
183 A (profils), 190 A (rangement par identifiant stable d'usager), 200 B (`prefix`), 206 A et
207 A (lancements et processus), 212 A (image sans agent), 213 A (pty), 216 A (Ctrl+C).

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Vm/` | `VmRegistry` et son document (`VmRegistryDocument`, `VmRecord`, `IVmRegistryStore`), `VmManager` (les VM de la machine, le budget), `VmInstance` (le cycle de vie d'une VM), `VmApplication`, les consoles (`VmConsoles.cs`), `MemoryBudget`, `IEngineFactory` et `IBootImageSource`, `X86Service` (l'implémentation d'`Ix86`) |
| `src/OsX86/Profiles/` | `VmProfile` (format `.x86profile`), `BuiltInProfiles` (`alpine-test`, `linux-base`) |
| `src/OsX86.Feature/` | `X86Command` en quatre fichiers (aiguillage, `vm`, `shell`, `profile`), `CommandArguments`, `TerminalTextFilter`, `X86Runtime` (le gestionnaire partagé), `X86Files` (fichiers de Kazo, magasin du registre) |
| `src/OsX86.Interface/` | `Ix86` étendue, `IVmConsole`, `VmConsoleOptions`, `X86Exception` et `X86ErrorCodes`, `VmMemoryBudgetExceededException` |
| `tests/OsX86.Tests/Vm/` | tests `unit` (moteur et agent simulés, horloge manuelle) et test `guest` (`VmShellGuestTests`, `vm.html`) |

## Vue d'ensemble

```
x86 (commande) ─┐
Ix86 (apps)  ───┴─> VmManager ──> VmRegistry ──> IVmRegistryStore (un document par usager)
                        │
                        ├──> MemoryBudget (165 A)
                        └──> VmInstance (une par VM) ──> IEngineFactory ──> IEngine (un par marche)
                                   │                        └──> AgentClient (EngineAgentChannel)
                                   └──> applications, enfants, surveillance, consoles
```

Un `VmManager` par hôte tient les VM de toute la machine : le budget de mémoire compte celles de
tous les usagers. Chaque opération nomme l'usager et n'atteint que ses VM (190 A) ; une VM d'un
autre usager répond `vm-not-found`, exactement comme une VM qui n'existe pas. Les droits
d'administration sur les VM des autres sont le chantier 7.

## Le registre (190 A)

Une VM a un identifiant GUID, un nom, une date de création et son profil (genre, image et
version, mémoire, port série déclaré, préfixe par défaut) ; son état n'est pas rangé, il vit en
mémoire. Le registre est **un document par identifiant stable d'usager**, jamais par nom de
connexion : un compte supprimé puis recréé sous le même nom ne retrouve pas les VM de l'ancien.

- **Dans Kazo**, la commande range le document de l'usager dans le dossier privé de l'app,
  `~/.config/ca.kazo.x86/vms/<guid>.json` (ADR 0044 de Kazo ; permissions
  `filesystem.private.read` et `filesystem.private.write`, déclarées dans le manifeste). Le
  magasin refuse de lire ou d'écrire le document d'un autre usager que celui qui lance la
  commande. L'identifiant vient de `IAppExecutionContext.UserId` (les 32 chiffres hexadécimaux du
  `LocalUserId` de Kazo) ; un nom de connexion est refusé (`invalid-user`).
- **Format 1** (section 13 du plan), versionné comme les autres documents d'Os-x86 :

```json
{
  "format": 1,
  "user": "0000000000000000000000000000a001",
  "vms": [
    { "id": "3f2c…", "name": "Alpine", "created": "2026-11-12T10:00:00+00:00",
      "profile": { "format": 1, "name": "alpine-test", "kind": "linux", "image": "alpine-test",
                   "imageVersion": "0.1.0", "memoryMiB": 128, "serialConsole": true, "prefix": "test" } }
  ],
  "profiles": []
}
```

- Un format inconnu est refusé sans rien écrire (169). Sont aussi refusés le document d'un autre
  usager, un identifiant vide ou en double, deux noms égaux à la casse près, un profil invalide.
- Opérations : créer (depuis un profil copié dans la VM : la recette, 121), lister, trouver par
  GUID ou par nom (sans égard à la casse, le GUID l'emporte), renommer (permis en marche),
  supprimer (refusé en marche sauf avec `--force`, qui l'arrête d'abord). Un nom a 1 à 64
  caractères, sans caractère de contrôle ni blanc en tête ou en fin, unique pour l'usager.
- Le document est relu avant chaque opération et réécrit après chaque changement, sous un verrou.

## Le cycle de vie (166 A)

Une **application** garde la VM en marche : un programme lancé par l'agent, un shell interactif
(`x86 shell`), une console. `VmInstance.AcquireAsync` en prend une ; la première démarre la VM.

1. **Démarrage à la demande.** Image trouvée (`IBootImageSource`), budget vérifié, état
   `Starting`, moteur créé par la fabrique (`IEngineFactory`, injectable), `StartAsync`, puis
   connexion de l'`AgentClient` (`EngineAgentChannel.For(engine)`), puis `Running`. Les
   applications qui arrivent pendant le démarrage attendent le même : un seul moteur par marche.
2. **Compteur d'applications.** Chaque `VmApplication` est libérée par `Dispose` : une console la
   libère en se fermant, un programme à son `exit`.
3. **Arrêt après la dernière application**, au bout du **délai de grâce** (`StopGrace`, 5 s par
   défaut). Une application qui revient pendant le délai l'annule.
4. **Démarrage à la main** (`x86 vm start`, `Ix86.StartVmAsync`) : la VM est tenue (72.6) et ne
   s'arrête plus seule ; `x86 vm stop` la relâche et l'arrête.
5. **Arrêt** : refusé (`vm-busy`) tant que d'autres applications la tiennent. **Forcé**
   (`--force`), il ferme tout : les consoles échouent avec `console-closed`, l'agent est fermé, le
   moteur arrêté et libéré. L'arrêt n'efface rien.
6. **Mort de l'invité** : si le moteur s'arrête seul (plantage du worker), la marche se termine
   comme un arrêt.

États exposés : `Stopped`, `Starting`, `Running`, `Stopping` (`VmState`), avec l'événement
`VmManager.InstanceStateChanged` et, filtré par usager, `Ix86.VmStateChanged`.

**Choix du délai de grâce : 5 s.** 166 A dit « dès que ». Un délai court laisse un shell qui se
rouvre aussitôt, ou un programme qui en enchaîne un autre, garder la VM, alors qu'un démarrage
coûte 8 s pour l'image Alpine (mesuré plus bas) et 45 à 70 s pour linux-base. Il est réglable
(`VmManagerOptions.StopGrace`, 0 = arrêt immédiat) ; son écriture par l'admin est le chantier 7.

## La limite de mémoire (165 A)

`MemoryBudget` : la limite est la moitié de la mémoire annoncée par le navigateur
(`navigator.deviceMemory`), ou la limite de l'admin (`VmManagerOptions.MemoryLimitMiB`, lue ici,
écrite par le chantier 7). Sans annonce (Firefox, Safari), on suppose 4 Gio, soit 2 Gio de budget.

- Sous la limite (mémoire des VM en démarrage ou en marche plus la nouvelle), **aucune question**.
- Au-delà, refus typé `VmMemoryBudgetExceededException` (`memory-budget-exceeded`), avant de
  créer le moindre moteur. Il porte la mémoire demandée, celle en marche, la limite, **les VM en
  marche de l'appelant** et parmi elles **les compatibles** (même genre, même image). Les VM des
  autres usagers sont comptées, jamais nommées. La fenêtre de choix est une affaire d'interface.
- La vérification et le passage à `Starting` sont un seul pas pour toute la machine : deux VM qui
  démarrent ensemble ne passent pas toutes deux un budget où une seule tient.

`navigator.deviceMemory` est arrondi par le navigateur (8 Go au plus dans Chrome). La Feature le
lira par son module JS (chantier 8) ; la commande du squelette n'y a pas accès et suppose 4 Gio.

## Agent, processus et image sans agent (206 A, 207 A, 212 A)

- **Enfants.** Chaque lancement (`VmInstance.LaunchAsync`) et chaque console est enregistré comme
  enfant de la VM avec son `LaunchId` à la réponse de l'agent, et retiré à `Exited` (207 A) ;
  `x86 vm info` les liste. C'est la forme qu'auront les processus Kazo enfants de la VM quand la
  Feature sera un vrai processus (chantier 8) ; rien ne passe par `StartChild`.
- **Agent redémarré** (événement `ready` après la connexion) : les enfants sont oubliés, leurs
  poignées échouent avec `closed` (comportement du client du chantier 5).
- **Image sans agent.** Si l'agent ne répond pas à la connexion (`not-ready`), la VM marche sans
  enfants (`HasAgent = false`). `x86 shell` ouvre alors la **console série** si le profil déclare
  un port série, sinon il refuse (`no-console`, code 6). Une console série est tout `ttyS0`,
  partagé avec les messages de démarrage, sans code de sortie (0 à la fermeture).

### Surveillance de l'agent

**Choix : ping périodique**, toutes les 20 s (`AgentWatchdogInterval`), chaque ping ayant 10 s pour
répondre (`AgentWatchdogTimeout`). Après **2 échecs de suite** (`AgentWatchdogMisses`), l'agent est
déclaré **perdu**. Un ping à la demande existe aussi (`VmInstance.PingAgentAsync`).

- Coût : un `ping` prend 3 à 9 ms ; un toutes les 20 s est négligeable. Un agent mort est vu en 40
  à 60 s, sans fausse alerte pour une VM chargée grâce aux 10 s de marge.
- **Ce qui se passe quand il ne répond plus** : l'événement `VmManager.AgentLost` est levé, les
  enfants sont oubliés, `x86 vm info` affiche « lost », toute nouvelle console ou tout nouveau
  lancement échoue avec `agent-lost`. **La VM n'est pas arrêtée** : une console déjà ouverte peut
  encore marcher si seul le canal de protocole est bloqué, et c'est à l'usager de l'arrêter
  (`x86 vm stop --force`). L'agent n'est pas relancé : il ne sait pas l'être (chantier 5).

Mesuré par le test `guest` sur six passages (Chrome sans écran, Windows 11, Ryzen 7 3700X, image
Alpine de test) :

| Mesure | Valeur |
| --- | --- |
| `x86 shell` jusqu'à la première invite, démarrage à froid de la VM compris | 8,2 à 11,6 s |
| Ctrl+C (ligne `^C`) jusqu'à l'invite suivante | 61 à 64 ms |
| `ping` de l'agent, aller-retour par le pont du test | 8,7 à 45 ms |
| `exit` jusqu'au début de l'arrêt, délai de grâce du test de 3 s | 3,0 s |

**Écho du terminal de l'invité à la connexion.** Tant que l'agent n'a pas ouvert `hvc0` en mode
brut, le pilote de terminal de l'invité renvoie ce que l'hôte écrit : le premier `ping` peut
revenir tel quel et échouer en erreur de protocole. Trouvé par le test `guest`, qui échouait une
fois sur deux. La connexion le réessaie, comme le silence d'un invité qui démarre, jusqu'au délai
de connexion ; une autre erreur de protocole (version inconnue) fait échouer le démarrage.

## Les profils (183 A)

Un profil est la **recette** d'une VM (121) : un document JSON `.x86profile` versionné.

| Champ | Sens |
| --- | --- |
| `format` | 1 ; un format inconnu est refusé sans rien écrire |
| `name` | le nom du profil (`--profile <nom>`) |
| `kind` | `linux`, `dos`, `windows-programs` |
| `image`, `imageVersion` | l'image du dépôt et sa version |
| `memoryMiB` | 16 à 4096, compté dans le budget |
| `serialConsole` | l'image a une console série : seule console d'une image sans agent (212 A) |
| `prefix` | le préfixe où s'ouvre une console sans `--prefix` (200 B), ou absent |
| `programs` | section opaque réservée à BoxedWine, gardée telle quelle |

Les propriétés inconnues sont ignorées, les commentaires et virgules finales acceptés, 256 Kio
au plus. Profils livrés : **`alpine-test`** (image Alpine de test avec agent, 128 Mio, série,
préfixe `test`, défaut de `x86 vm create`) et **`linux-base`** (256 Mio, série, sans préfixe :
ses usagers invités viennent avec les préfixes, chantiers 9 et 10). Un profil importé
(`x86 profile import`) est rangé dans le registre de l'usager et masque un profil livré du même nom.

## La commande `x86`

```text
x86 vm list
x86 vm create <nom> [--profile <profil | fichier.x86profile>]
x86 vm start <vm>
x86 vm stop <vm> [--force]
x86 vm remove <vm> [--force]
x86 vm rename <vm> <nom>
x86 vm info <vm>
x86 shell [--vm <vm>] [--prefix <préfixe>] [--cols N] [--rows N]
x86 profile list | show <profil> | import <fichier> | export <profil> <fichier>
```

`<vm>` est un nom ou un GUID. Les options s'écrivent `--nom valeur` ou `--nom=valeur`, dans
n'importe quel ordre ; une option inconnue est une erreur ; `--` termine les options. Les messages
sont en anglais, comme le reste de la commande : le squelette n'a pas de ressources traduites.

```text
$ x86 vm create Alpine
Created Alpine (3f2c…) from the profile alpine-test.
$ x86 vm list
ID                                    KIND              STATE     MEMORY  IMAGE                 NAME
3f2c…                                 linux             stopped     128M  alpine-test 0.1.0     Alpine
$ x86 shell
~ $ echo vm-$((6*7))-$(id -u)
vm-42-1001
~ $ sleep 600
^C
~ $ exit 5
$ echo $?
5
```

### Codes de sortie

| Code | Sens |
| --- | --- |
| 0 | fait |
| 1 | l'opération a échoué : agent, profil ou nom invalide, format inconnu, démarrage |
| 2 | ligne de commande fausse : commande ou option inconnue, argument manquant ; l'usage va sur la sortie d'erreur |
| 3 | budget de mémoire dépassé ; les VM en marche et les compatibles sont listées |
| 4 | aucune VM ni aucun profil de ce nom |
| 5 | VM occupée : arrêt ou suppression sans `--force` |
| 6 | rien sur cet hôte pour la faire tourner : pas d'émulateur, image absente, aucune console |
| 130 | Ctrl+C avant qu'une console ne soit ouverte |

`x86 shell` rend le code du shell de l'invité, 128 plus le signal quand un signal l'a terminé.

### `x86 shell`, ligne par ligne

Tant que l'ADR 0252 n'est pas livrée, le terminal de Kazo est en mode ligne. `x86 shell` relaie
la console de l'agent (213 A) ainsi :

- **sortie** : les octets du pty deviennent du texte (UTF-8 décodé à cheval sur les morceaux ;
  retours chariot et séquences CSI, OSC et ESC retirés ; autres caractères de contrôle retirés
  sauf LF, tabulation et BEL) ; l'écho par le pty de chaque ligne envoyée est retiré une fois,
  puisque le terminal de Kazo a déjà affiché la ligne tapée ;
- **entrée** : chaque ligne lue part avec LF. La ligne `^C` envoie Ctrl+C au programme invité,
  comme l'interruption de la commande elle-même (Ctrl+C dans le terminal de Kazo, 216 A). La ligne
  `~.` ferme la console, comme dans ssh. La fin de l'entrée envoie Ctrl+D ;
- **fin** : le code de sortie du shell est rendu. La console était une application de la VM, qui
  s'arrête après le délai de grâce si c'était la dernière.

Le shell compte comme une application (166 A). Sans `--vm`, il prend la seule VM de l'usager ;
sans VM, il le dit (code 4) ; avec plusieurs, il demande `--vm` (code 2).

## Ce que `Ix86` expose, et pourquoi

Ce que le Terminal et BoxedWine devront appeler, rien de plus (`PublicAPI.Unshipped.txt`) :

| Ajout | Pour qui, pourquoi |
| --- | --- |
| `VmSummary` avec `Image`, `ImageVersion`, `MemoryMiB` | BoxedWine affiche les VM et propose une VM compatible (même image) ; la mémoire sert la fenêtre de choix de 165 A |
| `VmStateChanged`, `VmStateChangedEventArgs` | la page de la VM et BoxedWine suivent l'état sans sonder |
| `StartVmAsync`, `StopVmAsync(force)` | « démarrer, arrêter une VM » du tableau de la section 9 du plan |
| `OpenConsoleAsync`, `IVmConsole`, `VmConsoleOptions` | le Terminal ouvre une console (213 A) : octets, Ctrl+C (216 A), taille, code de sortie |
| `X86Exception`, `X86ErrorCodes` | des erreurs à code stable plutôt que des exceptions internes |
| `VmMemoryBudgetExceededException` | le refus de 165 A porte ce qu'il faut à la fenêtre de choix |

Pas encore : la création et la suppression de VM, les profils, les lancements de programmes, les
préfixes ; ils viendront avec leurs clients (BoxedWine, chantiers 10 et 13). Le service est lié à
un usager (`new X86Service(manager, userId)`) ; son enregistrement dans Kazo est le chantier 8.

## Tests

- **`unit`** (`VmLifecycleTests`, `VmModelTests`, `X86CommandTests`) : le moteur simulé
  (`FakeEngine`) parle sur `virtio0` et `virtio1` à l'agent simulé du chantier 5 ; une horloge
  manuelle fait passer le délai de grâce et la surveillance sans attendre. Sont couverts le cycle
  de vie (premier démarrage, applications pendant le démarrage, délai de grâce et son annulation,
  arrêt à la main, arrêt refusé puis forcé, mort de l'invité, échecs de démarrage), les enfants, le
  repli série, la perte de l'agent, le budget (refus, compatibles, VM d'un autre usager non
  nommée, limite de l'admin), le registre et son format, les profils, le service, le filtre de
  terminal, l'analyse et la sortie exacte de chaque sous-commande avec son code. Couverture des
  fichiers du chantier : 94 % des lignes.
- **`guest`** (`VmShellGuestTests`) : l'image Alpine avec agent démarre dans un vrai navigateur
  sous la `V86Engine` de production ; seule l'interop JS est remplacée par Playwright
  (`vm.html`). La commande `x86` la pilote : `x86 vm create`, puis `x86 shell` démarre la VM à la
  demande et ouvre un shell dans l'invité comme l'usager du préfixe `test` ; une commande répond
  sans écho doublé ; Ctrl+C interrompt `sleep 600` (statut 130) ; `exit 5` rend 5 ; la VM s'arrête
  après le délai de grâce et son worker est libéré. Le test est déclenché par `src/OsX86/Vm/**`,
  `src/OsX86/Profiles/**`, `src/OsX86.Feature/**` et `tests/OsX86.Tests/Vm/**` dans
  `guest-tests.yml`.

## Ce qui attend les autres chantiers

- **6a (disque `hda`)** : brancher `linux-base` comme image par défaut de `x86 vm create`, et lui
  donner un `IBootImageSource` qui lit le dépôt (`ImageStore.Locate`). `BootImage` n'a aujourd'hui
  que noyau, initramfs et ligne de commande ; il prendra le disque.
- **7 (admin)** : écrire la limite de mémoire et le délai de grâce, voir et gérer les VM des autres
  usagers, tenir le journal personnel ; la fenêtre de choix de 165 A côté interface.
- **8 (Feature)** : la Feature crée le moteur (elle a l'`IJSRuntime` que la commande n'a pas,
  ADR 0070 de Kazo), lit `navigator.deviceMemory`, enregistre `Ix86` par usager, et fait de chaque
  VM un processus Kazo et de chaque enfant un processus enfant (207 A).
- **10 (préfixes)** : `--prefix` créera ses usagers invités ; `x86 run` et `./hello.exe` passeront
  par `VmInstance.LaunchAsync`.
- **ADR 0252** : quand le Terminal aura son écran plein, `x86 shell` relaiera les octets bruts à un
  émulateur VT au lieu du filtre ligne par ligne.

## Limites connues et non vérifié

- **Dans Kazo, la commande ne démarre pas de VM** : une commande n'a pas d'émulateur
  (`engine-unavailable`, code 6) tant que la Feature n'existe pas (chantier 8) ; le test `guest`
  remplace la fabrique de moteurs. `vm list`, `create`, `rename`, `remove`, `info` et `profile`
  marchent sur le registre persistant.
- **Non vérifié** : la commande dans Kazo lui-même (fichiers privés réels, Ctrl+C du terminal de
  Kazo transmis comme annulation de la commande), Firefox et Safari, linux-base (pas de `hda`).
- Les VM vivent dans le processus hôte : recharger la page les arrête toutes ; le registre reste.
- Le filtre ligne par ligne ne rend ni couleurs ni curseur : un programme plein écran (`vi`) est
  illisible jusqu'à l'ADR 0252. Un programme qui coupe l'écho (mot de passe) peut laisser au plus
  huit lignes attendre un écho qui ne vient pas.
