# Essai E17 — Wine dédié : un autre Wine que celui d'Alpine corrige-t-il les fenêtres 1x1 ?

Date : 2026-11-11. Code jetable dans `essais/e17-wine-dedie/` (décision 170 A, jour 1 ; plan 2026-11-11 §4.2 ligne « Wine dédié »). Cet essai reprend le bogue de E1/E4 (Wine 11.0 i386 d'Alpine 3.24 sous Xorg dans v86 : Notepad, winecfg, winemine restent mappés à 1x1 pixel ; côté Win32 les rectangles sont corrects mais winex11 reçoit (0,0)-(1,1)) et mesure, dans le même v86 (0.5.473, Chrome headless via Playwright), un invité **Debian trixie i386** avec le **Wine i386 de WineHQ** (`wine-stable-i386` 11.0.0.0, glibc), noyau `linux-virt` d'Alpine (celui de E1), bouteille préfabriquée dans Docker. Un contrôle préalable sur l'image E1 (Xorg sans Xinerama, trace `WINEDEBUG`) dit si le bogue est propre au Wine d'Alpine. Le jour 2 de 170 (lecture de `X11DRV_UpdateDisplayDevices`, Wine recompilé) n'a pas été fait.

## But

Savoir si Windows graphique reste en fin de V1 (J3) : critère « Bloc-notes affiché à une taille normale (> 100x100) et utilisable (texte tapé par `xdotool`, lu sur une capture), dans v86, sous Chrome », avec quel Wine, et ce que ça implique pour 187 (le Wine de l'image) et pour 170 (jour 2 nécessaire ou non). Mesurer aussi le temps de boot, le temps d'ouverture de Notepad, la mémoire du renderer et la taille de l'image zstd.

## Ce qui a été construit

Tout est dans `essais/e17-wine-dedie/` (README de 10 lignes).

- `Dockerfile` en deux étapes : (1) `alpine:3.24` i386 + `linux-virt` + `mkinitfs -F "base virtio 9p"` (noyau 6.18.63-0-virt, même recette que E1) ; (2) `debian:trixie-slim` i386, dépôt WineHQ (`winehq.key`, `winehq-trixie.sources`, suite `trixie` `main` i386), `winehq-<branche>` + `wine-<branche>` + `wine-<branche>-i386` épinglés (`stable` = 11.0.0.0~trixie-1, `devel` = 11.21~trixie-1, `staging` = 11.21~trixie-1), les bibliothèques X recommandées par Wine (fontconfig, freetype, Xcomposite, Xcursor, Xfixes, Xi, Xinerama, Xrandr, Xrender, Xxf86vm, gnutls), `xserver-xorg-core`, pilotes `fbdev`, `dummy`, `evdev`, `x11-utils`, `x11-xserver-utils`, `xdotool`, `twm`, `fonts-dejavu-core`, `kmod`, `procps` ; **sans Mono ni Gecko** (`WINEDLLOVERRIDES="mscoree,mshtml="`), sans GL/EGL, sans son. Le noyau, l'initramfs et `/lib/modules` d'Alpine sont copiés dans l'image Debian ; les modules `.ko.gz` sont décompressés et `depmod` refait (voir « ce qui a raté »). **Bouteille préfabriquée** : `wineboot -u` tourne dans le conteneur (Wine i386 natif sous Docker x86_64, ~60 s) au lieu de ~7 min dans v86 (E1) ; sans serveur X, donc sans aucune clé d'affichage dans le registre — elles sont créées au premier `wine` sous Xorg, dans la VM, exactement le cas à tester.
- `guest/init.sh` (PID 1, remplace sysvinit : montages, `modprobe bochs atkbd psmouse evdev`, `MARK boot-ready`, shell `dash` relancé sur `ttyS0` avec `PS1='debian:~# '`), `guest/shrc` (variables Wine), `guest/startx.sh` (modes `xorg` = fbdev comme E1, `noxinerama` = fbdev + `Section "Extensions" Option "XINERAMA" "false"`, `noxinerama-cli` = `-extension XINERAMA`, `dummy`, `xorg-norandr`), `guest/xorg*.conf`, `guest/twmrc` (E1), `guest/bench.sh` (X, `twm`, `wine cmd /c echo hi`, puis pour chaque programme : lancement, attente d'une fenêtre mappée dont le titre correspond et dont la largeur dépasse 100 px, ligne `WIN <prog> <id> <l>x<h>+<x>+<y> <titre>`, frappe de `hello from v86 e17` dans Notepad ; `MARK` et `MEM` comme E1).
- `build.ps1 -Branch stable|devel|staging` : `docker build --platform linux/386` → `docker export` → `strip-dockerenv.py` → `fs2json.py --zstd` → `copy-to-sha256.py --zstd` dans un **dossier plat partagé entre branches** (`images/rootfs-flat/`, fichiers nommés par SHA-256 : un fichier commun aux deux images n'est stocké qu'une fois, et les 667 Mio de DLL PE que `wineboot` copie dans la bouteille sont les mêmes octets que ceux de `/opt/wine-stable/lib/wine/i386-windows/`, donc gratuits dans l'image). Le tar intermédiaire est supprimé.
- `boot.html` (E1, paramètres `?fs=`, `?flat=`, `?state=`, `?mem=` ; `window.E17`), `server.mjs` (E4 : COOP/COEP, Range, compteur d'octets, dépôt d'état par morceaux, repli lecture seule sur `essais/e1-wine-v86/images/` pour relancer l'image E1), `drive.mjs` (E4 : pilotage interactif par HTTP : commande série, lecture/injection de fichiers par 9p, capture, sauvegarde d'état), `run.mjs` (mesure automatique : boot à froid, bench, capture du canvas, mémoire, `save_state`).

Configuration v86 : `memory_size` 1 Gio, VGA 8 Mio, racine 9p (`root=host9p rootfstype=9p rootflags=trans=virtio,cache=loose`), `console=ttyS0 tsc=reliable mitigations=off`, noyau et initramfs lus dans l'arborescence.

## Protocole

Dans l'ordre du plan, en s'arrêtant à la première étape qui réussit :

0. **Contrôle sur l'image E1** (Alpine 3.24, Wine 11.0-r0, image 9p toujours sur le disque, Docker non reconstruit) via `drive.mjs --fs alpine-fs.json --flat alpine-rootfs-flat` : boot, `wineboot -u` (7 min, état sauvegardé après), puis (a) Xorg fbdev avec XINERAMA désactivé et `wine notepad` ; (b) pilote `dummy` : **non disponible** dans l'image E1 (paquet `xf86-video-dummy` absent, pas de réseau dans la VM) ; (c) `WINEDEBUG=+x11drv,+win` sur `wine notepad`, grep `UpdateDisplayDevices`, `xinerama`, `xrandr`. Boîte de temps : 1 h 30.
1. **Debian i386 + WineHQ 11.0** : `run.mjs --runs 2` : contexte Chrome neuf (cache vide), boot jusqu'au prompt, `sh /root/bench.sh xorg notepad winecfg winemine`, capture du canvas, mémoire (CDP `Performance.getMetrics`, `measureUserAgentSpecificMemory`, `Get-Process` sur les processus Chrome), `save_state`. Avant cela, une session interactive (`drive.mjs`) a servi à mettre l'image au point.
2. **WineHQ 11.21 (`wine-devel-i386`)** : seulement si 11.0 échoue ; l'image a tout de même été construite (en arrière-plan) pour chiffrer sa taille.

Les temps « hôte » sont des différences de `performance.now()` entre deux lignes `MARK` reçues sur la console série ; les temps « invité » des différences d'uptime ; ils concordent à 0,1 s près.

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (8 cœurs, v86 n'en utilise qu'un, dans le fil principal de la page), 64 Go. Pendant les runs, une deuxième VM v86 (l'image E1, étape 0) et un `docker build` (image devel) tournaient en parallèle sur la même machine ; un autre essai (E3) tournait aussi par moments.
- Chrome 156.0.8078.80 (canal installé, Playwright, headless, profil temporaire supprimé) ; v86 0.5.473 (npm) ; serveur sur `localhost` ; Docker Desktop (WSL 2), Python 3.14.
- Cache : chaque run part d'un contexte Chrome neuf (cache HTTP vide) ; les `.zst` sont servis avec `max-age=3600`.
- Disque : 21,7 Go libres au départ (puis bien plus après un nettoyage fait par ailleurs) ; jamais sous 180 Go pendant l'essai.

## Résultats

### WineHQ 11.0 (`wine-stable-i386` 11.0.0.0~trixie-1) sur Debian trixie i386 — critère atteint

Deux runs automatiques (`results-1.json`, `results-2.json`) plus la session interactive de mise au point (même image avant correction des modules, chiffres équivalents). Temps en secondes.

| Mesure | Run 1 | Run 2 | Note |
|---|---|---|---|
| v86 prêt (`emulator-started`) | 0,24 s | 0,24 s | |
| Boot à froid, page → `MARK boot-ready` | 48,8 s | 49,8 s | uptime invité 39,0 / 37,9 s (E1 Alpine : 60,8-62,7 s, uptime 50,8 s) ; montage 9p ≈ 14 s (uptime 18 → 32) |
| Boot à froid, page → prompt shell | 49,1 s | 50,0 s | |
| Octets servis pour le boot | 55 requêtes, 21,7 Mo | 55, 21,7 Mo | noyau 6 Mo, initramfs, modules |
| Xorg fbdev + `twm` prêts | 13,9 s | 19,8 s | 1024x768x24, extensions RANDR et XINERAMA présentes |
| `wine cmd /c echo hi` (premier processus Wine : wineserver, bouteille lue à la demande) | 78,9 s | 62,5 s | E1 : 7,3-9,1 s **avec** wineserver déjà chaud après `wineboot` dans la VM ; ici c'est le premier contact de Wine avec la bouteille (lecture 9p de ~95 Mo, voir plus bas) |
| `wine notepad` → fenêtre « Untitled - Notepad » de taille réelle | **32,1 s** | **23,0 s** | **721x512+50+50** ; session interactive : 23,2 s |
| Frappe `hello from v86 e17` (`xdotool type`, 18 caractères) | 3,1 s | 3,0 s | Notepad affiche « Ln 1, Col 19 » (capture `images/e17-run<n>.png`) |
| `wine winecfg` → « Wine configuration » | 26,2 s | 21,3 s | **410x446+80+80** |
| `wine winemine` → « WineMine » | 18,0 s | 11,9 s | **154x201+110+110** |
| Octets servis pendant le bench | 392 req., 98,8 Mo | 391, 98,8 Mo | Wine ne touche qu'un quart de l'image (380 Mio) |
| Mémoire invité (`/proc/meminfo`, 880 Mio visibles) | 25 Mio au boot, 86 après `cmd`, 142 Mio avec les trois fenêtres | 25 / 90 / 141 Mio | |
| Renderer Chrome, mémoire privée | 1 431 Mio au boot, 2 260 Mio après le bench | 1 440 / 2 225 Mio | tous processus Chrome : 1 690 → 2 571 Mio |
| `measureUserAgentSpecificMemory` | 36 → 231 Mio | 37 → 231 Mio | |
| `save_state()` (trois fenêtres Wine ouvertes) | 299 Mo en 0,36 s | 299 Mo en 0,58 s | E1 : 607 Mo (le `wineboot` dans la VM salit beaucoup plus de pages) |

Les trois fenêtres sont dessinées sur le canvas VGA (`images/e17-run1.png`, `e17-run2.png`, `e17-stable-notepad.png` : Notepad avec menus, barre de défilement et « Ln 1, Col 19 » ; winecfg avec ses onglets ; winemine avec sa grille). `xwininfo -root -tree` les montre reparentées par `twm` (`721x512+0+19 +50+50`, etc.), avec `WM_CLASS` = `notepad.exe`, `winecfg.exe`, `winemine.exe`.

### Alpine 3.24, Wine 11.0-r0 (image E1) + options Xorg — étape 0

Session interactive sur l'image E1 (pas de reconstruction Docker), boot 53 s, `wineboot -u` 7,5 min (uptime 90 → 541 s), état sauvegardé après (527 Mo). Boîte de temps 1 h 30 tenue (1 h 20, dont 15 min perdues sur l'injection 9p, voir plus bas).

| Variante | Fait | Xorg | `wine cmd` | `wine notepad` | Résultat |
|---|---|---|---|---|---|
| (a) fbdev + `Section "Extensions" Option "XINERAMA" "false"` + `twm` | oui | 1024x768, **RANDR et XINERAMA toujours listées** par `xdpyinfo` (la section est lue : `(++) Using config file`, mais l'extension reste active) | 69 s | « Untitled - Notepad » mappée à **1x1+1+1**, toujours 1x1 après 491 s (`MARK notepad-failed`) | échec |
| (a') fbdev + `Xorg -extension XINERAMA` + `twm` | oui | idem : XINERAMA toujours listée | — | **1x1** pendant les 120 s de scrutation | échec |
| (a'') Xvfb 800x600 sans gestionnaire de fenêtres (lancé par erreur, voir « ce qui a raté ») | oui | Xvfb | 75 s | **1x1+0+0** après 512 s | échec (même résultat que E1) |
| (b) pilote `dummy` | non | paquet `xf86-video-dummy` absent de l'image E1, pas de réseau dans la VM | — | — | non mesuré |
| (c) trace `WINEDEBUG=+x11drv,+win,+xrandr wine notepad` (2 300 lignes, sur (a')) | oui | — | — | voir ci-dessous | — |

Ce que dit la trace Alpine (`/root/trace.log` dans la VM, non conservée hors de l'invité ; lignes citées) :

- `xinerama_init monitor 0x0: (0,0)-(1024,768) work (0,0)-(1024,768) (primary)` ; `X11DRV_XRandR_Init Found XRandR 1.6.` ; `Display device functions are now handled by: XRandR 1.4` ; `X11DRV_UpdateDisplayDevices via "XRandR 1.4"` (dans E4, avec RANDR désactivé, c'était `via "Xinerama"` : même résultat) ;
- `warn:xrandr:xrandr14_get_gpus XRandR implementation doesn't report any providers, faking one.` ; `warn:xrandr:get_primary_rect Primary is set to a disconnected XRandR output.` (trois fois) ; `GPU count: 1`, `GPU: 0 (null), adapter count: 1`, `adapter: 0x4d, monitor count: 1` : le pilote fbdev n'a pas de sortie RandR 1.2 connectée ;
- côté Win32 les tailles sont correctes : `NtUserCreateWindowEx hwnd 0x1002a cs 0,0 100x100 { window (0,0)-(100,100) … }` ; mais `X11DRV_WindowPosChanging hwnd 0x1002a … rects { window (0,0)-(1,1), client (0,0)-(1,1), visible (0,0)-(1,1) }` puis `window_set_wm_normal_hints … requesting WM_NORMAL_HINTS { pos 0,0 min 1,1 max 1,1 grav 10 } serial 72` ; 11 lignes `Ignoring window … old WM_NORMAL_HINTS` ; le DC du bureau est ici `dc_rect (0,0)-(1024,768)` (pas les 73728x55296 de E4).

**Même trace sur Debian + WineHQ 11.0** (même `xorg.conf`, fbdev, `twm`, 991 lignes) : la partie X est **identique** — `xinerama_init monitor 0x0: (0,0)-(1024,768)`, `Found XRandR 1.6.`, `Display device functions are now handled by: XRandR 1.4`, `X11DRV_UpdateDisplayDevices via "XRandR 1.4"`, `warn:xrandr:xrandr14_get_gpus XRandR implementation doesn't report any providers, faking one.`, `warn:xrandr:get_primary_rect Primary is set to a disconnected XRandR output.`, `GPU count: 1`, 7 lignes `Ignoring window … WM_NORMAL_HINTS` — mais winex11 reçoit cette fois les bons rectangles : `X11DRV_WindowPosChanging hwnd 0x10028 … rects { window (0,0)-(100,100), client (0,0)-(100,100), visible (0,0)-(100,100) }`, et Notepad passe à 721x512 au 20ᵉ sondage (≈ 40 s). Les avertissements XRandR (pas de fournisseur, sortie primaire déconnectée) sont donc **sans rapport** avec le bogue : ils sont présents dans les deux cas. Ce qui diffère est entre `NtUserCreateWindowEx` (rectangles corrects) et `X11DRV_WindowPosChanging` (rectangles 1x1 chez Alpine, corrects chez WineHQ), c'est-à-dire la conversion repère virtuel → repère brut du moniteur dans `win32u` (`map_rect_virt_to_raw` et le mode courant du moniteur), dans le même environnement X. Piste pour un éventuel jour 2 : comparer, dans les deux bouteilles, `HKLM\System\CurrentControlSet\Control\Video\{…}\0000` (`DefaultSettings.*`) et `Enum\DISPLAY` après le premier `wine` sous X, puis lire `win32u/sysparams.c` (modes ajoutés par `add_modes` quand le gestionnaire est « XRandR 1.4 » sans sortie connectée) plutôt que `X11DRV_UpdateDisplayDevices` lui-même.

### WineHQ 11.21 (`wine-devel-i386` 11.21~trixie-1) — non requis par le protocole, un run de contrôle

L'image devel a été construite en arrière-plan (même `Dockerfile`, `-Branch devel`) et mesurée une fois (`results-3.json`, pendant que la VM Alpine de l'étape 0 tournait encore). Même résultat que 11.0 : les trois fenêtres ont leur taille normale.

| Mesure | Run 3 (11.21) | Rappel 11.0 (runs 1-2) |
|---|---|---|
| Boot à froid, page → prompt | 55,2 s (uptime 42,8 s à `boot-ready`) | 49,1 / 50,0 s |
| Xorg + `twm` | 14,5 s | 13,9 / 19,8 s |
| `wine cmd /c echo hi` (premier processus) | 45,2 s | 78,9 / 62,5 s |
| `wine notepad` → 721x512 | 45,3 s | 32,1 / 23,0 s |
| Frappe (18 caractères) | 3,2 s | 3,1 / 3,0 s |
| `winecfg` → 410x446 ; `winemine` → 154x201 | 21,3 s ; 14,4 s | 26,2 / 21,3 s ; 18,0 / 11,9 s |
| Octets servis pendant le bench | 387 req., 109,1 Mo | 392, 98,8 Mo |
| Invité, trois fenêtres | 187 Mio | 142 / 141 Mio |
| Renderer, mémoire privée (boot → bench) | 1 443 → 2 310 Mio | 1 431 → 2 260 ; 1 440 → 2 225 Mio |
| `save_state` | 359 Mo en 0,39 s | 299 Mo |

`wine-staging-i386` 11.21 n'a pas été essayé (même dépôt, même recette : `-Branch staging`).

### Taille des images

| Image | Fichiers réguliers (distincts) | zstd | décompressé | `fs.json` |
|---|---|---|---|---|
| E1 : Alpine 3.24 + Wine 11.0-r0 (rappel) | 3 860 (3 800) | 186,6 Mio | 587,9 Mio | 303 Kio |
| E17 stable : Debian trixie i386 + WineHQ 11.0 + Xorg + bouteille | 12 732 (10 795) | **380,1 Mio** | 2 101,8 Mio | 975 Kio |
| E17 devel : idem avec WineHQ 11.21 (bouteille 849 Mio) | 12 942 (10 965) | 423,3 Mio | 2 382,9 Mio | 992 Kio |

Détail de l'image stable (installé) : `wine-stable-i386` 675 Mio (`/opt/wine-stable` : 691 Mio, dont les DLL PE i386), bouteille préfabriquée 727 Mio (dont 667 Mio de copies de ces mêmes DLL dans `drive_c/windows/system32`, dédoublonnées par hachage dans l'image 9p), modules du noyau décompressés ≈ 130 Mio, Debian de base + Xorg le reste. Le tar `docker export` fait 2 109 Mio.

## Ce qui a raté ou a été contourné

- **Modules du noyau** : le `kmod` de Debian trixie refuse les `.ko.gz` d'Alpine (`modprobe: could not insert 'bochs': Function not implemented`) : pas de `/dev/fb0`, Xorg échoue (`X no fb0`). Contourné à la main dans la VM (`gunzip -k` + `insmod` dans l'ordre de `modprobe --show-depends`), puis dans le `Dockerfile` (`gunzip` de tous les modules + `depmod`). Reconstruction : 1 min de `docker build` (couches en cache), 2,5 min d'export et de compression (seuls les fichiers nouveaux sont recompressés).
- **Clavier et souris** : sans `udev` ni `mdev`, rien ne charge `atkbd`/`psmouse` ; `/dev/input` n'existe pas tant qu'ils ne sont pas chargés (`i8042` est intégré). Ajoutés à `init.sh`.
- **Injection de fichiers par 9p dans l'image E1** (`emu.create_file`) : un fichier **déjà vu par l'invité** (ici `startx.sh`, listé par `ls` juste avant) n'est pas remplacé du point de vue de l'invité (`cache=loose` : l'inode et les données restent celles du cache), et `emu.read_file` ne voit pas les fichiers créés par l'invité pendant la session (« File not found » sur `/root/xorg.log`). Conséquence : la première exécution de la variante (a) a tourné sur **Xvfb** (le `startx.sh` de E1 prend « noxinerama » pour « pas xorg »), sans gestionnaire de fenêtres ; refaite après réécriture du script par la console série (`cat <<'EOF'`). E4 n'avait pas rencontré ce cas (fichiers nouveaux, dossier nouveau).
- **`strip-dockerenv.py` de E1 ne retirait rien** (`lstrip("./")` enlève aussi le point de `.dockerenv`) : corrigé dans la copie E17 ; `.dockerenv` à la racine n'a jamais gêné le boot 9p ici (ni, visiblement, dans E1).
- `XDG_RUNTIME_DIR is invalid or not set` imprimé deux fois par `wineboot` dans Docker : sans effet.
- `xdotool windowraise` sur la fenêtre cliente ne la remonte pas au-dessus des autres sous `twm` (il faudrait remonter le cadre) : sur les captures, Notepad est partiellement couvert par winecfg et winemine ; le texte tapé se lit à « Ln 1, Col 19 » et au début de la ligne (« hell… »). Dans la session interactive, la capture a été prise avant les deux autres fenêtres.
- Le `ctl` de pilotage (hors livrable) imprimait les sorties série en cp1252 : erreurs d'encodage sur les séquences ANSI, sans effet sur les mesures.
- Ports : j'ai d'abord lancé le pilote sur 8098/8099, réservés à l'essai E3 : arrêté au bout de quelques secondes et relancé sur 8130-8137.

## Limites

- Une seule machine, Chrome headless seulement, `localhost`, deux runs automatiques ; d'autres charges tournaient en parallèle (deuxième VM, `docker build`, E3) : les temps absolus sont pessimistes, les géométries et les comportements ne le sont pas.
- Pas de Mono ni de Gecko, pas de GL/EGL (winex11 11 utilise EGL par défaut : non installé, Wine s'en passe pour ces trois programmes), pas de son, pas de réseau dans l'invité.
- « Utilisable » est mesuré par la géométrie X, la frappe `xdotool` et la position du curseur de Notepad sur la capture ; pas de clic dans les menus, pas de boîte de dialogue, pas d'installateur.
- Les images ne sont pas optimisées (Debian complet avec `apt`, 675 Mio de Wine, bouteille non dégraissée) ; les 380 Mio sont une borne haute.
- L'étape 0 n'a testé qu'une partie des pilotes (fbdev avec et sans Xinerama ; `dummy` absent de l'image E1) et le jour 2 (lecture de `X11DRV_UpdateDisplayDevices`) n'a pas été fait : la **cause** du bogue d'Alpine n'est pas établie, seulement le fait qu'il ne se reproduit pas avec le Wine de WineHQ sur le même noyau, le même v86 et la même configuration Xorg.

## Chiffres bruts

- `essais/e17-wine-dedie/results-1.json`, `results-2.json`, `results-3.json` (11.21) (marques horodatées, lignes `WIN`/`MEM`/`X`, octets servis par extension, mémoire page et processus, `save_state`, taille de l'image) ; captures `images/e17-run1.png`, `e17-run2.png`, `e17-stable-notepad.png` (session interactive), `e17-run3.png` (11.21) ; journaux `images/docker-build-stable.log`, `docker-build-devel.log`, `run-stable.log`, `drive-e1.log`, `drive-stable.log` ; état `images/e1-wineboot.bin` (527 Mo, image E1 après `wineboot`, supprimé à la fin). Dossier `images/` ignoré par git.
- Paquets WineHQ (`dists/trixie/main/binary-i386/Packages`, 2026-10-31) : `wine-stable-i386` 11.0.0.0~trixie-1, 109,4 Mo téléchargés, 690 615 Kio installés ; `wine-devel-i386` 11.21~trixie-1, 136,1 Mo, 810 567 Kio ; `wine-staging-i386` 11.21~trixie-1, 813 745 Kio.
- Docker : images `e17-wine-stable` (3,05 Go) et `e17-wine-devel` (3,44 Go) conservées ; tars intermédiaires supprimés ; aucun autre conteneur ni image touché.
- Étape 0 (image E1, console série, journal `images/drive-e1.log`) : boot 53,3 s d'uptime à `boot-ready` ; `wineboot -u` ≈ 450 s ; variante (a) : `x-ready` 14,0 s après `bench-start`, `cmd` 69,0 s, Notepad 1x1 après 490,8 s ; variante (a'') (Xvfb) : `cmd` 74,8 s, Notepad 1x1 après 512,1 s ; trace (a') : 2 300 lignes.

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **Le critère est atteint, avec WineHQ 11.0 (`wine-stable-i386`, glibc, Debian trixie i386)** : Notepad 721x512 (puis winecfg 410x446 et winemine 154x201) affichés sur le VGA de v86 sous Xorg fbdev + twm, texte tapé et lu, 23 à 32 s après `wine notepad` (wineserver déjà lancé par `wine cmd`), deux runs sur deux. Le bogue 1x1 de E1/E4 **n'est pas un bogue de v86, du noyau, de Xorg ni de la configuration X** (mêmes noyau `linux-virt` 6.18, même `xorg.conf`, même v86) : il est propre à la build i386 de Wine 11.0 d'Alpine (musl, `-no-pie`, tests désactivés, cf. E13), ou à ce que la bouteille y devient. À valider : **Windows graphique reste en fin de V1 (J3)** ; le jour 2 de 170 (lecture de `X11DRV_UpdateDisplayDevices`, Wine recompilé) **n'est pas nécessaire** pour avancer.
2. **187 (le Wine de l'image)** : la voie la moins chère qui marche aujourd'hui est **Debian trixie i386 + paquets WineHQ i386** (stable 11.0 épinglée, devel/staging disponibles au même prix), avec le noyau `linux-virt` d'Alpine et un init minimal, plutôt qu'Alpine + Wine d'Alpine. Coût : image 380 Mio zstd (contre 187 Mio) et 2,1 Gio décompressés, boot plus court (49 s contre 62 s), bouteille préfabriquée gratuite dans l'image grâce au dédoublonnage par hachage. Alternatives non testées : chroot glibc dans Alpine (plus petit, deux libc), Wine recompilé par nous (E13 : faisable, mais à tester à chaque version). À valider : **ADR ou décision 187 = Debian i386 + WineHQ**, avec la règle de suivi de E13 (vérifier à chaque Wine stable de janvier que `binary-i386` trixie/forky est toujours publié).
3. **Ne pas faire `wineboot` dans la VM** se confirme : la bouteille préfabriquée dans Docker (60 s natif) donne un premier `wine` à 62-79 s (lecture à la demande de ~95 Mo de DLL par 9p) puis des lancements à 12-32 s ; l'état sauvegardé après trois fenêtres fait 299 Mo (607 Mo dans E1 après `wineboot` en VM). À valider : la bouteille gabarit (152) est produite par la recette d'image, pas par la VM, et l'état gabarit est pris après un premier `wine` à blanc pour que le premier lancement chez l'usager soit à ~25 s et non ~70 s.
4. **Coût du premier processus Wine (62-79 s)** : c'est du 9p à la demande (392 requêtes, 99 Mo), pas du calcul ; un état gabarit pris après ce premier lancement (point 3) ou un préchargement des DLL de la bouteille (E3) le ramène sous la barre. À valider dans E3 / le jalon J3 (« Bloc-notes utilisable < 30 s après l'invite » : 23-32 s mesurés ici, wineserver chaud).
5. **Budget mémoire** : renderer à 1,43 Gio au boot (1 Gio de `memory_size`) et 2,2-2,3 Gio avec trois fenêtres Wine et l'état en mémoire ; l'invité n'utilise que 142 Mio. Même conclusion que E1 : 512 Mio de VM suffiraient pour ces programmes ; à revalider avec Mono/Gecko et un vrai programme.
6. **Taille de l'image** : 380 Mio zstd est une borne haute (Debian avec `apt`, locales, `/opt/wine-stable` complet avec outils, 130 Mio de modules dont presque aucun ne sert) ; une passe de dégraissage (modules utiles seulement, `apt` retiré, `wine-*-i386` sans les DLL inutiles) devrait la ramener vers 250 Mio sans changer le Wine. À chiffrer dans E3 (distribution). À valider.
7. **Le contrôle sur Alpine (étape 0)** : Xorg sans Xinerama n'a pas pu être obtenu sur l'image E1 (ni par `Section "Extensions"`, ni par `-extension XINERAMA` : `xdpyinfo` liste toujours XINERAMA, et Notepad reste 1x1 dans les deux cas), le pilote `dummy` n'y est pas, et la trace montre que le chemin X (XRandR 1.4 « faked », sortie primaire déconnectée) est **le même** avec le Wine de WineHQ qui, lui, affiche les fenêtres : les options Xorg ne sont pas la bonne piste, et le bogue est dans la build d'Alpine (entre `NtUserCreateWindowEx` et `X11DRV_WindowPosChanging`), pas dans X ni dans v86. À valider : ne plus investir dans le Wine d'Alpine pour l'affichage ; le garder éventuellement pour la console (image plus petite) seulement si 187 décide deux images, ce qui n'est pas recommandé (deux Wine à suivre).
8. **Deux pièges de prototypage** à noter pour E7/E8 (agent, 9p) : l'injection par `create_file` ne remplace pas un fichier déjà lu par l'invité (`cache=loose`), et le `kmod` de Debian ne lit pas les modules compressés d'Alpine ; les deux sont des détails de recette, pas des risques de conception. À valider.
