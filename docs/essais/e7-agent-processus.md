# Essai E7 — agent et processus

Date : 2026-11-11. Code jetable dans `essais/e7-agent-processus/` (plan 2026-11-11 §4.2 ligne E7 ; nourrit §1.1 « agent invité », l'ADR 17 « processus invités », les rangées 102, 107/146, 139, 144, 145, 147/167, 190 du registre et R134.5). L'essai reprend l'invité de E17 (Debian trixie i386 + WineHQ 11.0, noyau `linux-virt` d'Alpine, racine 9p dans v86 0.5.473, Chrome headless par Playwright) et y ajoute un agent root statique qui parle à la page par un port virtio-console en JSON, deux bouteilles = deux usagers invités avec chacun une copie du préfixe préfabriqué, un cgroup v2 par lancement, une console d'usager sur un second port, un disque ext4 de bouteille, un partage 9p que l'hôte fait « disparaître », et l'image Alpine nue de E6 pour le tableau « sans agent ».

Non couverts : l'Internet réel et le réseau par bouteille (E6), le Web Worker, Firefox/Edge/Safari, la sauvegarde d'état avec agent, un vrai Windows ou FreeDOS (seulement Alpine nue pour 195 A), Kazo lui-même (le modèle de processus est une proposition sur les faits lus, rien n'est codé côté Kazo).

## But

Savoir si un agent dans l'invité, parlant en arguments structurés par virtio-console, suffit à : lancer un programme comme l'usager de sa bouteille, lister les processus et les fenêtres avec leur bouteille, **arrêter A sans toucher B ni la VM** (critère du plan), arrêter un arbre entier (enfants, démons) d'une bouteille sans toucher l'autre, ouvrir une console d'usager sur laquelle les garanties 1 et 2 de la section 134 tiennent (R134.5 : `unshare`, `nsenter`, lire une autre bouteille, parler à l'agent, chercher un setuid, plus `ptrace`, `kill` et une bombe de processus), savoir ce qu'un programme Wine écrit hors de son compte (102), détecter un partage disparu et rendre une erreur claire plutôt qu'un dossier vide (E8), lire le disque interne d'une bouteille depuis Node à l'arrêt et par l'agent en marche, et dire ce qui reste pour une image apportée sans agent (195 A). Mesurer les délais (aller-retour, liste, arrêt), la taille et la mémoire de l'agent.

## Ce qui a été construit

Tout est dans `essais/e7-agent-processus/` (README de 10 lignes).

