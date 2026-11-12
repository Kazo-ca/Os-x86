> [English version](../en/agent.md)

# L'agent invité et son client

Jalon 0, chantier 5 (`j0/5-agent`). L'agent est un petit programme qui tourne en root dans
l'invité Linux et reçoit ses ordres d'Os-x86 : lancer un programme pour un préfixe, lister et
arrêter, ouvrir une console, dire l'état des montages. Ce chantier livre l'agent (C statique),
sa recette, son installation dans les images, le client C# du protocole et leurs tests. Le
registre des VM et `x86 shell`, qui s'en servent, sont le chantier 6
([ordre du jalon](../conception/jalon-0-execution.md)). Décisions : 205 A (protocole), 206 A (un
lancement = un cgroup), 207 A (modèle de processus), 208 A (fermer la console), 213 A (pty par
console), 216 A (Ctrl+C) ; l'essai d'origine est [E7](../essais/e7-agent-processus.md).

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `images/agent/src/` | l'agent : `agent.c` (boucle, port, réponses), `launch.c` (lancements, cgroups, kill, liste), `jobs.c` (`exec-in-prefix`, `mounts`), `console.c` (pty et trames), `url.c` (socket `open-url`), `json.c`, `agent.h` ; `open-url.c` (le programme `osx86-open-url`) |
| `images/agent/Dockerfile`, `build-agent.py` | la recette : étape Alpine i386 épinglée, `--check`, `--smoke` |
| `images/build/agent/` | la sortie (ignorée par git) : `agent`, `osx86-open-url`, `toolchain.txt`, `manifest.json` |
| `src/OsX86/Agent/` | le client : `AgentClient` (deux fichiers), `IAgentChannel` et `EngineAgentChannel`, `ConsoleHandle`, `ConsoleFrame`, les types (`AgentTypes.cs`), les erreurs, le protocole |
| `tests/OsX86.Tests/Agent/` | tests `unit` (canal simulé) et test `guest` (image Alpine de test dans un vrai navigateur) |

```bash
python -I images/agent/build-agent.py --smoke          # construit, vérifie, pilote l'agent dans un conteneur
python -I images/agent/build-agent.py --check          # vérifie une construction existante, sans Docker
python -I images/alpine-test/build-alpine-test.py      # l'image de test embarque l'agent s'il est construit
```

## Vue d'ensemble

```
C# AgentClient ── IAgentChannel ──> IEngineConsole virtio0 ──> hvc0 : JSON par ligne ──> agent (root)
                                └─> IEngineConsole virtio1 ──> hvc1 : trames de console ──> pty ──> shell ou programme de l'usager
```

- **`hvc0`** porte le protocole : une requête JSON par ligne avec un `id`, la réponse porte le même
  `id`, les événements n'en ont pas. Le port est en `0600` root dans l'invité (init des images).
- **`hvc1`** porte les octets de toutes les consoles ouvertes, multiplexés par trames.
- `hvc2` et `hvc3` restent libres.
- L'agent est lancé par l'init de l'image s'il existe en `/usr/lib/osx86/agent`. Une image sans
  agent reste légitime (212 A) : le client échoue alors à la connexion avec `not-ready`.

L'agent est un seul fil et une seule boucle `poll` : les deux ports, les pty, le socket
`open-url` et ses appelants, un tube pour `SIGCHLD`, les tubes des commandes en cours et le
fichier `cgroup.events` de chaque lancement. **Aucune opération n'en bloque une autre**
(correction 1 de 205 A) : `exec-in-prefix` et `mounts` tournent dans un enfant et répondent
quand il a fini ; `kill` répond quand le lancement a disparu. Ce qui part vers l'hôte passe par
une file d'attente par port : l'agent n'attend jamais qu'un port se vide, et une ligne ou une
trame est mise en file entière ou pas du tout, jamais écrite en partie.

## Le protocole (version 1)

