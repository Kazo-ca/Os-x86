> [English version](../en/programs.md)

# Préfixes, détection, `x86 run` et programmes DOS

Jalon 0, chantier 10 (`j0/10-prefixes-and-detection`). Ce chantier ouvre l'exécution des
programmes Windows console : les préfixes (un usager invité et une copie de la bouteille gabarit
par préfixe), la détection du type d'un programme par son en-tête puis son extension, `x86 detect`,
`x86 prefix` et `x86 run`, qui lance un programme dans un préfixe sur un pty relié au terminal et
rend son vrai code de sortie ([ordre du jalon](../conception/jalon-0-execution.md)). Le passage par
le shell de Kazo (`./hello.exe` tapé au terminal, ADR Kazo 0251 et 0252) attend que Kazo ait codé
ces ADR ; tout ce qui se fait côté Os-x86 sans elles est ici. Le chantier 11 y ajoute les programmes
DOS, montrés dans le terminal ([plus bas](#programmes-dos-chantier-11)).

Décisions : 110 (vrai code de sortie), 144 (ce qui s'exécute dans un préfixe a ses droits), 146
(dossiers entre préfixes), 152 A (gabarit copié, jamais de `wineboot` chez l'usager), 162 A et
240 D (Os-x86 détecte, en-tête puis extension, type MIME), 193 A (Os-x86 décide du préfixe), 194 A
(une VM par genre et par image pour tous les préfixes de l'usager), 200 B (« prefix »), 206 A,
207 A, 208 A, 213 A (pty par programme), 216 A (Ctrl+C au programme), 252 à 255 A (détecteur de
format déclaré, types MIME freedesktop ou IANA).

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Programs/ProgramDetector.cs` | la détection, pure et sans état |
| `src/OsX86/Prefixes/Prefix.cs` | `PrefixId` (le nom, 200 B) et `Prefix` (le préfixe rangé au registre) |
| `src/OsX86/Vm/VmManager.Programs.cs` | choix de la VM et du préfixe, refus motivés, `RunProgramAsync`, création, suppression et liste des préfixes |
| `src/OsX86/Vm/VmInstance.Programs.cs` | les préfixes dans l'invité, la copie du programme, la session Wine de chaque préfixe, la console du programme, le `sync` avant l'arrêt |
| `src/OsX86/Agent/AgentClient.Prefixes.cs` | les opérations du protocole 2 de l'agent côté client |
| `src/OsX86.Feature/X86Command.Programs.cs` | `x86 prefix`, `x86 detect`, `x86 run` |
| `src/OsX86.Interface/ProgramDetection.cs`, `VmConsole.cs` | `ProgramDetection`, `ProgramMimeTypes`, `ProgramRunOptions` (`Ix86`) |
| `images/agent/src/prefix.c` | l'agent, protocole 2 : `prefix-ensure`, `prefix-delete`, `prefix-list`, `put-file`, `sync` |
| `images/linux-base/prefix-admin.sh` | `/usr/lib/osx86/prefix-admin` : usager, home et étapes de l'image, en root |
| `images/wine/prefix-hook.sh`, `prefix-session.sh` | copie du gabarit à la création d'un préfixe ; session Wine d'un préfixe |
| `tests/OsX86.Tests/Programs/`, `Vm/ProgramRunTests.cs`, `Agent/AgentPrefixTests.cs`, `X86ProgramCommandTests.cs` | tests `unit` |
| `tests/OsX86.Tests/Vm/X86RunWineGuestTests.cs` | test `guest` sur l'image Wine |

## Les préfixes (200 B, 193 A, 152 A)

Un **préfixe** (« prefix » dans le code, l'API et la CLI ; « bouteille » dans les textes plus
anciens) est un usager invité à lui : `osx86-<nom>`, uid à partir de 2000, home
`/home/osx86-<nom>` en 0700, shell `/bin/sh`, aucun mot de passe. Son préfixe Wine est
`~/.wine`, une copie de la bouteille gabarit de l'image (152 A : quelques Mio écrits, des liens
vers les fichiers de Wine, jamais de `wineboot`). Le nom (`PrefixId`) a 1 à 16 caractères :
lettres minuscules, chiffres, tirets ; il est unique dans sa VM.

- **Ce qui s'exécute dans un préfixe a ses droits (144)** : chaque programme, console ou session
  Wine tourne comme l'usager du préfixe, dans un cgroup à lui (206 A).
- **Deux préfixes ne voient pas les fichiers l'un de l'autre (146)** : homes en 0700, `hidepid`
  sur `/proc`, dossier du `wineserver` en 0700 dans `/tmp` ; vérifié par le test `guest` (un shell
  du préfixe `work` reçoit « Permission denied » sur le préfixe `default`, et l'inverse).
- **Rangés avec la VM** (registre du chantier 6, format 1 inchangé : deux champs facultatifs, que
  les versions plus anciennes ignorent) :

```json
{ "id": "3f2c…", "name": "windows", "profile": { "name": "wine", "prefix": "default", … },
  "prefixes": [ { "id": "default", "created": "2026-11-16T10:00:00+00:00" },
                { "id": "work",    "created": "2026-11-16T10:05:00+00:00" } ],
  "pendingPrefixDeletions": [ "old" ] }
```

- **Préfixe par défaut (193 A)** : sans `--prefix`, Os-x86 prend le préfixe du profil de la VM, et
  sinon `default`. Le profil livré `wine` déclare `default` ; il est rangé au registre à sa
  première utilisation. Un autre nom doit avoir été créé (`x86 prefix create`) : un nom inconnu est
  refusé (`prefix-not-found`, code 4) plutôt que créé, pour qu'une faute de frappe ne fasse pas un
  préfixe neuf.
- **Création paresseuse** : `x86 prefix create` range le préfixe tout de suite ; son usager et ses
  fichiers sont faits dans l'invité aussitôt si la VM tourne, sinon à sa première utilisation (on ne
  démarre pas une VM pour cela). Dans l'invité, `prefix-ensure` est idempotent et se répare : un
  usager présent sans home, ou un home sans `.wine`, est complété.
- **Suppression** : VM en marche, les programmes du préfixe sont tués (`cgroup.kill` de tout le
  préfixe), son usager, son home et son dossier `wineserver` supprimés ; refusé tant que des
  programmes ou des consoles y tournent, sauf `--force` (`vm-busy`, code 5). VM arrêtée, le préfixe
  quitte le registre et son nom est gardé dans `pendingPrefixDeletions` : l'invité le supprime au
  démarrage suivant, avant tout programme (seul l'invité écrit sur son disque, 211 A).
- **Export** : `x86 prefix export` existe et dit que ce n'est pas encore là (code 6) ; il viendra
  avec l'export des disques et les points de restauration (plan, section 11 ; question ouverte
  après la série 6 sur l'export d'une seule bouteille).

### Où vivent les préfixes dans l'invité (choix)

Les préfixes vivent **sur le disque de la VM**, dans sa couche d'écriture persistante (chantier 6b) :
un programme installé dans un préfixe survit à `x86 vm stop`. Ce qui change dans linux-base (et
donc dans l'image Wine, qui en part) :

- **La racine reste en lecture seule pour tout le monde, `/home` est inscriptible.** L'init remonte
  le système de fichiers en écriture, puis remonte le point de montage `/` en lecture seule
  (`mount -o remount,bind,ro /`) ; `/home` devient un montage lié du disque, `nodev,nosuid`
  (R134.5) ; root seul a une vue inscriptible de la racine, `/run/osx86-admin/root` (0700), par où
  `prefix-admin` remplace `/etc/passwd` et `/etc/group` d'un seul `rename`. Un usager ne peut rien
  écrire hors de son home et des `tmpfs` qui l'étaient déjà (les 22 contrôles R134.5 tiennent, test
  d'image de 210 A).
- **Journal ext4** (8 Mio) : une VM peut s'arrêter salement (page fermée) ; le journal est rejoué au
  démarrage suivant. Avant le chantier 10, la racine était sans journal parce que toujours en
  lecture seule.
- **256 Mio de place libre** dans l'ext4 (`OSX86_FREE_MIB`), pour les préfixes : des zéros, qui ne
  coûtent presque rien au pack (linux-base 73,13 Mo contre 73,05 ; Wine 365,9 Mo contre 365,8).
- **Écritures vidées vite** : `vm.dirty_expire_centisecs` et `dirty_writeback_centisecs` à 1 s, et
  l'agent fait `sync` avant qu'Os-x86 n'arrête le moteur.

`/usr/lib/osx86/prefix-admin ensure|delete <préfixe>` (root, lancé par l'agent seul) fait
l'usager, son home, puis les étapes de l'image dans `/usr/lib/osx86/prefix.d/` : l'image Wine y met
`50-wine`, qui copie le gabarit par `prefix-copy` (chantier 9). Codes : 0 fait, 10 rien à faire,
autre chose un échec dont la dernière ligne dit la cause.

## Le protocole 2 de l'agent

L'agent n'avait aucune opération pour créer un usager ni copier le gabarit (note du chantier 5).
Plutôt qu'une opération générique « exécuter comme root », **des opérations nommées** qui appellent
un script fixe de l'image avec un nom de préfixe déjà vérifié : rien de ce que l'hôte envoie n'est
interprété par un shell, et un programme invité ne peut rien demander à l'agent (son port est root).
Détail dans [`agent.md`](agent.md#le-protocole-version-2).

| `op` | Ce qu'elle fait |
| --- | --- |
| `prefix-ensure` | lance `prefix-admin ensure <préfixe>` ; rend `created`, `uid`, `home`. Une image sans `prefix-admin` (Alpine de test) répond pour les usagers qu'elle porte |
| `prefix-delete` | `cgroup.kill` de tout le préfixe, puis `prefix-admin delete` ; rend `deleted` |
| `prefix-list` | les usagers `osx86-*` de l'invité |
| `put-file` | écrit des octets (base64) dans le home de l'usager, **comme lui** : l'enfant devient l'usager avant d'ouvrir quoi que ce soit, crée les dossiers manquants en 0700, ne suit pas un lien final |
| `sync` | `sync()` dans un enfant |

Le client accepte un agent de version 1 (images plus anciennes) : les opérations nouvelles y
répondent `unknown-op`, et Os-x86 suppose alors que l'image porte ses usagers.

## La détection (162 A, 240 D, 252 à 255 A)

`ProgramDetector.Detect(en-tête, extension)` : **l'en-tête d'abord, l'extension quand l'en-tête ne
dit rien, une contradiction refusée**. La classe est pure : ni moteur, ni fichier, ni contexte
d'app, aucun état. Elle a la forme du **détecteur de format déclaré par la Feature** quand Kazo
codera l'ADR 0251 (252 A) : un nom (`ca.kazo.x86.programs`), un constructeur sans argument,
`DetectAsync(en-tête, extension, annulation)`.

| Fichier | Type MIME (255 A) | `Kind` | Détail |
| --- | --- | --- | --- |
| `MZ` + `PE\0\0` | `application/vnd.microsoft.portable-executable` (IANA) | `Windows` | PE32 ou PE32+, console ou fenêtré, machine, .NET (en-tête CLR), DLL |
| `MZ` + `NE` | `application/x-ms-ne-executable` | `Windows` | Windows 16 bits |
| `MZ` seul (ou `LE`, `LX`) | `application/x-dosexec` | `Dos` | 16 bits |
| `.com` sans `MZ` | `application/x-dosexec` | `Dos` | image `.com`, par l'extension |
| `\x7fELF` | `application/x-executable` | `Linux` | 32 ou 64 bits, type, machine |
| `.bat`, `.cmd` | `application/x-bat` | `Batch` | par l'extension |
| `.msi` avec la signature OLE | `application/x-msi` | `WindowsInstaller` | |
| ELF nommé `.exe`, `.dll`, `.bat`, `.cmd`, `.msi` ; MZ nommé `.bat`, `.cmd`, `.msi` ; `.exe` sans `MZ` ; `.msi` sans OLE | `application/octet-stream` | `Unknown`, source `Conflict` | refusé |
| le reste | `application/octet-stream` | `Unknown` | |

**64 octets ou 4 Kio.** Le détecteur de l'ADR 0251 reçoit 64 octets : l'en-tête PE est plus loin
(`e_lfanew`, 0x80 en général). Avec 64 octets, un `MZ` dont la table de relocations est à 0x40 et
dont `e_lfanew` pointe au-delà de l'en-tête DOS est pris pour un exécutable Windows (ce qu'écrit
tout éditeur de liens Windows) : le type MIME est juste, les détails restent inconnus
(`IsComplete = false`). `x86 detect` et `x86 run` lisent 4 Kio et ont tout.

```text
$ x86 detect osx86-hello.exe
osx86-hello.exe: application/vnd.microsoft.portable-executable
  PE32 console i386 (by its header)
  x86 run: runs it in a virtual machine of Windows programs
$ x86 detect old.exe
old.exe: application/x-dosexec
  DOS MZ executable (by its header)
  x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal
```

## `x86 run`

```text
x86 run [--vm <vm>] [--prefix <préfixe>] [--cols N] [--rows N] <programme> [arguments...]
x86 prefix list [--vm <vm>]
x86 prefix create <nom> [--vm <vm>]
x86 prefix delete <nom> [--vm <vm>] [--force]
x86 prefix export <nom> [--vm <vm>]
x86 detect <fichier>
```

Les options de `x86 run` se mettent **avant** le programme : tout ce qui suit le programme lui est
passé tel quel, options comprises (`x86 run outil.exe --help`). Pas à pas :

1. **Détection** sur les 4 premiers Kio. Ce qui ne peut pas encore tourner est refusé avec la
   raison et ce qui viendra (`program-not-supported`, code 6) : Linux (pas encore
   par `x86 run` ; `x86 shell`), fenêtré, 16 bits et `.msi` (il leur faut un affichage : BoxedWine,
   chantier 13), 64 bits (le processeur émulé, v86, est 32 bits), DLL, autre machine. Passent : les
   programmes console PE32 i386, .NET compris (Mono est dans l'image), et les `.bat`/`.cmd` (par
   `cmd.exe /c`). Un programme DOS prend sa propre voie ([plus bas](#programmes-dos-chantier-11)).
2. **La VM (194 A)** : `--vm`, sinon la VM « programmes Windows » de l'usager (la seule ; parmi
   plusieurs, celle qui tourne, sinon la plus ancienne). S'il n'en a aucune, Os-x86 en crée une,
   `windows`, depuis le profil livré `wine` (512 Mio, préfixe par défaut `default`), et le dit sur la
   sortie d'erreur.
3. **Le préfixe (193 A)** : `--prefix` (doit exister), sinon le préfixe par défaut.
4. **La VM démarre** au besoin (166 A ; le programme est une application de la VM), image
   téléchargée au besoin avec sa progression (chantier 6b).
5. **Le préfixe est fait dans l'invité** s'il ne l'est pas (une fois par marche de la VM).
6. **Le programme est copié dans le préfixe** (choix ci-dessous) :
   `C:\osx86\run\<16 chiffres du SHA-256>\<nom>`, soit
   `~/.wine/drive_c/osx86/run/<empreinte>/<nom>` ; une copie des mêmes octets déjà là (même taille,
   dossier nommé par l'empreinte) n'est pas refaite.
7. **La session Wine du préfixe** est démarrée si aucune ne tourne (conception plus bas).
8. **Le programme tourne sur un pty** de l'agent (213 A) : `wine <chemin> <arguments>`, dossier de
   travail celui du programme, `WINEDEBUG=-all`. La console est relayée au terminal ligne par ligne
   (comme `x86 shell`, chantier 6) : sortie filtrée, chaque ligne tapée part avec CR, `^C` et le
   Ctrl+C du terminal envoient Ctrl+C au programme (216 A), `~.` ferme la console.
9. **Le code de sortie du programme est celui de la commande (110)**.

```text
$ x86 run osx86-hello.exe a b
x86: created the virtual machine windows (031a…) for Windows programs, from the profile wine.
x86: wine 0.1.0 is not on this machine yet: downloading it.
…
osx86-hello windows=C:\windows user=osx86-default args=2 a b
$ echo $?
7
```

### Copie plutôt que partage (choix)

Le programme est **copié** dans le préfixe par l'agent (`put-file`, comme l'usager du préfixe), et
non partagé : le partage du dossier de Kazo par 9p (D22, E8) n'existe pas encore dans le moteur, et
une copie dans le préfixe a les droits du préfixe (144) sans rien ouvrir d'autre. Limites : un
programme seul (pas les DLL posées à côté de lui), 256 Mio au plus, et la copie coûte un aller-retour
par morceau de 6 Kio. Ce n'est pas la taille de ligne de l'agent (64 Kio) qui borne les morceaux :
**v86 livre une écriture de l'hôte vers l'invité par morceaux de 1 Kio, un par tampon de réception
de l'invité, et jette ce qui ne trouve pas de tampon libre** ; une ligne de 49 Kio s'est perdue au
premier essai. Une requête à la fois, chacune attendue, laisse à l'invité le temps de reposter ses
tampons. Le partage par 9p, avec la traduction des chemins (sujet sans numéro de la série 15), est
la voie suivante pour les gros programmes.

### Le `wineserver` d'un préfixe (conception)

Constat du chantier 9 : un `wineserver` démarré par une commande appartient au cgroup de cette
commande, que l'agent termine à la fin de son programme (SIGHUP puis `cgroup.kill`, 208 A) ; un
second programme connecté à ce serveur reste bloqué ou meurt avec le premier. Les services de la
session (`services.exe`, `plugplay`, `winedevice`), lancés par le premier programme, ont le même
sort.

**Choix : une session Wine par préfixe, lancée à part.** Avant le premier programme d'un préfixe,
Os-x86 lance par l'agent `/usr/lib/osx86/wine/prefix-session start <jeton>`, un lancement à lui
(son cgroup, 206 A), enfant de la VM de type `Session` (207 A) mais pas une application : il ne
garde pas la VM en marche. Le script démarre `wineserver -p` (persistant ; sans `-f`, il rend la
main une fois prêt), puis un premier processus Wine (`wine cmd.exe /c exit 0`) qui fait le
`wineboot --init` de la session : serveur et services sont dans le cgroup de la session, pas dans
celui d'un programme. Un `exec-in-prefix` de `prefix-session wait <jeton>` revient quand la session
est prête. Les programmes s'y connectent ; la fermeture de leur console ne touche pas la session.
**Arrêt** : `PrefixSessionGrace` (30 s) après la fin du dernier programme du préfixe, Os-x86 tue le
lancement (SIGTERM puis `cgroup.kill`) ; la session meurt aussi avec la VM. Un programme lancé
pendant le délai réutilise la session.

Options écartées : `wineserver -p<n>` qui s'arrête seul (course entre son départ et un programme
qui arrive, dont le serveur de repli tomberait dans le cgroup d'un programme), et une session par
programme (perd l'intérêt du second lancement rapide et laisse les services d'un programme à
l'autre).

### Ce que voit le terminal

- **CR comme Entrée.** La console de Wine lit son terminal en mode brut : LF y est Ctrl+J et ne
  termine pas une ligne. Le relais envoie désormais chaque ligne avec CR, ce qu'envoie la touche
  Entrée ; un shell (`x86 shell`) le reçoit en LF par sa discipline de ligne (ICRNL).
- **Espaces.** La console de Wine dessine les blancs d'une ligne par des déplacements du curseur
  (`CSI n C`) : le filtre de ligne les rend en espaces (`a b` sortait `ab`).
- **Le pty naît en mode brut (chantier 10b).** La console de Wine ne passe le terminal en mode brut
  qu'à sa première lecture. Une ligne tapée juste avant passait par la discipline de ligne : son CR
  devenait LF (ICRNL), que Wine prend pour Ctrl+J, et la ligne ne se terminait jamais (8 fois sur
  12 dans un essai en boucle, intermittent en CI). L'agent ouvre maintenant le pty d'un programme
  dans le mode que Wine réglera (`open-console` avec `raw`) : ce qui est tapé arrive tel quel, quel
  que soit le moment.
- **Code de sortie.** Celui de `wine`, donc celui du programme modulo 256 (un code Windows plus
  grand, comme `0xC000013A`, arrive tronqué) : après Ctrl+C, le programme sort avec 58.

## Programmes DOS (chantier 11)

Jalon 0, chantier 11 (`j0/11-dos`). `x86 run` d'un programme DOS (`application/x-dosexec` : un
`.com`, ou un MZ sans en-tête plus récent) le fait tourner dans une VM FreeDOS et montre son écran
texte dans le terminal, clavier compris. Décisions : 111 (FreeDOS), 164 A (DOS graphique), 166 A
(arrêt de la VM avec sa dernière application), 212 A (image sans agent), 214 A (voie VGA à 33 ms,
comparaison par ligne, attributs), 215 A (écran plein, Ctrl+lettre, accord Échap), 216 A (Ctrl+C au
programme), 217 A (signal du mode graphique), 218 A (champs internes de v86 dans un seul
adaptateur). Le passage par le shell de Kazo (`./prog.com`, ADR Kazo 0251) et l'affichage enrichi
d'un programme invité par le Terminal (ADR Kazo 0252) attendent que Kazo ait codé ces ADR ; d'ici
là, la Feature se sert de l'`IFullScreenTerminalSession` du SDK de Kazo 3.4.0.

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Dos/DosProgramDisk.cs` | le disque du programme : MBR, une partition FAT12, les fichiers à la racine |
| `src/OsX86/Dos/DosRunScript.cs` | noms 8.3, ligne de commande DOS, `OSX86RUN.BAT`, lecture des marqueurs de COM1 |
| `src/OsX86/Dos/GuestKeyboard.cs` | `GuestKey`, traduction en scancodes PS/2 (jeu 1, clavier US), accord Échap (`EscapeChord`) |
| `src/OsX86/Dos/TextScreenDiff.cs` | la comparaison par ligne (texte et attributs) et du curseur |
| `src/OsX86/Vm/VmManager.Dos.cs`, `VmInstance.Dos.cs`, `DosProgramSession.cs` | choix de la VM DOS, démarrage avec le disque du programme et le clavier, session (écran, touches, début, fin, mode graphique), arrêt |
| `src/OsX86/Engine/EngineTextScreen.cs` | l'écran texte avec attributs et curseur, `ITextModeEngine` |
| `src/OsX86/wwwroot/engine/vga-text.js` | le seul lecteur des champs internes de la VGA de v86 (218 A) |
| `src/OsX86.Feature/X86Command.Dos.cs` | `x86 run` d'un programme DOS |
| `src/OsX86.Feature/GuestScreenTerminal.cs` | `IGuestScreenTerminal` (la frontière que l'ADR 0252 enrichira), son adaptateur `KazoFullScreenTerminal` (SDK 3.4.0), les couleurs VGA |
| `src/OsX86.Feature/DosTerminalBridge.cs` | le pont : sondage, comparaison, rendu, touches, mesures |
| `images/freedos/FDAUTO.BAT`, `images/freedos/programs/` | l'appel de `D:\OSX86RUN.BAT` ; les programmes de test en assembleur (NASM) |
| `tests/OsX86.Tests/Dos/`, `Engine/TextModeEngineTests.cs` | tests `unit` |
| `tests/OsX86.Tests/Vm/X86RunFreeDosGuestTests.cs` | test `guest` sur FreeDOS |

```text
$ x86 detect old.exe
old.exe: application/x-dosexec
  DOS MZ executable (by its header)
  x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal
$ x86 run outil.com a1 b2
x86: created the virtual machine dos (…) for DOS programs, from the profile dos.
        (l'écran du programme occupe le terminal jusqu'à sa fin)
OSX86 DOS TEST                          (puis son dernier écran reste dans l'historique)
ARGS:[ a1 b2]
…
```

### `x86 run` d'un programme DOS

1. **Détection** (162 A) : un programme `application/x-dosexec` prend la voie DOS. `--prefix`,
   `--cols` et `--rows` sont refusés (code 2) : une VM DOS n'a pas de préfixe et l'écran a la taille
   que le programme lui donne.
2. **Le terminal doit offrir l'écran plein** (`TerminalExecutionContext.FullScreen`) ; sinon (script,
   tube), refus avec code 6.
3. **La VM DOS** : `--vm` (une VM du genre `dos`, arrêtée), sinon la plus ancienne VM DOS arrêtée de
   l'usager ; s'il n'en a pas d'arrêtée, Os-x86 en crée une, `dos` (puis `dos-2`…), depuis le profil
   livré `dos` (image `freedos` 0.2.0, 64 Mio, sans agent ni console série, 212 A), et le dit.
4. **Le disque du programme** est fait en mémoire (choix plus bas) et la VM démarre avec lui comme
   `hdb` et le clavier actif ; l'image est téléchargée au besoin avec sa progression (chantier 6b).
5. **L'écran** du terminal passe en écran alterné : « starting the DOS virtual machine… » pendant que
   FreeDOS démarre, puis l'écran du programme dès que le lot l'annonce sur COM1.
6. **Les touches** tapées vont au programme ; Ctrl+C aussi (216 A).
7. **La fin** : quand le lot écrit le code du programme sur COM1, le dernier écran est lu, l'écran
   alterné rendu, ce dernier écran écrit sur la sortie (il reste dans l'historique du terminal), et la
   VM **s'arrête aussitôt** (166 A, sans délai de grâce : voir plus bas). Le code du programme
   (`ERRORLEVEL`, 0 à 255) est celui de la commande (110).
8. **Mode graphique** : message, arrêt du programme et de la VM, code 6 (217 A).

`OSX86_DOS_STATS=1` dans l'environnement fait écrire les mesures des rafraîchissements sur la sortie
d'erreur (`x86: DOS screen: … polls, … frames, … rows changed; read …; frame …; key to screen …`,
médiane, 90e centile et maximum en millisecondes).

### La VM DOS (choix)

- **Un programme par démarrage.** Le programme arrive sur un disque qu'on ne peut donner qu'au
  démarrage (v86 ne branche pas de disque à chaud) : une VM DOS en marche est occupée ; nommée par
  `--vm`, elle est refusée (`vm-busy`, code 5) ; sans `--vm`, Os-x86 prend une VM DOS arrêtée ou en
  crée une autre (165 A : pas de question sous la limite de mémoire, que le démarrage vérifie).
- **Arrêt aussitôt la fin.** Le programme est l'application de la VM (166 A). À sa fin, la VM
  s'arrête sans attendre le délai de grâce, qui ne garderait qu'une VM que personne ne peut plus
  utiliser. Une VM DOS tenue par `x86 vm start` (sans programme) ne prend pas de programme.
- **Pas d'agent.** FreeDOS n'en a pas (212 A) : le démarrage d'une VM `dos` n'attend aucune connexion
  d'agent. `x86 shell --vm dos` est refusé (`no-console`) : le profil ne déclare pas de console
  série, COM1 ne porte que le début et la fin d'un programme.
- **Le disque de FreeDOS (`hda`)** garde la couche d'écriture persistante de la VM (chantier 6b) : ce
  qu'un programme écrit sur `C:` reste d'un lancement à l'autre.

### Le programme sur un second disque (choix)

Le programme et son lot vont sur un **second disque dur** (`hdb`), fait en mémoire par le C# à chaque
lancement et envoyé avec les options de démarrage (`EngineFileSource.InMemory`, source `{inline}` de
`disk.js`) : jamais rangé dans l'OPFS, ses écritures jetées avec le moteur. Écarté : écrire le
programme dans la couche d'écriture de `hda`, qui demanderait de tenir à jour depuis l'hôte la FAT16
de FreeDOS (allocation, répertoire) dans une couche que l'invité écrit lui aussi, et laisserait des
copies sur le disque de la VM.

- **Format** : un MBR, une partition primaire FAT12 (type 0x01) au secteur 63, géométrie de v86
  (16 têtes, 63 secteurs par piste), taille arrondie au cylindre. FreeDOS la monte en `D:` (`hda` a
  une seule partition, `C:`). FAT12 parce que le disque est petit : au plus 16 Mio de fichiers, plus
  1 Mio de place libre pour ce que le programme écrit à côté de lui (perdu à l'arrêt).
- **Fichiers** : le programme sous un nom 8.3 (`ToShortName` : dernier élément du nom, majuscules,
  lettres, chiffres, `_` et `-`, 8 caractères, extension `.COM` ou `.EXE` gardée, sinon `.EXE` pour un
  MZ et `.COM` pour le reste ; un nom de périphérique comme `CON`, ou celui du lot, prend un `_`) et
  `OSX86RUN.BAT`.
- **Arguments** : joints par des blancs, un argument avec un blanc entre guillemets, `%` doublé pour
  le lot ; refusés (code 6) : ce qui n'est pas de l'ASCII imprimable, `|`, `<`, `>`, `"`, et une ligne
  de plus de 125 caractères (la queue de commande de DOS).

### Lancement et fin sans agent

Le `FDAUTO.BAT` de l'image (version 0.2.0) finit par `if exist D:\OSX86RUN.BAT call D:\OSX86RUN.BAT`.
Le lot, écrit par Os-x86 :

```bat
@ECHO OFF
REM Os-x86 (work item 11): runs one DOS program, its end and ERRORLEVEL told on COM1.
CLS
D:
CD \
ECHO OSX86-START >COM1
D:\OUTIL.COM a1 b2
ECHO OSX86-EXIT %ERRORLEVEL% >COM1
PAUSE >NUL
```

Les deux marqueurs passent par **COM1** (le port série, jamais l'écran) ; la session les lit sur
`serial0`. `OSX86-START` dit que FreeDOS a démarré et que le programme part (l'écran est montré à
partir de là) ; `OSX86-EXIT <n>` donne le **vrai code de sortie** (`%ERRORLEVEL%` de FreeCOM, vérifié
par le test `guest` : 3 et 42). `PAUSE >NUL` attend une touche qui ne vient jamais (FreeDOS reste
oisif avec `FDAPM APMDOS`) : le dernier écran du programme ne bouge plus jusqu'à l'arrêt de la VM.
Sans le disque `D:`, `FDAUTO.BAT` rend l'invite comme avant, sans message (vérifié par
`FreeDosGuestTests`).

### L'écran (214 A, 218 A)

- **Lecture** : le pont lit l'écran toutes les **33 ms** (`ReadTextScreenAsync`). Le worker le lit
  dans la mémoire VGA de v86 par `vga-text.js`, **seul fichier qui touche les champs internes de
  v86** (`vga_memory`, `start_address`, `cursor_address`, `attribute_mode`… de v86 0.5.473, 218 A) :
  caractères CP437 rendus en Unicode (glyphes VGA, `☺`, `╔`…), un octet d'attribut par cellule, le
  curseur matériel (position, montré ou caché), le clignotement. Si une version de v86 n'a plus ces
  champs, il retombe sur les caractères de l'événement public `screen-put-char`, en gris sur noir,
  sans curseur.
- **Comparaison par ligne** (`TextScreenDiff`) : seules les lignes dont le texte ou les attributs ont
  changé sont reformatées ; rien n'est envoyé au terminal quand ni une ligne ni le curseur n'ont
  changé.
- **Rendu** (`IGuestScreenTerminal`, adaptateur `KazoFullScreenTerminal`) : le SDK 3.4.0 remplace la
  trame entière à chaque `RenderAsync` ; chaque ligne porte ses couleurs en séquences SGR 24 bits
  (palette VGA par défaut ; le thème du terminal ne change pas l'allure d'un programme DOS), comme
  `htop` colore ses lignes ; le curseur suit celui de la VGA. Quand la fenêtre a moins de lignes que
  l'écran du programme, les lignes montrées suivent le curseur ; les colonnes au-delà de la largeur
  sont coupées (80 x 25 voulus, 215 A). Un redimensionnement redessine tout.
- **L'ADR 0252** enrichira `IGuestScreenTerminal` (couleurs par cellule et touches de fonction
  natives, envoi des seules lignes changées si le Terminal le permet) sans toucher au pont : les
  capacités (`GuestTerminalCapabilities`) disent déjà ce qu'un terminal sait faire.

### Le clavier et les accords (215 A, 216 A)

Le clavier PS/2 de v86 reçoit des **scancodes du jeu 1** (commande `keys` du worker), pour la voie
DOS seulement : le worker refuse `keys` à un moteur démarré sans `keyboard: true`.

| Touche du terminal | Ce que reçoit le programme |
| --- | --- |
| lettres, chiffres, ponctuation du clavier US | la touche, avec Maj quand il le faut (`A`, `!`) |
| Entrée, Retour arrière, Tab, Maj+Tab, Échap | la touche |
| flèches, Début, Fin, Suppr, Page préc., Page suiv. | la touche étendue (préfixe 0xE0) ; Kazo 3.4.0 les a |
| Ctrl+lettre | Ctrl tenu autour de la lettre ; **Ctrl+C va au programme** (216 A) |
| Alt+lettre (préfixe Méta décodé par Kazo) | Alt tenu |
| **Échap puis lettre** | **Alt+lettre** (accord, 215 A) |
| **Échap puis 1 à 9, 0** | **F1 à F10** |
| **Échap puis `-`, `=`** | **F11, F12** (les deux touches qui suivent 0 sur la même rangée : proposition de ce chantier) |
| Échap puis Échap | un Échap |
| Échap seul | envoyé après 800 ms sans seconde touche |
| Échap puis une autre touche | Échap puis cette touche |
| caractère sans touche sur un clavier US (`é`…) | rien |

Alt est tenu en trois envois espacés de **40 ms** (Alt enfoncé, la touche, Alt relâché) : E14 a
mesuré qu'EDIT ignore Alt+F envoyé d'une rafale. En écran plein, le Terminal de Kazo 3.4.0 donne
Ctrl+C au programme comme une touche quand rien n'est sélectionné (une sélection est copiée). Pour
**tuer** un programme DOS qui ne rend pas la main : `x86 vm stop --force dos` dans un autre onglet du
terminal (la commande rend alors `console-closed`) ; ni Ctrl+Pause ni bouton du Terminal en 3.4.0.

### Mode graphique (217 A, 164 A)

Le signal est l'événement `screen-set-size` de v86 avec `bpp ≠ 0`, remonté par le worker
(`screen-size`) puis `IEngine.ScreenSizeChanged`. Seul compte un passage après le début du
programme (le BIOS et FreeDOS changent de mode en démarrant) ; un passage vu avant la ligne
`OSX86-START` compte dès qu'elle arrive (COM1 est vidé toutes les 4 ms alors que le changement de
mode part aussitôt : le test `guest` l'a montré). Sans BoxedWine, l'écran alterné est rendu, le
message dit le mode et la décision, le programme et la VM sont arrêtés, code 6 :

```text
x86: the DOS program switched to a graphical mode (320x200, 8 bits per pixel). Graphical DOS programs are shown by BoxedWine (decisions 164 A and 217 A), which is not installed: the program and its virtual machine dos were stopped.
```

La reprise de la VM par BoxedWine (217 A : même VM, canvas dans une fenêtre, retour au Terminal en
mode texte) viendra avec BoxedWine (chantier 13).

### Mesures (DOS)

Test `guest` `X86RunFreeDosGuestTests`, trois passages, même hôte que plus bas (Windows 11, Ryzen 7
3700X, Chrome sans tête par Playwright, pack FreeDOS depuis une origine locale) :

| Mesure | Valeur |
| --- | --- |
| Premier `x86 run` : VM créée, pack téléchargé et vérifié (16,4 Mio), démarrage de FreeDOS jusqu'à l'écran du programme | 2,7 à 2,9 s |
| `x86 run` suivant (image installée), jusqu'à l'écran du programme | 2,5 à 2,7 s |
| Programme graphique, du lancement à l'arrêt de la VM | 2,6 à 2,9 s |
| **Touche → écran** (de la touche reçue du terminal au rendu qui la montre ; 20 touches une à une) | médiane 47,8 à 48,6 ms, p90 49,5 à 51,6 ms, max 64 à 65 ms |
| Lecture de l'écran (aller-retour C# → worker, 80 x 25 avec attributs) | médiane 1,8 à 2,0 ms, max 2,8 à 3,9 ms |
| Formatage et rendu d'une trame | ≤ 0,3 ms |
| Tours de sondage utiles | 22 trames pour 35 tours pendant la frappe ; aucun envoi à l'oisiveté |

La touche → écran rejoint E14 (environ 50 ms en médiane, moins de 100 ms) : la part de FreeDOS
(réveil de v86 au tic du PIT, 55 ms, avec `FDAPM APMDOS`) domine, pas le terminal.

### Vérifié dans l'hôte de développement

Dans le Terminal de Kazo 3.4.0 de `OsX86.Os` (session invitée), avec `OSX86KEY.COM` et `OSX86GFX.COM`
envoyés par le bouton « Upload » de l'Explorateur de fichiers puis copiés dans
`~/.config/ca.kazo.x86/` (la commande ne lit que son dossier privé, voir les limites) :
`x86 run …/osx86key.com a1 b2` crée la VM `dos`, télécharge FreeDOS 0.2.0, montre l'écran (titre
jaune sur bleu, curseur), reçoit `a`, `B`, Tab, Échap+x (`K=2D00`, Alt+X), Échap+1 (`K=3B00`, F1),
la flèche haut, Ctrl+A, Ctrl+C (code 3) ; un second lancement finit par Entrée (code 42) ; le
programme graphique rend le message et le code 6 ; `x86 vm list` montre la VM `dos` arrêtée après
chaque programme. Le Ctrl+C tapé par l'outil d'automatisation du navigateur n'atteignait pas la page
(l'outil le prend pour « copier ») : envoyé à xterm.js comme une touche, il est arrivé au programme.

### Tests (DOS)

- **`unit`** : `DosProgramDiskTests` (le disque relu comme DOS le lit : MBR, BPB, chaînes de
  clusters, FAT12 par le nombre de clusters, gros programme, noms 8.3, arguments, lot, marqueurs),
  `GuestKeyboardTests` (scancodes, Maj, Ctrl+C, Alt tenu 40 ms, touches étendues, F1 à F12, accord
  Échap, délai d'Échap seul, comparaison des lignes et des attributs), `DosRunTests` (choix et
  création de la VM DOS, démarrage avec `hdb` en mémoire, clavier et sans agent, marqueurs de COM1,
  arrêt aussitôt, mode graphique avant et après la ligne de début, VM arrêtée sous le programme,
  refus), `DosTerminalTests` (`x86 run` sur un écran plein simulé : écran, couleurs, curseur, touches
  et accords, code de sortie, dernier écran, mesures, mode graphique, VM morte, refus ; trames,
  fenêtre plus petite, touches de Kazo), `TextModeEngineTests` (commande `keys`, option `keyboard`,
  source `{inline}`, écran avec attributs), et `disk-tests.js` sous Node (disque `{inline}`,
  `vga-text.js` sur une VGA simulée et sans ses champs).
- **`guest`** : `X86RunFreeDosGuestTests`, sur le pack FreeDOS et les programmes de test, dans un
  vrai navigateur, avec le moteur, le stockage et le `VmManager` de production et un écran plein
  simulé (le Terminal de Kazo n'existe pas dans le test) : `x86 detect` ; `x86 run osx86key.com a1 b2`
  (écran, couleurs, curseur, `a`, `B`, Tab, Alt+X et F1 par l'accord, flèche, Ctrl+C → code 3, VM
  arrêtée) ; un second `x86 run` (20 touches une à une, Entrée → 42) ; `x86 run osx86gfx.com` (mode
  13h → message, code 6, VM arrêtée). Son nom contient `FreeDosGuestTests` : le job `guest`
  d'`images.yml` le lance avec le pack et l'artefact `freedos-test-programs`.
- **Programmes de test** : `images/freedos/programs/osx86key.asm` (titre en couleur, arguments, lit
  les touches par l'int 16h du BIOS, écrit `K=` scancode et ASCII, Entrée → 42, Ctrl+C → 3, int 23h
  détourné pour que DOS ne prenne jamais Ctrl+C) et `osx86gfx.asm` (mode 13h, une diagonale, attend
  une touche) ; assemblés par NASM 3.01 épinglé (`nasm=3.01-r0` d'Alpine 3.24) dans le conteneur
  `osx86-nasm:3.01`, jamais commis.

### Limites et non vérifié (DOS)

- **Lecture des fichiers de l'usager** : la commande `x86` ne déclare que `filesystem.private.read`
  et `.write` ; dans Kazo elle ne lit donc pas `~/prog.com` (« no file »), ni un programme Windows
  (même limite pour le chantier 10). Les permissions déclarées viennent avec la Feature (chantier 8).
- **Ctrl+C pris par DOS** : un programme qui lit par les fonctions de DOS avec `BREAK` peut être
  arrêté par DOS lui-même, et FreeCOM demande alors s'il faut arrêter le lot (« Terminate batch job
  (Y/N)? ») ; non provoqué. Le programme de test lit par le BIOS et détourne l'int 23h.
- **Non vérifié** : EDIT et d'autres programmes réels de FreeDOS par `x86 run` (seuls les programmes
  de test) ; un mode texte de 50 lignes ; un programme `.EXE` réel et un programme qui écrit sur
  `D:` ; Firefox et Safari ; l'ADR 0252 (pas codée).
- Un seul fichier copié : un programme qui a besoin de fichiers à côté de lui ne les trouve pas.
- Pas de clavier autre que US (pas de `KEYB` dans FreeDOS, un `é` ne part pas) ; pas d'Alt+pavé
  numérique.
- 16 couleurs de la palette VGA par défaut : une palette changée par le programme (DAC) n'est pas
  suivie ; le clignotement (bit 7 quand il est actif) n'est pas rendu.

## `Ix86` : ce qui est ajouté, et pourquoi

| Ajout | Pour qui |
| --- | --- |
| `DetectProgramAsync(en-tête, nom ou extension)` → `ProgramDetection` | BoxedWine et le Terminal décident quoi faire d'un fichier (162 A : le Terminal exécute lui-même un programme console, sinon il l'envoie à BoxedWine) |
| `RunProgramAsync(ProgramRunOptions)` → `IVmConsole` | lancer un programme console avec sa console (213 A), son Ctrl+C (216 A) et son code de sortie (110, `IVmConsole.Exited`) |
| `ProgramRunOptions` | le contenu du programme, son nom, ses arguments, la VM et le préfixe facultatifs, la taille du terminal |
| `ProgramDetection`, `ProgramKind`, `ProgramSubsystem`, `ProgramDetectionSource`, `ProgramMimeTypes` | le résultat de la détection |
| `X86ErrorCodes.PrefixNotFound`, `PrefixExists`, `ProgramNotSupported` | des refus à code stable |

Pas dans `Ix86` : la gestion des préfixes (`x86 prefix` seulement ; BoxedWine la demandera avec
son interface) et la permission `x86.programs.launch` (chantier 8). `RunProgramAsync` prend les
octets du programme plutôt qu'un chemin : la Feature n'a pas à lire les fichiers de l'appelant.

## Mesures

Hôte de développement des tests `guest` : Windows 11, Ryzen 7 3700X, Chrome sans tête par
Playwright, pack Wine copié depuis une origine locale.

| Mesure | Valeur |
| --- | --- |
| Premier `x86 run osx86-hello.exe a b` : VM créée, pack téléchargé et vérifié (348,9 Mio), démarrage, préfixe créé (gabarit copié), session Wine, programme | 37,0 à 37,7 s (trois passages) |
| Second `x86 run` (VM et session en marche, programme déjà copié) | 3,3 à 3,5 s |
| `x86 shell --prefix default` après `x86 vm stop`, démarrage compris (le fichier écrit avant l'arrêt est là) | 20,0 s |
| `x86 run` juste après, VM en marche mais session à démarrer (cache de l'invité froid) | 15,3 s |
| Ctrl+C sur un programme qui attend : code de sortie rendu | 58 (`0xC000013A`, `STATUS_CONTROL_C_EXIT`, tronqué à 8 bits) |
| Ligne tapée juste après l'annonce de lecture du programme | perdue une fois sur deux passages, lue au second essai |

Le démarrage de la session Wine (le `wineboot --init` de la session) coûte environ 12 s quand le cache
de l'invité est froid ; c'est ce que le second `x86 run` n'a plus à payer. Le préchargement des
fichiers chauds du chantier 9 tourne au démarrage de l'image et en prend une part.

## Tests

- **`unit`** : `ProgramDetectorTests` (en-têtes construits octet par octet, aucun binaire dans le
  dépôt : PE32, PE32+, console, fenêtré, .NET, DLL, machines, NE, DOS, LE, ELF 32 et 64 bits, gros
  et petit boutiste, `.bat`, `.cmd`, `.com`, `.msi`, contradictions, 64 octets seuls, en-têtes
  coupés), `AgentPrefixTests` (protocole 2 : opérations, morceaux de `put-file`, chemins refusés,
  agent de version 1), `ProgramRunTests` (choix de la VM, préfixe par défaut, suppression en
  attente faite au démarrage, session réutilisée puis arrêtée après son délai, session qui ne
  démarre pas, `sync` avant l'arrêt, `.bat`, refus), `X86ProgramCommandTests` (sortie exacte et code
  de `x86 detect`, `x86 run`, `x86 prefix`), plus les tests mis à jour du chantier 6.
- **`guest`** : `X86RunWineGuestTests`, sur le pack Wine, dans un vrai navigateur, avec le moteur,
  le stockage et le `VmManager` de production : `x86 detect` ; premier `x86 run osx86-hello.exe a b`
  (sa ligne au terminal, code 7) ; second `x86 run` plus rapide (code 3) ; une ligne tapée arrive au
  programme ; Ctrl+C interrompt un programme qui attend ; `x86 prefix create work`, un programme y
  tourne comme `osx86-work` ; deux préfixes ne lisent pas les fichiers l'un de l'autre ;
  `x86 prefix list` et `delete` ; après `x86 vm stop`, le préfixe et un fichier écrit dedans sont
  toujours là. Son nom contient `WineGuestTests` : le job `guest-wine` d'`images.yml` le lance avec
  le pack et `osx86-hello.exe`.
- **Test de fumée de l'agent** (`build-agent.py --smoke`) : `prefix-list`, `prefix-ensure` sans
  `prefix-admin`, `put-file` en deux morceaux comme l'usager, `sync`, les refus.

## Ce qui attend les ADR Kazo 0251 et 0252, et les autres chantiers

- **ADR 0251** (route du shell vers les apps à capacité d'exécution) : la Feature déclarera
  `ProgramDetector` comme détecteur de format (`FormatDetectors`, 252 A) et une capacité
  d'exécution pour `application/vnd.microsoft.portable-executable` et `application/x-bat` ; le shell
  appellera alors `x86 run` (ou `Ix86.RunProgramAsync`) pour `./hello.exe`. Rien n'est à changer
  dans la détection.
- **ADR 0252** (le Terminal affiche un programme invité) : le Terminal relaiera les octets bruts du
  pty à son émulateur VT au lieu du filtre ligne par ligne ; la frappe ira caractère par caractère
  (et la frappe perdue au démarrage de la console de Wine disparaîtra avec le mode ligne).
- **Chantier 8** : permission `x86.programs.launch` sur `RunProgramAsync`, enregistrement d'`Ix86`.
- **Chantier 13** : BoxedWine (fenêtrés, installateurs, DOS graphique).

## Limites connues et non vérifié

- **Non vérifié** : Firefox et Safari ; un programme .NET réel (Mono présent, non lancé) ; un
  programme plus gros que quelques dizaines de Kio (la copie est linéaire, environ un aller-retour
  par 6 Kio) ; une coupure de la page pendant une écriture de préfixe (le journal est là, non
  provoqué) ; la commande dans un Kazo autre que l'hôte de développement (pas d'émulateur avant le
  chantier 8).
- Un seul fichier copié : un programme qui a besoin de DLL ou de données à côté de lui ne les trouve
  pas (partage 9p à venir).
- Les préfixes d'une VM sont perdus avec sa couche (`x86 vm remove`) ; une mise à jour de l'image
  Wine garde des préfixes copiés de l'ancien gabarit (migration à faire, chantier 9).
- La copie d'un programme reste dans le préfixe (`C:\osx86\run`) ; rien ne la nettoie encore.