- `agent/e7-agent.c` (456 lignes, C, compilé en statique avec musl i386 dans une étape Alpine du `Dockerfile`) : **binaire de 108 Kio, RSS 168-180 Kio** dans l'invité. Il tourne en root, ouvre `/dev/hvc1` en mode brut (le port virtio-console 1 de v86 ; v86 déclare ses 4 ports comme ports « console », donc Linux les expose en `hvc0..hvc3`, pas en `vport*`), lit une requête JSON par ligne et répond une ligne JSON portant le même `id` ; il émet des événements (`ready`, `exit`, `open-url`, `console-closed`). Analyseur et écrivain JSON minimaux faits main (pas de bibliothèque). Il se déclare `PR_SET_CHILD_SUBREAPER` : les orphelins des programmes lancés lui reviennent, d'où un événement `exit` pour chaque processus d'un arbre lancé.
- Opérations : `ping` (uptime, RSS de l'agent) ; `list-processes` (pid, ppid, pgid, uid, état, bouteille = usager `wine-<prefix>`, `comm`, ligne de commande, cgroup, lus dans `/proc`) ; `launch` {prefix, program, args[], env{}, cwd} : `fork`, `setsid`, cgroup `/sys/fs/cgroup/e7/<prefix>/<seq>`, `RLIMIT_NPROC` 256, `initgroups`/`setgid`/`setuid` vers `wine-<prefix>` (uid ≠ 0 exigé), environnement reconstruit (HOME, WINEPREFIX, DISPLAY=:0, WINEDEBUG…), `execvp` ; **jamais une ligne de shell** ; `kill` {pid, signal} avec `scope` = `pid` | `group` (pgid) | `prefix` (balayage de `/proc` par uid) | `cgroup` (`cgroup.kill` du lancement `seq` ou de toute la bouteille) ; refus de tuer un pid root sans `allow_root` ; `list-windows` (script auxiliaire `xwininfo -root -tree` + un `xprop` par fenêtre nommée : id, `_NET_WM_PID`, titre, bouteille déduite de l'uid du pid) ; `mounts` (pour chaque montage 9p/ext4/tmpfs : `statfs` + `opendir`/`readdir` dans un enfant, délai borné : état `ok` | `error` + errno | `hung`) ; `exec-in-prefix` {prefix, program, args, timeout_ms, max_output} : non interactif, entrée fermée, sortie capturée et bornée, `SIGKILL` du groupe à l'échéance ; `open-console` {prefix} : `openpty`, shell `sh -i` de l'usager de la bouteille sur l'esclave (son propre cgroup, `RLIMIT_NPROC`), pompe maître ↔ `/dev/hvc2` ; `close-console` (`SIGHUP` au groupe du shell).
- `open-url` (139) : pas une opération de l'hôte mais un canal montant : `agent/e7-open-url.c` (18 Kio) écrit l'URL sur le socket unix `/run/e7/open-url.sock` (0666) ; l'agent lit `SO_PEERCRED` et émet `{"event":"open-url","uid":1001,"prefix":"b1","url":…}` — il **ne fait rien d'autre** avec l'URL.
- `agent/e7-ptrace.c`, `agent/e7-rawsock.c` : sondes pour R134.5 (`PTRACE_ATTACH` sur un pid d'une autre bouteille ; ouverture d'un socket brut).
- `Dockerfile` : `FROM e17-wine-stable` + `inotify-tools`, `xterm`, `util-linux` ; usagers `wine-b1` (1001) et `wine-b2` (1002), home 0700 **sur la racine 9p** avec `cp -a /root/.wine` (le préfixe préfabriqué de E17, 727 Mio chacun : mêmes octets, donc dédoublonnés par hachage dans l'image 9p : **+0,6 Mio** zstd pour deux préfixes de plus) ; `/srv/share-b1` (le « partage » de b1, lié dans son home) ; tous les bits setuid/setgid retirés ; `guest/init.sh` (PID 1 : montages, `cgroup2` sur `/sys/fs/cgroup`, `/home` remonté `nosuid,nodev`, `user.max_user_namespaces=0`, `chmod 600` des `hvc*`, `ttyS0`, `fb0`, `input/*`, modules, agent, shell root de pilotage sur `ttyS0`). Image 9p : **10 943 fichiers, 380,7 Mio zstd** (E17 stable : 10 795 fichiers, 380,1 Mio) ; `fs.json` 1 140 Kio.
- `disk-seed/` + `build.ps1` : disque ext4 de **256 Mio** (`mkfs.ext4 -d`, e2fsprogs d'Alpine dans Docker) avec un `drive_c` minimal, attaché en `hda` **chargé en mémoire** (`{buffer}`), exportable vers le serveur depuis la page.
- `ext4-ls.mjs` : lecteur ext4 en lecture seule, jetable (superbloc, descripteurs 64 bits, extents, données en ligne, répertoires linéaires) pour lister `drive_c` depuis Node.
- `boot.html` (E17 + `virtio_console: true`, RPC `E7.agent()` apparié par `id` avec aller-retour mesuré, pompe du port 2 pour la console, **« partage disparu »** : interception de `virtqueue.pop_request` du périphérique 9p, la requête est lue dans la page ; si son fid vise un inode du sous-arbre en faute le serveur de la page répond `Rlerror EIO` et rend au vrai serveur une requête factice ignorée, sinon une copie rejouant les octets), `server.mjs` (E17 + repli lecture seule vers `e17-wine-dedie/lib`, `e17-wine-dedie/images`, `e6-reseau/images`), `drive.mjs` (pilotage : série, agent, console, faute, export du disque, injection 9p), `run.mjs` (mesure automatique), `run-alpine.mjs` (Alpine nue).

Configuration v86 : `memory_size` 1 Gio, VGA 8 Mio, racine 9p `cache=loose`, `console=ttyS0`, `virtio_console` (4 ports), `hda` ext4 256 Mio en mémoire.

## Protocole

Pour chaque run (`run.mjs`, contexte Chrome neuf, cache vide), dans l'ordre : (1) boot à froid jusqu'au prompt série et à l'événement `ready` de l'agent ; (2) injection par 9p des scripts corrigés après la construction de l'image (voir « ce qui a raté ») ; (3) 10 `ping`, 5 `list-processes` à vide ; (4) Xorg fbdev + twm + `xhost +si:localuser:wine-b1,wine-b2` (`guest/x-up.sh`) ; (5) montage du disque ext4 sur `/home/wine-b1/disk` et du partage sur `/home/wine-b1/share`, `mounts` ; (6) `inotifywait -m -r` sur `/tmp /var /dev/shm /run /etc /root /home/wine-b2 /usr/share /usr/lib /usr/local /opt /srv /mnt` (tout sauf le home de b1 ; **n'a pas abouti**, voir « ce qui a raté » : la mesure des écritures hors compte est faite à part par `run-writes.mjs`) ; (7) **A** = `launch b1 wine notepad`, scrutation de `list-windows` jusqu'à « Untitled - Notepad » ; (8) arrêt d'inotify, liste des chemins écrits hors du home de b1, lisibilité de chacun par b2 (`exec-in-prefix b2 test -r`) ; (9) **B** = `launch b2 wine winemine`, **C** = `launch b2 xterm` ; capture ; (10) `list-processes` ×5 et `list-windows` ×3 avec A, B, C ; (11) trois arbres `sh tree.sh` (parent + deux enfants dans son groupe + un enfant `setsid`) lancés en b1 et tués par `pid`, `group`, `cgroup` ; (12) console d'usager b2 sur hvc2 : `verify-console.sh` (R134.5), puis `sleep 700 &` et `wine winecfg &` depuis la console, puis `close-console` (167 A) puis `cgroup.kill` de la console ; (13) seconde console b2 : `bomb.sh` (bombe de processus), `ping` et `list-processes` toutes les 2 s pendant 12 s, b1 et ses fenêtres observés, puis `cgroup.kill` de cette console ; (14) partage disparu : `faultOn("srv/share-b1")`, `mounts`, lecture d'un fichier déjà lu, d'un fichier jamais lu, `ls`, puis `faultOff` et relecture ; (15) disque : `find` par l'agent, `wine cmd /c echo … > Z:\home\wine-b1\disk\drive_c\from-wine.txt`, `cp` par `exec-in-prefix`, export du disque **sans `sync`**, puis après `sync`, puis après `umount` ; lecture des trois images par `ext4-ls.mjs` et comparaison avec la liste de l'agent ; (16) `e7-open-url` depuis b1 ; (17) **critère** : `kill` de Notepad (pid réel, SIGTERM), temps jusqu'à la disparition du processus et de la fenêtre, B et C encore là, `ping` ; (18) `cgroup.kill` de toute la bouteille b1 ; (19) mémoire (page, processus Chrome, invité, agent) et aller-retour de toutes les opérations. Trois runs (`results-1..3.json`). Une session interactive (`drive.mjs`) a précédé pour la mise au point (mêmes chiffres, cités quand les runs ne les ont pas).

`run-alpine.mjs` : l'image Alpine 3.24 de E6 (autologin root sur `ttyS0`, aucun agent, pas de `virtio_console` dans la page) : boot, 10 allers-retours d'une commande série, `ps`, une commande longue pendant qu'une autre attend, capture.

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (v86 n'utilise qu'un cœur), 64 Go. Pendant les runs, un autre essai (E14, DOS) tournait en parallèle sur la même machine, avec ses propres VM v86 et ses constructions Docker : les temps absolus sont pessimistes.
- Chrome 156.0.8078.80 (canal installé, Playwright, headless, profil temporaire supprimé) ; v86 0.5.473 (`lib/` de E17) ; serveur sur `localhost` ; Docker Desktop (WSL 2), Python 3.14, Node 24.
- Cache : contexte Chrome neuf à chaque run ; `.zst` servis avec `max-age=3600` ; le disque ext4 est rechargé entièrement (256 Mio) à chaque page.
- Disque C: : 200 Go libres au départ, 171 Go au plus bas (tar d'export de 3,6 Gio, supprimé).

## Résultats

Temps en secondes ou millisecondes comme indiqué. Trois runs (`results-1..3.json`) ; quand ils concordent, une seule valeur, sinon la fourchette. « RTT » = aller-retour mesuré dans la page entre l'envoi de la ligne JSON et sa réponse (comprend le travail de l'agent).

### L'agent : protocole, taille, délais

| Mesure | Run 1 | Run 2 | Run 3 | Note |
|---|---|---|---|---|
| Boot à froid, page → prompt série | 47,5 s | 52,0 s | 46,8 s | uptime invité 35-40 s à `boot-ready` (E17 : 49-50 s) ; octets servis 290,8 Mo dont 268 Mo de disque ext4 |
| Événement `ready` de l'agent (page → `hvc1`) | 47,1 s | 51,6 s | 46,5 s | l'agent parle **avant** le shell série |
| Binaire de l'agent / RSS | 108 Kio / 160-184 Kio | idem | idem | statique musl i386 ; `VmSize` 444 Kio |
| `ping` RTT (10 fois) | 2,6-4,6 ms, méd. 3,6 | 2,4-4,3, méd. 3,6 | 2,0-4,6, méd. 3,2 | **série de E6/Alpine : ≈ 190 ms** par commande |
| `list-processes` à vide (55 processus) | méd. 409 ms | 475 ms | 384 ms | lecture de 4 fichiers `/proc` par processus |
| `list-processes` avec A, B, C (78 processus) | 500 / 622 ms | 585 / 664 ms | 640 / 732 ms | médiane / max ; ≈ 7 ms par processus |
| `list-windows` (3 fenêtres clientes, 14 fenêtres dans l'arbre twm) | méd. 4,3 s | 3,5 s | 4,9 s | `xwininfo -tree` + un `xprop` par fenêtre nommée ; 0,8-1,7 s avec une seule fenêtre ; max 8-11 s pendant qu'un programme se lance |
| `mounts` (5 montages) | 42-63 ms | 43-59 ms | 52-70 ms | un enfant par montage, `statfs` + `readdir` |
| `launch` RTT | 10-24 ms | | | `fork` + cgroup + `setuid` + `exec` : 1,5-4 ms dans l'agent |
| `exec-in-prefix` (`cat`, `ls`, `test`) | méd. 133 ms | 99 ms | 106 ms | `wine cmd /c` : 5,7-7,8 s |
| `kill` RTT | 10-20 ms (max 217 ms pour un `cgroup.kill`) | | | |
| `open-console` → premier prompt du shell sur `hvc2` | 154 ms | 154 ms | 170 ms | `openpty` + `setuid` + `sh -i` |

### Critère : arrêter A sans toucher B ni la VM

A = `wine notepad` dans b1, B = `wine winemine` dans b2, C = `xterm` dans b2 (tous lancés par `launch`, jamais par un shell).

| Mesure | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| A : `launch` → fenêtre « Untitled - Notepad » (premier Wine de b1 sous X) | 71,3 s | 73,8 s | 90,5 s |
| B : `launch` → « WineMine » (premier Wine de b2) | 79,4 s | 79,3 s | 83,4 s |
| C : `launch` → « xterm-b2 » | 8,6 s | 8,2 s | 11,5 s |
| Processus de b1 / de b2 avec A, B, C | 10 / 12 | 10 / 12 | 10 / 12 |
| `kill` {pid de notepad.exe, SIGTERM} : RTT | 19,1 ms | 18,0 ms | 19,6 ms |
| notepad.exe absent de `list-processes` | 1,2 s | 0,9 s | 1,4 s |
| Fenêtre Notepad absente de `list-windows` | 3,6 s | 2,6 s | 3,4 s |
| Événement `exit` reçu pour notepad.exe (signal 15) | oui | oui | oui |
| B : processus winemine.exe toujours là | **oui** | **oui** | **oui** |
| B et C : fenêtres toujours listées juste après | non (liste vide, voir « ce qui a raté ») | **oui** | non (liste vide) |
| B et C : fenêtres listées 4 s plus tard (`kill_bottle_b1`) | **oui, les deux** | oui | oui |
| VM : `ping` après le kill | 18 ms | 108 ms | 18 ms |
| Processus restants de b1 après le kill de A | **0** | 0 | 0 |

**Verdict : critère atteint aux trois runs.** Deux faits à retenir : (1) le pid rendu par `launch` est celui de `start.exe` (Wine 11 lance les programmes par `start.exe /exec`), pas celui de `notepad.exe` ; le pid du programme se retrouve par `_NET_WM_PID` de sa fenêtre (`list-windows`) ou dans `list-processes` par son nom ; tuer `start.exe` ne tuerait pas Notepad. (2) Dans la minute qui suit la mort de Notepad, **tout le reste de la bouteille b1 s'arrête tout seul** (`start.exe` sort avec le code 1, puis `explorer.exe`, puis `wineserver` et ses services 6 s plus tard : 10 événements `exit`), parce que Notepad était le dernier client du `wineserver` ; b2 n'est pas touché.

### Arbres : que tue `kill pid`, `kill group`, `cgroup.kill` ?

Arbre de test lancé en b1 : `sh tree.sh` (parent) + deux `sleep` dans son groupe + un `sleep` lancé par `setsid` (nouveau groupe et nouvelle session, comme un démon). Trois runs identiques.

| Méthode | Survivants | Durée de l'arrêt |
|---|---|---|
| `kill` pid du parent (SIGKILL) | **3 sur 3** `sleep` (réadoptés par l'agent, subreaper) | 2-4 ms |
| `kill` groupe du parent (`kill(-pgid)`) | **1** : le `sleep` lancé par `setsid` | 3-4 ms |
| `cgroup.kill` du lancement (`/sys/fs/cgroup/e7/b1/<seq>`) | **0** (4 processus tués) | 19-42 ms, atomique |
| `kill scope=prefix` (balayage de `/proc` par uid, en passes de 10 ms) | 0 (session interactive) | dépend du nombre de passes ; course possible contre une bombe |

Même chose sur Wine : `cgroup.kill` de toute la bouteille b1 (10 processus : wineserver, services, winedevice, explorer…) prend **1,2-1,4 ms** d'écriture et b2 (12 processus, deux fenêtres) n'y voit rien, aux trois runs. Un cgroup v2 par lancement est donc **l'unité d'arrêt** ; le pgid ne suffit pas (démon `setsid`), le pid encore moins.

### Console d'usager (hvc2) : garanties R134.5

Shell `sh -i` de `wine-b2` sur un pty attaché à `hvc2`, son propre cgroup (`/e7/b2/7`), `RLIMIT_NPROC` 256. `verify-console.sh` : 22 contrôles en 18-24 s, **22 HELD, 0 LEAK aux trois runs** ; l'agent répond (`ping` 14 ms) après.

| Contrôle (depuis la console de b2) | Résultat observé | Garantie (134) |
|---|---|---|
| `unshare -n`, `unshare -U` | refusés (`Operation not permitted` ; `No space left on device` = `user.max_user_namespaces=0`) | 1 |
| `nsenter -t 1 -m` | refusé | 1 |
| lire le home, le préfixe (`user.reg`), le dossier `wineserver` (`/tmp/.wine-1001`) de b1 | `Permission denied` (0700) | 2 |
| lire `ttyS0` (shell root de l'essai), lire `hvc1` (port de l'agent), **écrire sur `hvc1`** (`{"id":99,"op":"ping"}`), lire `hvc3` | `Permission denied` (`crw------- root`) ; côté page, aucune réponse d'id 99 n'est jamais arrivée | 2 (canal agent réservé à root) |
| écrire directement sur `hvc2` (le port de sa propre console) | `Permission denied` : la console ne voit que son pty, pas le port | 2 |
| socket brut (`e7-rawsock`) | `Operation not permitted` (pas de CAP_NET_RAW) | 1/2 |
| binaire setuid dans l'image | aucun | 2 |
| `/home` et `/tmp` montés `nosuid,nodev` | confirmé (`/home` = bind du 9p remonté `nosuid`) | 2 |
| poser un bit setuid sur une copie de `true` dans son home ; `chown root` | bit posé mais sans effet (nosuid, propriétaire non root) ; `chown` refusé | 2 |
| `PTRACE_ATTACH` sur un processus de b1 ; lire `/proc/<pid>/environ` de b1 | `Operation not permitted` ; `Permission denied` | 2 |
| `kill -0`, `kill -9` d'un processus de b1, de PID 1, de l'agent | `Operation not permitted` | 2 |
| écrire `cgroup.kill` de b1 | `Permission denied` | 2 |
| lire `/dev/fb0` (INFO) | **lisible** lors de la session de mise au point (`init.sh` ne le protégeait pas) → `chmod 600` ajouté à `x-up.sh` et `init.sh` ; affichage partagé = hors garanties 1-2 | — |
| affichage X `:0` (INFO) | accessible à `wine-b1` et `wine-b2` par `xhost +si:localuser` ; les deux bouteilles partagent un serveur X (fenêtres, presse-papiers, clavier : limites déjà notées en E6) | — |

Deux fuites trouvées **hors** du script, par l'inventaire des dossiers où un usager de bouteille peut écrire (`find / -xdev -type d -perm -o+w`) : la **racine `/` du 9p est 0777** (mode du répertoire racine dans le tar de `docker export`) et **`/run` est 1777** (mode par défaut de tmpfs) : b2 y a créé des fichiers lisibles par b1 (session interactive). `chmod 755 /` et `mode=0755` corrigent ; ajoutés à `init.sh` après les runs. `/tmp`, `/var/tmp`, `/dev/shm` restent 1777 avec bit collant (normal).

### Console : fermeture, programme graphique et démon (147, 167 A)

Depuis la console de b2 : `sleep 700 &` (démon) puis `wine winecfg &` (fenêtre visible en 25-36 s), puis `close-console` = `SIGHUP` au groupe du shell.

| Après `close-console` (SIGHUP) | Après `cgroup.kill` de la console |
|---|---|
| shell terminé (événements `exit`, `console-closed`) ; **le `sleep` survit, winecfg et sa fenêtre survivent** (dash n'envoie pas SIGHUP à ses jobs, et chaque job a son propre pgid) ; b2 passe de 16 à 15 processus | 3 processus tués en 170-200 ms : démon et winecfg disparus ; **B et C (lancés par `launch`, autres cgroups de b2) intacts** aux trois runs |

Donc : « fermer la console termine ce qui a été lancé dedans » n'est vrai qu'avec le cgroup ; et si 167 A veut qu'un programme graphique survive à sa console, l'agent doit le déplacer dans un cgroup frère avant la fermeture (mécanisme disponible : écrire le pid dans un autre `cgroup.procs`).

### Bombe de processus (garantie 8)

`bomb.sh` (`b(){ b|b & }; b`) dans une seconde console de b2, `RLIMIT_NPROC` 256 pour l'uid 1002 (B et C comptent dedans).

| Mesure | Runs 1-3 | Session interactive (bombe tenue plus longtemps) |
|---|---|---|
| Processus total pendant la bombe | 285-286 (dont 15-25 de b2 au moment de la lecture) | 323 (256 de b2) pendant > 15 s |
| `ping` pendant | 31 / 447 / 288 ms puis **3,4-3,9 s** une fois, puis 14-28 ms | 9, 247, 33, 136 ms puis 2,0 s |
| `list-processes` pendant | 3,4-3,9 s (285 processus) puis 0,5-0,6 s | 3,5 s (323 processus) |
| b1 (10 processus, Notepad) et fenêtres B, C | intacts | intacts |
| Extinction | dans les runs, la bombe **s'éteint seule en < 2 s** (dash sort quand `fork` échoue à la limite) : il ne reste que le shell ; en session interactive elle a tenu > 15 s à 256 processus | |
| `cgroup.kill` de la console | 5-6 ms (1 processus) | **7,3 ms pour 256 processus**, b2 revenu à ses 12 processus, `ping` 14 ms après |

La VM reste utilisable : l'agent (root, hors des limites de l'uid) répond entre 10 ms et 4 s pendant la saturation, et le nettoyage est atomique. `RLIMIT_NPROC` par usager est un confort efficace ; Kazo n'a rien à faire (c'est la VM de l'usager, 102).

### Partage disparu (E8) : détection et erreur rendue

`/srv/share-b1` (3 fichiers, 1 sous-dossier) lié dans `/home/wine-b1/share` ; `hello.txt` lu une fois **avant** la panne (donc en cache de pages, `cache=loose`) ; `faultOn` fait répondre `Rlerror EIO` à toute requête visant les 5 inodes.

| Mesure | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `mounts` pendant la panne : état du partage | `error`, `statfs` errno 5, `readdir` errno 5, en 5,2 ms | `error`, 5, 3,4 ms | `error`, 5, 5,5 ms |
| Délai hôte, `faultOn` → réponse `mounts` | 63 ms | 47 ms | 56 ms |
| `cat hello.txt` (déjà lu, en cache) | **`Input/output error`**, rc 1, 80 ms | rc 1, 53 ms | rc 1, 59 ms |
| `cat never-read.txt` | `Input/output error`, 71 ms | 47 ms | 52 ms |
| `ls -la share/` ; `ls share/sub` | `Input/output error` ×2, rc 2 (pas de liste vide) | idem | idem |
| `Rlerror EIO` envoyés par la page | 7 | 7 | 7 |
| Après `faultOff` : `cat never-read.txt` ; `mounts` | rc 0, contenu correct ; `ok`, 5 entrées | idem | idem |

Le noyau 6.18 avec `cache=loose` ne sert **pas** le fichier en cache quand le serveur répond EIO (il revalide : `Tgetattr`/`Tlopen` échouent avant la lecture) : l'erreur est claire, immédiate (≈ 50-80 ms, pas les 2,6 s de réessai de E8 car ici c'est `open` qui échoue, pas `read`), et le montage revient sans remontage. Les autres montages (racine, disque, `/tmp`) restent `ok` pendant la panne.

### Disque interne : agent en marche contre Node à l'arrêt

Disque ext4 de 256 Mio (`hda` en mémoire) monté sur `/home/wine-b1/disk` ; écritures : `wine cmd /c echo … > Z:\…\from-wine.txt` (23 octets, 5,7-7,8 s) et un `cp` par `exec-in-prefix` (100 ms).

| Mesure | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Liste par l'agent (`find -printf`, 16 entrées) | 227 ms | 180 ms | 200 ms |
| Export du disque (256 Mio, page → serveur, 8 PUT) | 16,4 s | 15,0 s | 15,3 s |
| Lecture par `ext4-ls.mjs` (14-16 entrées, racine → feuilles) | 2-24 ms | 2-5 ms | 2-5 ms |
| Image exportée **sans `sync`** cohérente avec la liste de l'agent ? | **non** : `from-wine.txt` et `copy-by-exec.txt` absents | non | non |
| après `sync` (0,28 s) | **oui** (16 entrées, uid 1001 sur les nouveaux fichiers) | oui | oui |
| après `umount` (0,9-1,0 s) | oui | oui | oui |

Lecture : côté hôte, lire l'ext4 est négligeable (quelques ms, mais le lecteur est minimal : pas de journal, pas de htree) ; ce qui coûte, c'est de sortir l'image de la page (15 s ici, par HTTP ; dans Kazo le disque sera dans l'OPFS, donc sans copie). Pendant que la VM écrit, l'image est **incohérente** jusqu'au `sync` (données dans le cache de pages de l'invité) et le journal ext4 n'est pas rejoué par le lecteur : « lecture à l'arrêt » veut dire après `umount` (ou au moins `sync`), jamais sur une VM en marche. En marche, l'agent est la seule source cohérente (`find` 200 ms).

### Écritures hors compte (102)

Mesure dédiée (`run-writes.mjs`, 2 runs, `results-writes.json`), VM neuve à chaque run : inventaire des dossiers où `wine-b1` peut écrire, puis `inotifywait -m -r` sur ceux-là (hors home de b1) et instantané `find -newer` avant/après le premier `wine notepad` de b1 sous X (69-80 s jusqu'à la fenêtre : création des clés d'affichage, wineserver, explorer, fontconfig).

| Où b1 peut écrire (hors son home) | Ce que Wine y a écrit | b2 peut-il le lire ? |
|---|---|---|
| `/tmp` (1777) | `/tmp/.wine-1001/` (0700) avec `server-16-30b/socket` et `lock` (le `wineserver`) | **non** (`Permission denied`) |
| `/tmp/.X11-unix/X0` (socket d'Xorg, 0777) | rien d'écrit, mais utilisé : affichage partagé | b2 s'y connecte aussi (même serveur X) |
| `/var/tmp`, `/dev/shm`, `/srv/share-b1`, `/run` (1777 avant correction), `/` (0777 avant correction) | **rien** | — |
| `/usr/share`, `/usr/lib`, `/opt/wine-stable`, `/etc`, `/var` (root 755) | impossible (droits) | — |

Dans son home : `.wine/system.reg`, `user.reg`, `userdef.reg`, `.wine/dosdevices/com1..4`, `.cache/fontconfig/*` (4 caches), `e7-launch-1.log`. Aucune écriture dans `/home/wine-b2`. Conclusion : avec un usager par bouteille, un programme Wine n'écrit **que** dans son home et dans `/tmp/.wine-<uid>` (0700) ; ce que b2 peut atteindre d'autre est le serveur X partagé, pas des fichiers. Le journal `inotifywait` (41 Kio) n'a pas été exploité : son format (`chemin événement nom`) ne correspondait pas à mon filtre ; l'instantané `find -newer` sur les mêmes dossiers donne la liste ci-dessus, identique aux deux runs.

### `open-url` (139)

`e7-open-url https://example.test/doc?x=1` lancé en b1 par `exec-in-prefix` : 23-33 ms, et la page reçoit `{"event":"open-url","uid":1001,"pid":…,"prefix":"b1","url":"https://example.test/doc?x=1"}` aux trois runs. L'uid et donc la bouteille viennent de `SO_PEERCRED`, pas du message : un programme ne peut pas se faire passer pour une autre bouteille. L'agent ne fait rien d'autre.

### Mémoire

| Mesure | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Renderer Chrome, mémoire privée, fin de run (A tué, B et C ouverts, disque en mémoire, image en cache) | 2 853 Mio | 2 809 Mio | 2 630 Mio |
| Tous processus Chrome | 3 219 Mio | 3 185 Mio | 2 990 Mio |
| Invité (`free`, 880 Mio visibles), utilisé | 114 Mio | 114 Mio | 117 Mio |
| Agent, RSS | 184 Kio | 164 Kio | 168 Kio |

E17 mesurait 2 225-2 260 Mio de renderer avec trois fenêtres Wine ; les ≈ 500 Mio de plus sont le disque de 256 Mio chargé en `ArrayBuffer` plus sa copie à l'export, pas l'agent.

### Tableau « image apportée sans agent » (195 A) — Alpine nue de E6, console série seule

Un run (`results-alpine.json`), même page, `?alpine=1`, 512 Mio, aucun agent, `virtio_console` absent.

| Mesure | Valeur |
|---|---|
| Boot à froid, page → prompt root sur `ttyS0` | 67,1 s (uptime invité 54,9 s à `boot-ready` ; E6 : 58-68 s) |
| `/dev/hvc*`, `/dev/vport*` dans l'invité | absents (pas de périphérique virtio-console déclaré par la page) |
| Aller-retour d'une commande série (`true` + marque de fin, 10 fois) | 148 à 331 ms, médiane ≈ 190 ms (contre 3,6 ms par l'agent) |
| `ps -o pid,ppid,user,comm` par la console | 1,4 s, texte à analyser côté hôte, pas de bouteille, pas de cgroup |
| Une seule session : `sleep 8` puis une seconde commande | la seconde attend la fin de la première (8,1 s), aucune autre voie |

Ce qui reste sans agent : l'écran VGA (canvas), le clavier et la souris PS/2, le disque (racine 9p, ou un disque bloc), le réseau de la page (E6), et **une** console série root quand l'image en démarre une (Alpine : `agetty` sur `ttyS0` ; une image apportée quelconque n'en a pas forcément). Ce qui manque : liste de processus structurée et par bouteille, arrêt individuel, lancement comme un usager donné, fenêtres avec leur pid, état des partages, événements (`exit`, `open-url`), et toute commande non interactive à durée bornée (la session série est bloquée par toute commande longue, et c'est root). FreeDOS et un vrai Windows n'ont même pas la console série (non mesuré ici ; E14 couvre DOS).

### Proposition : un programme invité dans le modèle de processus de Kazo (à valider, rien n'est codé)

Faits lus (`verification-consignes-2026-11-10.md`) : Kazo n'a pas de processus « étranger » ; le modèle le plus proche est la tâche `KazoBackground` (ADR 0128, 0140) : processus enfants tuables un par un dans un bassin de workers, un worker coûtant 55 à 66 Mio ; l'ADR 0125 pose des limites par processus (CPU, RSS, nombre) avec SIGKILL dans un worker ; `StartChild` ne vérifie aucune permission (#431) et ne doit pas servir de base. Mesuré ici : une VM v86 = une page/worker de 1,4 à 2,3 Gio ; dedans, un lancement = un cgroup dont l'arrêt est atomique (7-35 ms), la liste coûte 0,4 s, un `ping` 4 ms.

Proposition :
1. **La VM est un processus Kazo** (un par système invité, rattaché au worker qui héberge v86) : c'est à lui que s'appliquent les limites de l'ADR 0125 (RSS = `memory_size` + surcoût du renderer mesuré en E1/E17, CPU = un cœur), **en entier et une fois** ; le SIGKILL de l'ADR 0125 sur ce processus arrête la VM et donc tous ses programmes (c'est le comportement attendu : l'invité ne peut pas survivre à sa VM).
2. **Chaque `launch` de l'agent est un processus Kazo enfant** de la VM (un par programme lancé, pas par processus invité : `wine notepad` fait 10 processus Linux dans un cgroup, c'est le cgroup qui est l'unité), avec comme identité la bouteille, comme pid Kazo l'identifiant du cgroup (`<prefix>/<seq>`), comme état celui du cgroup (`cgroup.events` populated), et comme opérations : arrêter = `kill scope=cgroup` (SIGTERM puis SIGKILL), lister les fenêtres = `list-windows` filtré par cgroup. Il n'a **pas** de limites propres de l'ADR 0125 (ni CPU ni RSS par programme : la VM n'expose pas ces comptes de façon fiable et le cgroup v2 sans contrôleurs ne les compte pas) ; seule la limite « nombre de processus » existe dans l'invité (`RLIMIT_NPROC` 256 par usager) et elle est un confort, pas une garantie Kazo (garantie 8 de 134).
3. **Les processus enfants créés par un programme invité** (ligne 4 de 134) n'apparaissent pas comme processus Kazo : ils sont dans le cgroup de leur lancement et meurent avec lui ; l'agent les voit (`list-processes`) et les signale (`exit`) si Kazo veut un détail « avancé ».
4. **Une console** (145, ligne 3) est aussi un processus Kazo enfant de la VM (son cgroup), et ce qui est lancé depuis la console reste dans ce cgroup : « fermer la console » = `cgroup.kill` de la console, ce qui tranche 167 A de façon mesurable (voir le résultat `console_close` : `SIGHUP` au shell ne termine ni un démon ni un programme graphique ; seul le cgroup le fait ; si l'on veut que le programme graphique survive, il faut le déplacer dans un cgroup frère avant, ce que l'agent peut faire).
5. **Ce qui ne passe pas par `StartChild`** : rien ici ne crée de processus Kazo via `StartChild` ; c'est l'hôte Os-x86 qui enregistre le processus Kazo à la réponse de `launch`, et qui le retire à l'événement `exit` du lancement (ou quand `cgroup.events` dit `populated 0`).
6. **Sans agent** (image apportée) : la VM reste un processus Kazo (point 1) sans enfants ; c'est le « modèle "processus étranger" simplifié (VM = un processus Kazo) » que le plan prévoit en cas d'échec de E7 ; il est aussi, de fait, le modèle des images sans agent.

## Ce qui a raté ou a été contourné

- **`inotifywait -r` sur la racine 9p** : la mise en place récursive des surveillances sur `/usr/lib`, `/usr/share`, `/opt` (milliers de dossiers sur 9p) n'a pas abouti en 10 min ; en plus `-q` supprime la ligne « Watches established » que mon script attendait. Les trois runs ont donc attendu 10 min chacun et n'ont rien capturé dans leur étape « écritures hors compte » (vide dans `results-1..3.json`). Contourné par une mesure dédiée (`run-writes.mjs`) qui ne surveille que les dossiers où l'usager de bouteille **peut** écrire (inventaire `find -perm -o+w`), prête en 33-36 s ; le script de la série ne pouvait plus être corrigé sans l'arrêter (interdit par la politique de la session) : les runs 2 et 3 ont payé le même délai.
- **`-q` et le format d'`inotifywait`** : le journal d'`inotifywait` (41 Kio) écrit `chemin événement nom`, pas `événement chemin` ; mon filtre n'a rien retenu. L'instantané `find -newer` fait le même travail ; le journal n'est pas exploité.
- **`list-windows` rend une liste vide pendant 1-3 s** quand un client X se termine (runs 1 et 3 juste après le kill de Notepad, et à 0,8 s et 6,8 s après le kill en session interactive, avec code de retour 0 du script) : `xwininfo -root -tree` s'interrompt sur une fenêtre détruite entre l'énumération et l'interrogation (erreur `BadWindow`). Une liste vide n'est donc pas une preuve d'absence ; l'hôte doit recouper avec `list-processes` (toujours juste) ou l'agent doit tenir sa propre connexion X et suivre les événements `MapNotify`/`DestroyNotify` (comme l'agent XComposite de E4) au lieu d'appeler des outils.
- **`list-windows` à 3-5 s** pour trois fenêtres : un `xprop` par fenêtre nommée dans l'arbre de twm, chaque processus coûtant ≈ 100-200 ms dans v86. Première version (un `xprop` + un `xwininfo` par fenêtre de l'arbre, cadres compris) : 6,4 s. Même remède que ci-dessus.
- **Le pid de `launch` n'est pas celui du programme** (`start.exe` de Wine 11) : vu à la première liste de processus ; `run.mjs` retrouve le pid de Notepad par `_NET_WM_PID`. Le premier script de contrôle a visé `start.exe` au lieu de `notepad.exe` (même uid 1001 : mêmes résultats) ; corrigé.
- **`/dev/sda` absent** : `sd_mod` n'est pas chargé par mon init minimal (E8 l'avait par `mdev`/`hwdrivers` d'Alpine) : ajouté (`modprobe sd_mod`) ; `ata_piix` est intégré.
- **`/dev/fb0` lisible par un usager de bouteille**, **`/` en 0777** (tar de `docker export`) et **`/run` en 1777** (défaut tmpfs) : trois trous de durcissement de l'image, trouvés par les contrôles, corrigés dans `init.sh`/`x-up.sh` (les runs ont tourné avec `fb0` corrigé par `x-up.sh` injecté, `/` et `/run` non corrigés : les contrôles R134.5 ne les testaient pas, l'inventaire `-perm -o+w` oui).
- **Fichiers corrigés après la construction de l'image** (`list-windows.sh`, `verify-console.sh`, `bomb.sh`, `x-up.sh`, `e7-rawsock`) : injectés par 9p au boot (`create_file` avant la première lecture, comme E8), `chmod` par la console série ; l'image Docker `e7-agent` n'a pas été reconstruite (16 min, dont 14 de hachage du tar de 3,6 Gio). Le `Dockerfile` et `guest/` du dépôt contiennent la version corrigée.
- **Le piège E17 (« un fichier déjà lu n'est pas remplacé par `create_file` ») a un contournement** : `sync; echo 3 > /proc/sys/vm/drop_caches` dans l'invité, puis le nouveau contenu est visible (vérifié sur `list-windows.sh` déjà exécuté). Le fichier injecté perd ses bits d'exécution (`create_file` crée en 0644).
- **`ulimit -u` n'existe pas dans dash** : la limite est lue dans `/proc/self/limits` (256, posée par l'agent).
- **`cgroup.kill` sur le cgroup d'une bouteille** rend `count 0` dans la réponse (l'agent ne compte que les membres directs, les processus sont dans les sous-cgroups) ; les processus sont bien tués (vérifié par `list-processes`). Détail de prototype, non corrigé (le binaire mesuré est celui du dépôt).
- **Export du disque : 15-16 s** pour 256 Mio (huit `PUT` de 32 Mio depuis un `ArrayBuffer` tranché) : coût de l'essai, pas de la lecture ext4.
- Un autre essai (E14) tournait en parallèle sur la même machine ; la mesure « écritures hors compte » a tourné pendant les runs 2-3 (deuxième VM sur un autre port).