Requête : `{"id":N,"op":"…", …}` suivie de LF, 64 Kio au plus ; les nombres hors bornes sont
ramenés à leur borne, `nan` et `inf` ne sont pas du JSON et font refuser la ligne. Réponse :
`{"id":N,"ok":true, …}` ou `{"id":N,"ok":false,"error":"<code>","message":"…"}`. Événement :
`{"event":"…", …}`. Les champs inconnus sont ignorés des deux côtés ; les chaînes sont de l'UTF-8
valide (un octet invalide d'une sortie de programme devient U+FFFD).

### Opérations

| `op` | Champs de la requête | Réponse (`ok: true`) |
| --- | --- | --- |
| `ping` | — | `version`, `uptime` (s), `rssKb`, `launches`, `consoles`, `cgroups` |
| `list-processes` | — | `processes[]` : `pid`, `ppid`, `pgid`, `uid`, `state`, `prefix`, `launch`, `comm`, `cmd`, `cgroup` |
| `launch` | `prefix`, `program`, `args[]`, `env{}`, `cwd`, `nproc`, `log` | `launch` (identifiant), `pid`, `uid`, `cgroup`, `ms` |
| `kill` par lancement | `launch`, `graceMs` (défaut 2000, au plus 10 min), `signal` (défaut 15 ; 0 refusé) | `forced`, `ms` ; arrive quand le lancement a disparu |
| `kill` d'administration | `scope` = `pid` \| `group` \| `prefix` \| `cgroup`, puis `pid`, `prefix`, `seq`, `signal`, `allowRoot` | `count`, `signal`, `target`, `ms` |
| `mounts` | `timeoutMs` (défaut 8000, au plus 1 h) | `mounts[]` : `path`, `type`, `device`, `state` (`ok`, `error`, `hung`), `entries`, `blocks`, `blockSize`, `freeBlocks`, `statfsErrno`, `readdirErrno`, `error` ; `timedOut` |
| `exec-in-prefix` | `prefix`, `program`, `args[]`, `env{}`, `cwd`, `timeoutMs` (défaut 30000, au plus 1 h), `maxOutput` (défaut 65536, au plus 1 Mio) | `code`, `signal`, `timedOut`, `truncated`, `output`, `ms` |
| `open-console` | `prefix`, `program` (défaut : le shell de l'usager, interactif), `args[]`, `env{}`, `cwd`, `cols`, `rows` | `launch`, `console` (numéro), `pid`, `uid`, `cols`, `rows` |
| `close-console` | `console` ou `launch`, `graceMs`, `keep[]` (pid à garder) | `keptCount`, `kept` (lancement des processus gardés) ; immédiate |
| `console-resize` | `console`, `cols`, `rows` | `console`, `cols`, `rows` |
| `list-windows` | — | erreur `no-x-server` tant que l'image n'a pas de serveur X (chantier 9) |

```json
→ {"id":12,"op":"launch","prefix":"test","program":"sleep","args":["300"]}
← {"id":12,"ok":true,"launch":"test/1","pid":448,"uid":1001,"ms":44.6,"cgroup":"/sys/fs/cgroup/osx86/test/1"}
→ {"id":16,"op":"kill","launch":"test/1"}
← {"event":"exit","launch":"test/1","pid":448,"code":-1,"signal":15,"remaining":0}
← {"id":16,"ok":true,"forced":false,"ms":3.1}
→ {"id":17,"op":"launch","prefix":"nobody","program":"sleep"}
← {"id":17,"ok":false,"error":"unknown-prefix","message":"no guest user osx86-nobody"}
```

### Événements

| `event` | Champs | Quand |
| --- | --- | --- |
| `ready` | `version`, `pid`, `cgroups`, `port`, `consolePort` | l'agent démarre |
| `exit` | `launch`, `pid`, `code` (-1 si signal), `signal`, `remaining` | le processus principal d'un lancement se termine ; `remaining` = processus encore dans son cgroup |
| `console-closed` | `launch`, `console`, `pid`, `code`, `signal`, `remaining` | le processus principal d'une console se termine |
| `open-url` | `prefix`, `uid`, `pid`, `url` | un programme invité a lancé `osx86-open-url` |

### Codes d'erreur

`invalid-json`, `line-too-long`, `unknown-op`, `invalid-request`, `invalid-prefix`,
`unknown-prefix`, `program-not-found`, `exec-failed`, `cgroup-failed`, `no-cgroup`,
`unknown-launch`, `kill-pending`, `unknown-pid`, `root-process`, `kill-failed`, `no-x-server`,
`no-console-port`, `unknown-console`, `pty-failed`, `too-many-launches`, `too-many-consoles`,
`too-many-jobs`. Le client y ajoute les siens : `timeout`, `not-ready`, `closed`, `protocol`,
`unsupported-version` (`AgentErrorCodes`).

## Cycle de vie d'un lancement (206 A)

1. **Préfixe et usager.** Le préfixe `p` (lettres minuscules, chiffres, tirets, 1 à 16
   caractères) a pour usager invité `osx86-p`, qui doit exister dans `/etc/passwd` de l'image et
   ne pas être root. L'image Alpine de test a `osx86-test` et `osx86-other` ; les usagers de
   linux-base viennent avec les préfixes (chantiers 9 et 10).
2. **`launch`.** L'agent crée le cgroup `/sys/fs/cgroup/osx86/<prefix>/<seq>`, puis l'enfant :
   `setsid`, entrée dans le cgroup, `RLIMIT_NPROC` (256 par défaut), `initgroups`/`setgid`/`setuid`
   vers l'usager, `chdir` (le home par défaut), environnement refait (`HOME`, `USER`, `LOGNAME`,
   `SHELL`, `PATH`, `TERM`, plus `env` sauf `LD_*`), tous les signaux remis par défaut, `execvp`.
   **Jamais une ligne de shell** : un programme et ses arguments. Un `exec` ou une étape qui
   échoue (cgroup, limite, identité, dossier) revient par un tube : la réponse est une erreur
   (`program-not-found`, `exec-failed`), pas un pid mort, et rien ne tourne hors de son cgroup.
   Un enfant qui n'atteint pas l'`exec` en 5 s (dossier de travail sur un partage mort) est tué.
3. **Identifiant.** `<prefix>/<seq>` nomme le lancement ; c'est ce qu'Os-x86 expose, jamais le pid
   (sous Wine le pid rendu est celui d'un lanceur, E7).
4. **Fin naturelle.** À la mort du processus principal : événement `exit` avec son code. Le
   lancement reste suivi tant que son cgroup a des processus (`remaining`), puis le cgroup est
   supprimé.
5. **`kill` par identifiant.** SIGTERM (ou `signal`) à tous les processus du cgroup ; s'il en
   reste après `graceMs`, `cgroup.kill` ; la réponse (`forced` dit lequel) arrive quand le cgroup
   est vide. `graceMs: 0` = `cgroup.kill` tout de suite. Un lancement déjà fini répond
   `unknown-launch`.
6. **Sans cgroup v2** (conteneur du test de fumée) : le lancement est un groupe de processus,
   `kill` vise le groupe ; les garanties d'E7 sur les démons `setsid` ne tiennent pas.

Les orphelins reviennent à l'agent (`PR_SET_CHILD_SUBREAPER`) et sont récoltés sans événement :
c'est le cgroup qui suit un lancement, pas ses pid.

## Consoles (213 A, 208 A)

`open-console` ouvre un pty, donne l'esclave à l'usager du préfixe (`0600`), en fait le terminal
de contrôle du programme (le shell de l'usager, `-i`, par défaut) et place le tout dans un cgroup
propre : une console est un lancement comme un autre (207 A).

**Choix : multiplexage sur `hvc1`** plutôt qu'une console par port. v86 n'a que quatre ports :
une console par port en donnerait trois au plus, alors que 213 A veut un pty par programme
console. Trame, dans les deux sens :

```
0xAE  type  console (4 octets, gros-boutiste)  longueur (2 octets)  charge utile
```

| Type | Sens | Charge utile |
| --- | --- | --- |
| 1 `DATA` | hôte → invité : frappe ; invité → hôte : sortie du pty | les octets (4 096 au plus par trame) |
| 2 `CLOSED` | invité → hôte, après la dernière `DATA` d'une console | code de sortie et signal, deux int32 gros-boutistes |

- Le numéro de console est le numéro de séquence de son lancement.
- **Ctrl+C** est l'octet `0x03` dans une `DATA` : la discipline de ligne du pty en fait un SIGINT
  (216 A), sans autre voie.
- **Le code de sortie voyage sur le canal des octets**, après eux (`CLOSED`) : le terminal sait
  que tout est arrivé quand il reçoit le code. L'événement `console-closed` de `hvc0` le redit
  pour le registre.
- Une trame n'est jamais écrite en partie : le flux ne se désynchronise pas. Un lecteur qui
  arrive en cours de route et ne trouve pas `0xAE` en tête de trame saute jusqu'au suivant.
- **Contrôle de flux** : tant que l'hôte n'a pas pris 256 Kio de sortie en attente, l'agent ne
  lit plus les pty ; un programme qui inonde son terminal attend, comme sur un terminal lent,
  sans gêner les autres consoles ni le protocole.

Mesuré dans l'image Alpine de test (v86 dans Chrome, cette machine, par le pont du test) :

| Mesure | Valeur | E7 (port dédié, image Debian) |
| --- | --- | --- |
| `open-console` → première invite | 86 à 109 ms | 154 à 170 ms |
| Ctrl+C → invite suivante | 62 à 64 ms | — |
| Deux consoles en même temps | aucune fuite d'une console vers l'autre | une seule console possible |
| Surcoût | 8 octets par trame (0,2 % à trame pleine ; une frappe coûte 9 octets) | 0 |

**Fermeture (208 A).** `close-console` déplace d'abord les processus de `keep` (ceux qui ont une
fenêtre ; l'hôte les nommera quand l'image aura X) dans un cgroup frère, qui devient un
lancement à part entière (`kept`) ; le reste reçoit SIGHUP puis `cgroup.kill` après `graceMs`.
Un shell qui sort de lui-même est traité de même, sans rien garder. Vérifié dans le test
`guest` : de deux tâches de fond, celle nommée dans `keep` survit dans son nouveau lancement,
l'autre meurt avec la console.

## `open-url` (139)

`osx86-open-url URL` écrit l'URL sur le socket unix `/run/osx86/open-url.sock`. L'agent lit
l'uid de l'appelant sur le socket (`SO_PEERCRED`), en déduit le préfixe et émet l'événement ;
il ne fait rien d'autre de l'URL. Le socket est ouvert à tout usager invité : la lecture ne
bloque jamais la boucle (500 ms par appelant, 8 appelants à la fois dont 2 au plus pour un même
usager, 2 048 octets).

## Le client C#

```csharp
await using var agent = new AgentClient(EngineAgentChannel.For(engine));
AgentStatus status = await agent.ConnectAsync(cancellationToken);      // attend l'agent

LaunchResult launch = await agent.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = ["300"] }, cancellationToken);
IReadOnlyList<ProcessInfo> processes = await agent.ListProcessesAsync(cancellationToken);
await agent.KillAsync(launch.Id);                                       // SIGTERM puis cgroup.kill
ProcessExit exit = await launch.Exited;                                 // code ou signal

ConsoleHandle console = await agent.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, cancellationToken);
await console.WriteAsync("ls\n", cancellationToken);
await console.SendInterruptAsync(cancellationToken);                    // Ctrl+C
await foreach (ReadOnlyMemory<byte> bytes in console.ReadOutputAsync(cancellationToken)) { /* vers le terminal */ }
ProcessExit shellExit = await console.Exited;
```

- **`IAgentChannel`** : les deux flux (lignes du protocole, octets des consoles).
  `EngineAgentChannel.For(engine)` les pose sur `virtio0` et `virtio1` ; `IEngine`, le worker et
  `Ix86` ne changent pas.
- **Connexion.** `ConnectAsync` envoie un `ping` toutes les secondes jusqu'à une réponse :
  l'événement `ready` de l'agent a pu être écrit avant que quiconque écoute. Une autre version
  du protocole est refusée (`unsupported-version`). Après un échec (`not-ready`), on peut
  rappeler `ConnectAsync` sur le même client.
- **Appariement.** Chaque requête a un `id` croissant ; la réponse est rendue à qui l'attend,
  dans n'importe quel ordre ; une réponse qui arrive après son délai est ignorée, sauf celle
  d'un `launch` ou d'un `open-console` : personne n'en tient plus l'identifiant, le client tue
  le lancement ou ferme la console aussitôt pour que rien ne tourne sans nom dans l'invité. Une
  réponse illisible qui porte un `id` fait échouer sa requête tout de suite (`protocol`).
- **Types.** `LaunchId` (`<prefix>/<seq>`), `LaunchResult` (avec `Exited`), `ProcessInfo`,
  `MountInfo` et `MountState`, `ExecResult`, `KillResult`, `SignalResult`, `ConsoleHandle`
  (`ReadOutputAsync`, `WriteAsync`, `SendInterruptAsync`, `ResizeAsync`, `CloseAsync`, `Exited`).
- **Événements.** `ReadEventsAsync` : `AgentReadyEvent`, `LaunchExitedEvent`,
  `ConsoleClosedEvent`, `OpenUrlEvent`, et `AgentFaultEvent` pour ce qui ne suit pas le protocole
  (signalé, sans arrêter le client). Un `ready` reçu après la connexion veut dire que l'agent a
  redémarré : les lancements et consoles suivis échouent avec `closed`.
- **Erreurs.** `AgentException` (code de l'agent), `AgentTimeoutException`,
  `AgentClosedException`, `AgentProtocolException`. Les arguments qui ne peuvent pas arriver
  intacts dans l'invité (caractère NUL, nom de variable avec `=`, préfixe mal formé) sont refusés
  avant tout envoi.
- **Fin.** `DisposeAsync` n'envoie rien : ce qui tourne dans l'invité continue. Les requêtes, les
  `Exited` et les consoles en attente échouent avec `closed`.

### Délais

| Délai | Valeur par défaut | Raison |
| --- | --- | --- |
| Réponse à une requête (`RequestTimeout`) | 30 s | la plus lente, `list-processes`, prend 0,1 à 0,7 s (E7 : 80 processus) ; large marge pour une VM chargée |
| Connexion (`ConnectTimeout`), sondage (`ConnectProbeInterval`) | 120 s, 1 s | démarrage à froid de l'image Linux : 45 à 70 s (E7, E17) |
| `kill` : grâce puis marge (`LongOperationMargin`) | 2 s + 10 s | la grâce est celle de l'agent ; le client attend la grâce demandée plus la marge |
| `exec-in-prefix`, `mounts` | 30 s et 8 s + marge | l'agent tue la commande à son délai ; un montage muet revient `hung` |
| Repli de fin de console (`ConsoleCloseFallback`) | 2 s | si la trame `CLOSED` n'arrive jamais, l'événement `console-closed` termine la console |
| Agent : port plein | file d'attente (8 Mio pour le protocole, 4 Mio pour les consoles) | l'agent n'attend jamais un port ; au-delà de 256 Kio de sortie en attente, les pty ne sont plus lus |
| Agent : enfant qui n'atteint pas l'`exec` | 5 s puis tué | un dossier de travail sur un partage mort ne gèle pas l'agent |
| Agent : écriture vers un pty plein | 200 ms puis abandon | un programme qui ne lit pas son clavier ne gèle pas l'agent |

## Mesures

Image Alpine de test (noyau 6.12, 128 Mio), v86 0.5.473 dans Chrome headless, Windows 11,
Ryzen 7 3700X ; aller-retour mesuré dans le test `guest`, donc à travers Playwright.

| Mesure | Valeur | E7 |
| --- | --- | --- |
| Binaire `agent` / `osx86-open-url` | 111 656 / 29 668 octets | 108 Kio / 18 Kio |
| Mémoire résidente de l'agent | 172 à 176 Kio | 160 à 184 Kio |
| Agent prêt après le démarrage de l'invité | ≈ 5 s (image Alpine) | 47 à 52 s (image Wine) |
| `ping`, aller-retour (10 fois) | médiane 4,5 à 7,4 ms | 3,2 à 3,6 ms |
| `kill` par lancement, aller-retour | 14 à 34 ms (3 à 25 ms dans l'invité) | 18 à 20 ms |
| `launch`, dans l'agent | 15 à 70 ms | 1,5 à 4 ms (hors attente de l'`exec`) |
| `list-processes` (≈ 60 processus), dans l'agent | 95 à 330 ms | 380 à 475 ms (55 processus) |
| `mounts` | 3 ms | 42 à 70 ms (5 montages) |

## Tests

- **`unit`** (`AgentProtocolTests`, `ConsoleFrameTests`, `AgentClientTests`, `AgentConsoleTests`,
  90 tests) : encodage et décodage, appariement dans le désordre, délais, annulation,
  événements, fautes de protocole, réponse tardive d'un lancement abandonné, redémarrage de
  l'agent, consoles (trames coupées n'importe où, trames arrivées avant la réponse, fin sans
  trame `CLOSED`, numéro réutilisé), fermeture du client. Le canal simulé
  (`SimulatedAgentChannel`) joue l'agent. Couverture du dossier `Agent` : 96,9 % des lignes.
- **`guest`** (`AgentGuestTests`) : l'image Alpine de test avec l'agent démarre dans le worker
  d'un vrai navigateur et le client C# la pilote ; ce sont les contrôles d'E7 qui ne demandent ni
  X ni Wine : `ping`, `launch` (binaires de l'image, jamais `sh -c`), `list-processes` avec
  préfixe, lancement et cgroup, `kill` par identifiant (A disparaît, B et la VM restent),
  `cgroup.kill` d'un arbre, code de sortie, `mounts`, `exec-in-prefix` (identité, délai, port de
  l'agent et home de l'autre préfixe refusés), `open-url`, deux consoles à la fois (shell
  d'usager, taille, redimensionnement, Ctrl+C, code de sortie, fermeture 208 A), les erreurs.
- **Test de fumée** (`build-agent.py --smoke`) : l'agent piloté par son entrée et sa sortie
  standard dans un conteneur Alpine i386 ; il couvre le repli sans cgroup.
- **CI** : `ci.yml` (tests `unit`, toute PR), `guest-tests.yml` (déclenché par
  `src/OsX86/Agent/**`, `tests/OsX86.Tests/Agent/**`, `images/**`), `images.yml` (job `agent`,
  déclenché par `images/**`).

## Garanties et limites

Tenu et vérifié ici :

- Un programme tourne comme l'usager de son préfixe, jamais root ; aucun texte de l'hôte n'est
  interprété par un shell.
- Arrêter un lancement n'en touche aucun autre ni la VM ; un arbre entier meurt avec son cgroup.
- Un usager de préfixe ne peut ni lire ni écrire le port de l'agent, ni lire le home d'un autre
  préfixe (deux des 22 contrôles R134.5, rejoués dans l'image Alpine).
- L'URL d'`open-url` est attribuée par le noyau, pas par le message.
- Dans l'image, l'agent appartient à root et n'est inscriptible par personne d'autre (contrôle
  `agent_files_root_only` du test d'image).

Limites connues :

- **Non vérifié** : X et `list-windows` (squelette), Wine, le démarrage de linux-base (le worker
  ne gère pas encore `hda` : l'agent y est installé, vérifié dans l'export, jamais exécuté) ; les
  22 contrôles R134.5 complets à l'exécution (`verify-console.sh`) attendent ce démarrage ;
  Firefox et Safari.
- Pas de chiffrement ni d'authentification sur le port : il est `0600` root dans l'invité et seul
  le worker y accède côté page.
- L'agent n'est pas relancé s'il meurt ; ses tables (lancements, consoles) ne survivent pas à un
  redémarrage ni à une restauration d'état.
- Tables bornées : 256 lancements suivis, 32 consoles, 32 commandes en cours, 8 appelants
  `open-url`.
- L'événement `exit` ne concerne que le processus principal ; un lanceur qui laisse des enfants
  rend `remaining` > 0 et le lancement reste tuable par son identifiant.
- Si l'hôte cesse de lire, les consoles attendent (contrôle de flux) ; une ligne du protocole
  n'est abandonnée, entière, qu'au-delà de 8 Mio en attente. Si un programme ne lit pas son
  terminal, la frappe en trop est abandonnée après 200 ms, et la boucle attend jusque-là.
- Un lancement d'un `exec` qui traîne (jusqu'à 5 s) et un `kill` par préfixe (jusqu'à 0,5 s)
  retiennent la boucle pendant ce temps.
- Aucune limite de CPU ni de mémoire par lancement (207 A) : seule `RLIMIT_NPROC` existe.

## Ce qui attend le chantier 6

- Créer le client sur le moteur (`EngineAgentChannel.For(engine)`), appeler `ConnectAsync` après
  `StartAsync`, traiter `not-ready` comme « image sans agent » (212 A : repli console série).
- Enregistrer chaque `LaunchResult` comme processus Kazo enfant de la VM et le retirer à
  `Exited` (207 A) ; `x86 shell` = `OpenConsoleAsync` relié au terminal.
- Prendre en charge `hda` dans le worker pour démarrer linux-base, puis rejouer
  `verify-console.sh` depuis une console de l'agent.
- Décider de la surveillance de l'agent (ping périodique) et de ce qu'on fait s'il ne répond plus.
- Créer les usagers `osx86-<préfixe>` avec les préfixes (chantiers 9 et 10) ; l'agent n'a pas
  d'opération pour cela.