## Limites

- Une machine, Chrome headless, `localhost`, trois runs ; v86 dans le fil principal (pas de Web Worker) ; pas de sauvegarde d'état avec l'agent.
- Deux bouteilles seulement, un seul serveur X partagé par `xhost` : l'isolation d'affichage, de presse-papiers et de clavier n'est pas traitée (comme E6) ; `fb0` et `input/*` sont réservés à root, mais tout client X voit toutes les fenêtres.
- Les programmes sont Notepad, WineMine, winecfg, xterm et des `sleep` ; pas d'installateur, pas de programme qui crée un service Windows ou un processus détaché par ses propres moyens (hors `setsid`).
- L'agent est un prototype : exécution bloquante (`exec-in-prefix` bloque la boucle pendant la commande, `ping` attend), une seule console à la fois, pas de limite de taille d'entrée autre que 64 Kio par ligne, pas de chiffrement ni d'authentification (le port est 0600 root dans l'invité ; côté page, seul le code de la page y accède), pas de reprise si l'agent meurt (il faudrait le relancer depuis init).
- Le lecteur ext4 ne rejoue pas le journal et ne gère ni htree ni fichiers > 4 Gio ; le disque est en mémoire (`ArrayBuffer`), pas dans l'OPFS ; « à l'arrêt » = image exportée après `umount`.
- La « disparition » du partage est simulée dans la page (EIO sur un sous-arbre d'inodes du serveur 9p de v86), pas par un vrai serveur 9p d'Os-x86 qui perd son lecteur ; E8 couvre les autres modes (`throw`, `hang`).
- Le tableau « sans agent » ne couvre qu'Alpine (console série présente) ; FreeDOS et Windows ne sont pas mesurés ici.
- Le modèle de processus Kazo est une proposition déduite de faits lus, pas d'un prototype dans Kazo.

## Chiffres bruts

`essais/e7-agent-processus/results-1.json`, `results-2.json`, `results-3.json` (toutes les étapes : réponses de l'agent, listes de processus et de fenêtres, contrôles R134.5, événements, listes de fichiers invité/hôte, mémoire, aller-retours par opération), `results-writes.json` (écritures hors compte, 2 runs), `results-alpine.json` (Alpine nue) ; `summarize.py 1 2 3` imprime le tableau comparatif. Captures `images/e7-run<n>-abc.png` (Notepad b1, WineMine et xterm b2), `images/e7-run<n>.png` (après le kill de A), `images/e7-alpine-run1.png`. Journaux `images/run.log`, `run-writes.log`, `run-alpine.log`, `drive.log`, `drive2.log`, `docker-build.log`, `build.out`. Images exportées `images/disk-dirty.img`, `disk-synced.img`, `disk-unmounted.img` (256 Mio chacune, ignorées par git). Docker : image `e7-agent` (5,1 Go affichés, dont 3,05 Go partagés avec `e17-wine-stable`) conservée ; tar d'export (3,6 Gio) supprimé ; aucun autre conteneur ni image touché. Disque C: : 200 → 147 Go libres pendant l'essai (E14 en parallèle), jamais sous 5 Go.

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **Critère atteint : A arrêté sans toucher B ni la VM**, trois fois sur trois (`kill` 18-20 ms d'aller-retour, processus disparu en 0,9-1,4 s, fenêtre en 2,6-3,6 s, événement `exit` reçu, B et C vivants, `ping` 18 ms). **Garanties console tenues** : 22 contrôles R134.5 sur 22, trois fois (espaces de noms, autre bouteille, ports de l'agent et de la console, setuid, `nosuid`, `ptrace`, `kill`, `cgroup.kill`, socket brut), l'agent répond pendant une bombe de processus et la nettoie en 7 ms. **Écritures hors compte connues** : seulement `/tmp/.wine-<uid>` en 0700 (plus le serveur X partagé). Conséquence du plan en cas d'échec (« modèle simplifié, VM = un processus Kazo ») **non déclenchée** ; le disque interne (V2 selon le plan) a tout de même été mesuré. À valider : E7 vert pour J0.
2. **Protocole de l'agent (§1.1, D61.3)** : JSON par ligne sur un port virtio-console (`hvc1`, 0600 root), réponses appariées par `id`, événements (`ready`, `exit`, `open-url`, `console-closed`) ; opérations `ping`, `list-processes`, `launch`, `kill` (pid | group | prefix | cgroup), `list-windows`, `mounts`, `exec-in-prefix`, `open-console`/`close-console`. Coût : 108 Kio, 170 Kio de RSS, 3,6 ms d'aller-retour, boot inchangé. À valider comme base du protocole, avec trois corrections : **une opération ne doit pas bloquer les autres** (boucle non bloquante ou un enfant par `exec-in-prefix`), **les fenêtres par événements X** (connexion Xlib permanente, `MapNotify`/`DestroyNotify`, `_NET_WM_PID`) plutôt que par `xwininfo`/`xprop` (3-5 s et listes vides transitoires), et **plusieurs consoles** (un port par console, ou multiplexage sur un seul port).
3. **Unité de lancement et d'arrêt = un cgroup v2 par `launch`** (`/sys/fs/cgroup/e7/<prefix>/<seq>`), pas le pid (3 orphelins sur 3 survivent), pas le pgid (un `setsid` survit), pas l'uid seul (balayage, course) : `cgroup.kill` est atomique (7-42 ms, 256 processus en 7 ms) et le cgroup de la bouteille tue tout (1,2 ms). Le pid rendu par `launch` est `start.exe` sous Wine 11 ; le programme se retrouve par la fenêtre ou par le cgroup. À valider : écrire ça dans l'ADR 17 (processus invités) : « un lancement = un cgroup ; arrêter = `cgroup.kill` ; la liste et les fenêtres sont filtrées par cgroup ».
4. **Console (145, 147, 167 A)** : un pty attaché à un second port marche (prompt en 154 ms) ; `SIGHUP` au shell ne termine ni un démon ni un programme graphique lancés depuis la console ; **le cgroup de la console** les termine tous (170-200 ms) sans toucher aux lancements normaux de la même bouteille. Pour 167 A (le programme graphique continue, le démon est terminé avec le shell), l'agent doit déplacer les processus à fenêtre dans un cgroup frère avant `cgroup.kill` ; mécanisme présent, non mesuré. À valider.
5. **Garanties 1, 2 et 8 de 134** tiennent avec la recette : usager par bouteille, home 0700 **sur 9p** remonté `nosuid,nodev` (E6 les avait en tmpfs : les deux marchent), `user.max_user_namespaces=0`, aucun setuid, ports 0600 root, `RLIMIT_NPROC`. Trois trous d'image trouvés et à mettre dans la recette : `chmod 755 /` (la racine d'un `docker export` est 0777), `/run` en `mode=0755`, `/dev/fb0` et `/dev/input/*` en 0600. Ce qui reste partagé entre bouteilles d'une même VM : le serveur X (fenêtres, presse-papiers, frappe : limite connue de E6, à traiter par l'affichage fenêtre par fenêtre de E4 ou par un X par bouteille, V2). À valider.
6. **102 (environnements partagés)** : un programme Wine n'écrit que dans son home/préfixe et `/tmp/.wine-<uid>` (0700) ; rien dans l'autre bouteille, rien dans le système. « Utilisés à tour de rôle » n'a donc pas de fuite de fichiers entre usagers si chaque usager Kazo a son usager invité ; la seule chose que l'usager suivant voit est l'état du préfixe (sien) et `/tmp` (sticky). À valider : lever la réserve de 102 pour les fichiers ; l'affichage reste la réserve.
7. **Partage disparu (E8, montage disparu J0)** : si le serveur 9p répond `Rlerror EIO`, l'agent le voit en 3-6 ms (`statfs`), le signale en < 65 ms, et les programmes reçoivent `Input/output error` immédiatement, **y compris pour un fichier déjà en cache** (`cache=loose` revalide à l'ouverture) ; aucun dossier vide, aucun blocage, retour sans remontage. À valider : le serveur 9p d'Os-x86 répond EIO (E8) et l'hôte interroge `mounts` à la demande ou sur événement ; pas besoin de démonter.
8. **Disque interne** : à l'arrêt, un lecteur ext4 minimal lit `drive_c` en quelques ms depuis Node, mais seulement sur une image **synchronisée ou démontée** (sans `sync`, deux fichiers manquaient aux trois runs) ; en marche, seul l'agent est cohérent (`find` 200 ms). À valider : « disque interne en V2 » tel que prévu, avec la règle « lecture à l'arrêt = après arrêt propre de la VM (umount) ; VM en marche = par l'agent » ; le coût de sortie de l'image (15 s ici) disparaît avec un disque dans l'OPFS.
9. **Sans agent (195 A)** : il reste l'écran, le clavier, la souris, le disque, le réseau de la page et, si l'image en démarre une, **une** console série root (190 ms par commande, bloquée par toute commande longue) ; pas de liste ni d'arrêt par programme, pas de bouteille, pas d'événements. À valider : le tableau de 195 A = « VM = un processus Kazo, sans enfants » pour toute image apportée ; la console série n'est offerte que si l'image en déclare une.
10. **Modèle de processus Kazo** (section plus haut, points 1 à 6) : la VM est le processus Kazo porteur des limites de l'ADR 0125 ; chaque `launch` (= cgroup) est un processus Kazo enfant sans limites propres ; les enfants invités ne sont pas des processus Kazo ; une console est un enfant comme un autre ; rien ne passe par `StartChild`. À valider avant l'ADR 17.
11. **Trois pièges de prototypage** pour la suite (E8, E14, J0) : le pid de `launch` ≠ pid du programme sous Wine 11 ; `drop_caches` permet de remplacer un fichier 9p déjà lu (contourne le piège E17), mais `create_file` crée en 0644 ; `inotifywait -r` sur une arborescence 9p est inutilisable (dizaines de minutes), surveiller seulement les dossiers inscriptibles. À valider.
