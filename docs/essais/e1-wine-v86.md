# Essai E1 — maquette du jalon 0 : Wine dans v86

Date : 2026-11-10. Code jetable dans `essais/e1-wine-v86/` (question 101, ligne E1 des études prévues). Cet essai couvre la partie « noyau » de E1 : un invité Alpine Linux 3.24 x86 (32 bits) avec Wine 11.0 et un serveur X, démarré dans v86 dans un onglet Chrome, et les mesures de démarrage, de création de la première bouteille, de lancement d'un programme console et du Bloc-notes, de mémoire, de sauvegarde et de restauration d'état, et de téléchargement. Ne sont **pas** couverts ici (restent à faire) : Mono et Gecko (désactivés par `WINEDLLOVERRIDES="mscoree,mshtml="`), le Web Worker avec pont écran OffscreenCanvas (v86 tourne ici dans le fil principal de la page), l'installateur, la remise en route du réseau et des partages après restauration, le disque de l'usager (les deux voies), Firefox, Edge et Safari.

## But

Savoir ce que coûte, en secondes et en octets, un Wine 11 complet dans v86 sur une machine de bureau correcte, pour juger la cible « Bloc-notes utilisable < 30 s depuis un état gabarit (très bon < 10 s) ; RAM totale < 2,5 Go pour une VM » et nourrir D25, 113 et 118.

## Ce qui a été construit

Tout est dans `essais/e1-wine-v86/` (README de 10 lignes pour l'ouvrir à la main).

- `Dockerfile` : `FROM --platform=linux/386 alpine:3.24` ; paquets `openrc alpine-base agetty alpine-conf linux-virt linux-firmware-none wine xorg-server xf86-video-fbdev xf86-video-vesa xf86-input-evdev xvfb xinit xterm xdotool xwininfo xdpyinfo xset font-dejavu twm procps-ng` ; autologin root sur `ttyS0` ; services OpenRC comme dans la recette `v86/tools/docker/alpine` ; un service `local` qui imprime `MARK boot-ready <uptime>` sur la console série ; `mkinitfs -F "base virtio 9p"`. Wine 11.0-r0 : 68 Mio téléchargés par apk, 220 Mio installés.
- `guest/bench.sh` : le script de mesure côté invité (lance X, `wineboot -u`, `wine cmd /c echo hi`, `wine notepad`, tape du texte, imprime des `MARK <nom> <uptime>` et des lignes `MEM`). `guest/startx.sh`, `guest/xorg.conf` (fbdev sur `/dev/fb0`, clavier et souris evdev, sans udev), `guest/twmrc`.
- `build.ps1` : `docker build` → `docker export` (tar de 591 Mio) → `fs2json.py --zstd` → `copy-to-sha256.py --zstd` (outils copiés du dépôt v86, même commit que le paquet npm `v86@0.5.473`). Résultat : `images/alpine-fs.json` (303 Kio) et `images/alpine-rootfs-flat/` : 3 860 fichiers réguliers, 3 800 contenus distincts, **186,6 Mio en zstd** (587,9 Mio décompressés). Les fichiers sont servis à la demande (un GET par fichier lu par l'invité), donc on ne télécharge que ce qui est touché.
- `lib/` : `libv86.js` (351 Kio), `v86.wasm` (1,9 Mio), `seabios.bin` (128 Kio), `vgabios.bin` (36 Kio) — 2,4 Mio fixes.
- `boot.html` : page avec canvas VGA, console série dans un `<pre>`, boutons « Lancer le bench (Xvfb / Xorg) », Save / Restore / Download state, chargement d'un état depuis un fichier, champ de commande série. Elle expose `window.E1` (marques horodatées avec `performance.now()`, événements v86, mesures de mémoire).
- `server.mjs` : serveur statique local avec `Cross-Origin-Opener-Policy: same-origin` et `Cross-Origin-Embedder-Policy: require-corp` (isolation d'origine croisée → `performance.measureUserAgentSpecificMemory()`), Range, compteur d'octets servis, dépôt d'un état par morceaux de 32 Mio.
- `run.mjs` : Playwright pilotant le Chrome installé (`channel: "chrome"`, headless), 2 runs.

Configuration v86 : `memory_size` 1 Gio, VGA 8 Mio, racine 9p (`root=host9p rootfstype=9p rootflags=trans=virtio,cache=loose`), `console=ttyS0 tsc=reliable mitigations=off`, noyau et initramfs lus depuis l'arborescence (`bzimage_initrd_from_filesystem`).

## Protocole

Pour chaque run, `run.mjs` fait dans l'ordre :

1. **Boot à froid** : contexte Playwright neuf (cache HTTP vide), ouverture de `boot.html`, attente du prompt `:~# ` sur la console série. Mesuré : temps page → `MARK boot-ready` (service `local`), page → prompt, uptime invité, octets servis.
2. **Bench invité** (`sh /root/bench.sh xorg`) : X sur le framebuffer `bochs-drm` puis `twm` ; `wineboot -u` ; `wine cmd /c echo hi` ; `wine notepad` en arrière-plan jusqu'à ce qu'une fenêtre mappée dont le titre contient « Notepad » ait une largeur > 50 px (`xdotool search --onlyvisible --name Notepad` + `xwininfo -id`), puis `xdotool type "hello from v86"`. Chaque étape imprime `MARK` ; la page horodate chaque ligne à réception (`performance.now()`), et l'uptime invité est imprimé dans la ligne, ce qui donne les deux horloges.
3. **État** : `save_state()` (taille, durée), envoi au serveur, puis `restore_state()` en page (`stop()` → `restore_state()` → `run()`), puis une commande série vérifie que Notepad est toujours là. Mémoire : `measureUserAgentSpecificMemory()`, tas JS (CDP `Performance.getMetrics`), working set et mémoire privée des processus Chrome (`SystemInfo.getProcessInfo` + `Get-Process`).
4. **Depuis l'état, cache chaud** : nouvel onglet du même contexte, `boot.html?state=state.bin` (`initial_state`), temps page → `emulator-started`, puis commande série → `MARK notepad-alive`.
5. **Boot à chaud** : nouvel onglet, même contexte (fichiers en cache), jusqu'au prompt.
6. **Depuis l'état, cache froid** : contexte neuf, comme 4.

Les temps « hôte » sont des différences de `performance.now()` entre deux lignes `MARK` ; les temps « invité » sont des différences d'uptime. Les deux concordent à 0,1 s près dans tous les runs (le TSC est fiable dans v86 avec `tsc=reliable`).

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (8 cœurs, v86 n'en utilise qu'un), 64 Go de RAM.
- Chrome 156.0.8078.80 (le Chrome installé, piloté par Playwright en mode headless ; même moteur que la fenêtre normale, mais sans compositeur à l'écran).
- Serveur sur `localhost` (le temps de téléchargement n'est donc pas celui d'un vrai réseau : les octets sont comptés, pas la latence).
- Cache : « froid » = contexte Playwright neuf (cache HTTP vide, états non mis en cache : `no-store`) ; « chaud » = même contexte, fichiers de l'image avec `Cache-Control: max-age=3600`.
- v86 0.5.473 (npm, commit 315b2b0 du 2026-10-30).

## Résultats

Deux runs complets (`results-1.json`, `results-2.json`), plus un run préliminaire (sans twm, sorti par un plantage du renderer après la sauvegarde, voir plus bas) dont les chiffres de boot et de `wineboot` sont du même ordre. Temps en secondes ; « page → X » = `performance.now()` depuis l'ouverture de la page ; Mo = 10^6 octets servis, Mio = 2^20.

### Démarrage, Wine, Bloc-notes

| Mesure | Run 1 | Run 2 | Note |
|---|---|---|---|
| v86 prêt (`emulator-started`) | 0,3 s | 0,2 s | wasm compilé, BIOS chargés |
| Boot à froid, page → `MARK boot-ready` | 60,8 s | 62,7 s | uptime invité 50,8 s : le noyau démarre ~10 s après l'ouverture de la page |
| Boot à froid, page → prompt shell | 62,4 s | 64,5 s | montage de la racine 9p : ~14 s à lui seul (uptime 18 → 33 s) |
| Boot à chaud (cache), page → prompt | 62,1 s | 62,8 s | pas plus rapide : le temps est dans le CPU émulé, pas dans le réseau |
| Serveur X prêt (Xorg fbdev + twm) | 16,0 s | 12,1 s | Xvfb : ~10 s (run préliminaire) |
| `wineboot -u` (première bouteille) | 420,4 s | 457,9 s | 406,8 s au run préliminaire ; bouteille de 210 Mio |
| `wine cmd /c echo hi` (bouteille existante) | 9,1 s | 7,3 s | 8,3 s au run préliminaire |
| `wine notepad` → fenêtre X créée et mappée | ≈ 20 s (wineserver déjà lancé) ; 60 à 80 s (wineserver à relancer) | — | mesuré à la main depuis l'état sauvegardé, pas par le bench |
| `wine notepad` → fenêtre de taille réelle | **jamais** (1x1 pixel, voir « ce qui a raté ») | **jamais** | le bench abandonne après ~1 140 s de scrutation |

### Mémoire

| Mesure | Après boot | Après wineboot + Notepad + `save_state` |
|---|---|---|
| Invité (`/proc/meminfo`, total 880 Mio visibles sur 1 Gio) | 28 Mio utilisés | 97 Mio après wineboot, 110 Mio avec Notepad |
| Renderer Chrome, mémoire privée | 1 465 Mio | 3 340 Mio (dont ~607 Mio d'état en mémoire et les morceaux de l'envoi) |
| Tous les processus Chrome de l'instance, mémoire privée | 1 696 Mio | 3 660 Mio |
| `performance.measureUserAgentSpecificMemory()` | 32 Mio | 1 165 Mio |
| Tas JS utilisé | 13 Mio | 23 Mio |

La mémoire wasm de 1 Gio est réservée d'un bloc (`memory_size`) ; le renderer paie 1,4 Gio dès le boot, avant tout Wine. L'invité, lui, n'utilise que 110 Mio : 1 Gio est très large pour cette maquette.

### État sauvegardé

| Mesure | Run 1 | Run 2 |
|---|---|---|
| `save_state()` : taille | 607,4 Mo (604,2 Mo au run préliminaire) | 605,5 Mo |
| `save_state()` : durée | 0,42 s | 0,50 s |
| Même état compressé zstd (Python 3.14, hors navigateur) | niveau 3 : 156,5 Mio en 2,8 s ; niveau 9 : 137,1 Mio en 11,8 s | — |
| `restore_state()` en page (`stop` → `restore` → `run`) | 0,25 s | 0,25 s |
| Rechargement depuis l'état (`initial_state`, 607 Mo bruts, cache chaud) : page → `emulator-started` | 2,2 s | 2,3 s |
| … page → l'invité répond sur la console série (Notepad toujours là) | 3,9 s | 4,0 s |
| Idem, cache froid (contexte neuf) | 2,0 s / 3,5 s | 2,2 s / 3,8 s |

Depuis un état, la VM est de nouveau vivante en 2 s et répond en 4 s, cache chaud ou froid, parce que tout vient de `localhost` : sur un vrai réseau il faut ajouter le temps de télécharger 607 Mo bruts, ou ~140-160 Mio une fois compressés (v86 sait lire un `initial_state` en `.zst`).

### Téléchargement

| Phase | Requêtes | Octets | Détail |
|---|---|---|---|
| Fixe (page, libv86.js, v86.wasm, BIOS, `alpine-fs.json`) | 5 | 2,9 Mo | |
| Boot jusqu'au prompt (noyau, initramfs, modules, OpenRC, busybox…) | 168 | 17,4 Mo | `.zst` seulement |
| Bench (X, `wineboot`, `cmd`, Notepad) | 2 611 | 202,9 Mo | quasiment toute l'image (186,6 Mio = 195,7 Mo) : Wine touche presque tous ses fichiers au premier `wineboot` |
| Boot à chaud (même contexte) | 164 | 16,5 Mo | v86 redemande les `.zst` malgré `max-age` ; seuls page, js, wasm et BIOS sont servis depuis le cache |
| Depuis l'état, cache chaud | 7 | 610,3 Mo | l'état brut (607,6 Mo) + fixe |
| Depuis l'état, cache froid | 24 | 612,0 Mo | + 1,8 Mo de fichiers touchés par la commande de vérification |

Image complète : 3 860 fichiers, 186,6 Mio en zstd (587,9 Mio décompressés), `alpine-fs.json` 303 Kio.

## Ce qui a raté ou a été contourné

- **Xorg** : la première config (`DefaultDepth 16`, `Modes "800x600"`) fait échouer `ScreenInit` du pilote fbdev ; le framebuffer de `bochs-drm` est 1024x768 en 32 bpp, il faut prendre le mode natif en profondeur 24. Avec ça Xorg démarre sur le canvas VGA de v86 (donc pas de repli Xvfb, le bouton Xvfb reste disponible). Les `FBIOPUTCMAP: Invalid argument` du journal Xorg sont sans effet.
- **Le nom d'hôte** n'est pas appliqué (prompt `(none):~#` au lieu de `localhost:~#`) : `setup-hostname` écrit `/etc/hostname` mais le service `hostname` n'est pas dans le runlevel. Sans conséquence ; la détection du prompt est faite indépendante du nom.
- **Bloc-notes : fenêtre créée, jamais dimensionnée.** C'est l'échec de l'essai. Au premier run une faute de syntaxe de ma part (`xdotool search --name -i`, `-i` n'existe pas) masquait le vrai problème ; corrigée, le bench exige une fenêtre mappée de largeur > 50 px. Le vrai problème : Wine 11 crée bien la fenêtre X de Notepad (titre « Untitled - Notepad », `Map State: IsViewable`, processus `notepad.exe` vivant, 15 s de CPU) mais la laisse à **1x1 pixel**, indéfiniment. Tout a été vérifié en restaurant l'état sauvegardé (2 s pour relancer la VM, contre 7 minutes pour refaire `wineboot` : la sauvegarde d'état est un outil de diagnostic en soi) :
  - ce n'est pas Notepad : `winecfg` et `winemine` font pareil ;
  - ce n'est pas le pilote X : identique avec Xorg `fbdev`, Xorg `modesetting` sur `bochs-drm`, et Xvfb (sur lequel tourne la suite de tests de Wine) ;
  - ce n'est pas l'absence de gestionnaire de fenêtres : avec `twm` la fenêtre est reparentée (barre de titre, `+0+19`) mais reste 1x1 ; `Managed=N` / `Decorated=N` dans `HKCU\Software\Wine\X11 Driver` ne changent rien ;
  - ce n'est pas le bureau virtuel : `wine explorer /desktop=d,800x600 notepad.exe` donne un bureau de 800x600 correct, avec Notepad 1x1 dedans ;
  - ce n'est pas le cache des écrans de Wine : effacer `HKLM\System\CurrentControlSet\Enum\DISPLAY` et `Control\Video` puis relancer `wineserver` ne change rien (Wine y avait créé un `Default_Monitor`, signe qu'il n'a trouvé aucune sortie XRandR connectée : `warn:xrandr:get_primary_rect Primary is set to a disconnected XRandR output`) ;
  - ce ne sont pas les polices : 56 polices Wine présentes, 22 vues par fontconfig.
  - Ce qui reste : les traces `warn:x11drv:handle_state_change Ignoring window 0x10098/e00001 old WM_NORMAL_HINTS { pos 0,0 grav 10 }/70, expected { ... }/81` : la machine à états des fenêtres de winex11 (réécrite dans Wine 10) reçoit des `PropertyNotify` avec des numéros de série en retard sur ce qu'elle attend et n'applique jamais la configuration de la fenêtre. Piste à creuser à part : Wine 11.0 i386 d'Alpine contre un autre Wine (10.x, ou wine-staging, ou une build Debian i386), et une trace `WINEDEBUG=+win,+x11drv` complète. Forcer la taille depuis X (`xdotool windowsize 0xa00001 640 400`) donne une fenêtre de 640x400 que Wine repeint au moins en partie (rectangle blanc sur le canvas), mais ce n'est pas une mesure honnête de « Notepad utilisable ».
  En conséquence : **« Bloc-notes utilisable » n'a pas été mesuré**. Ce qui l'a été : le temps d'apparition de sa fenêtre X (≈ 20 s avec wineserver déjà lancé, 60 à 80 s s'il doit redémarrer), et le reste de la chaîne.
- **Plantage du renderer** au premier run : l'envoi de l'état (604 Mo) au serveur par un seul `fetch` avec le `ArrayBuffer` en corps a tué le renderer (1 Gio de mémoire wasm + 604 Mo d'état + copie du corps), et `page.evaluate` de Playwright ne s'est jamais terminé. Envoi par morceaux de 32 Mio, et garde-fou sur l'événement `crash`.
- `build.ps1` : `--zstd` mal passé aux scripts Python (un tableau d'un élément devient une chaîne en PowerShell) ; `tar --delete` n'existe pas dans le bsdtar de Windows, remplacé par `strip-dockerenv.py`.
- Rien n'est mis en Web Worker : v86 tourne dans le fil principal de la page.

## Limites

- Une seule machine, un seul navigateur, headless ; `localhost`. Deux runs seulement.
- Pas de Mono ni de Gecko, pas de réseau dans l'invité, pas de son.
- Le temps « Notepad visible » est détecté par la taille de la fenêtre X, pas par un rendu vérifié à l'œil ; la capture du canvas en headless est fournie à titre indicatif (`images/screen-run*.png`).
- Les fichiers de l'arborescence 9p sont lus à la demande : le « téléchargement de l'image » dépend de ce que l'invité touche, donc des programmes lancés.

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **La cible « Bloc-notes utilisable < 30 s depuis un état gabarit » est atteignable sur le papier, pas démontrée.** Depuis un état, la VM est vivante en 2 s et répond en 4 s sur `localhost` ; ce qui manque, c'est (a) le téléchargement de l'état (607 Mo bruts, ~140-160 Mio en zstd, à ajouter selon le débit de l'usager, et à comparer aux 1,2 Mo/s d'un simple ADSL : 2 minutes), et (b) un Wine dont les fenêtres s'affichent. Le « très bon < 10 s » suppose l'état déjà en cache local (OPFS, voir E2) : à valider.
2. **Ne jamais faire `wineboot` chez l'usager** : 7 minutes dans v86 sur un Ryzen 3700X (cœur unique émulé), contre ~10 s en natif. La première bouteille doit venir dans l'état gabarit ; créer une bouteille par programme (D41) voudrait dire 7 minutes par bouteille, ou une copie de la bouteille gabarit (210 Mio de fichiers, ce que E2 doit chiffrer en copie-sur-écriture). À valider.
3. **Lancer un programme Wine coûte ~9 s** (`wine cmd /c echo hi`, bouteille et wineserver déjà chauds) : à prévoir dans l'interface (indicateur d'attente), et à remesurer avec Mono/Gecko présents. À valider.
4. **Budget mémoire** : le renderer Chrome paie 1,4 Gio dès le boot pour 1 Gio de `memory_size`, et 3,3 Gio avec un état de 600 Mo en mémoire ; l'invité n'utilise que 110 Mio. Proposition : 512 Mio pour la VM (à vérifier avec un vrai programme + Mono/Gecko), et ne jamais garder l'état en mémoire après l'avoir écrit (le `fetch` d'un corps de 600 Mo a tué le renderer : écrire par morceaux, ou via un Worker et OPFS). La cible « < 2,5 Go par VM » tient si l'état n'est pas gardé en mémoire. À valider.
5. **Le boot à froid de Linux (62 s) n'est pas le chemin normal** : il ne sert qu'à fabriquer l'état gabarit (hors ligne, chez nous). Le boot à chaud n'est pas plus rapide (62 s) : le coût est CPU, pas réseau. À valider.
6. **Image en 9p servie fichier par fichier** : 186,6 Mio zstd, mais `wineboot` touche presque tout (203 Mo). Avec un état gabarit, l'image reste nécessaire derrière (les fichiers non encore chargés sont lus à la demande) : il faut donc servir l'image **et** l'état, soit ~350 Mio compressés au total pour la première visite, moins ce que le cache a déjà. Un `.zst` par fichier avec 3 800 requêtes HTTP est raisonnable sur `localhost` ; sur un vrai réseau, à mesurer (HTTP/2 ou regroupement). À valider.
7. **v86 recharge les `.zst` malgré `Cache-Control`** (16,5 Mo à chaque boot à chaud) : à regarder dans `filestorage.js` avant de compter sur le cache HTTP ; OPFS ou le Cache API côté Kazo seraient de toute façon plus sûrs. À valider.
8. **Xorg sur le VGA de v86 marche** (`bochs-drm` + `fbdev`, 1024x768x24, curseur visible sur le canvas) : la voie « affichage par le VGA émulé » est ouverte pour E4 ; mais Wine 11 n'y affiche rien tant que le point « fenêtres 1x1 » n'est pas résolu. Décision de périmètre à prendre (tes consignes §4) : soit on creuse Wine (version, build), soit le jalon 0 est redéfini sur des programmes console le temps de comprendre. À valider.
9. **La sauvegarde d'état est rapide** (0,4 s pour 607 Mo, restauration 0,25 s en page) : les points de restauration fréquents de D56 / E15 sont crédibles côté CPU ; le coût est le stockage (E2) et la compression (3 s en zstd-3 hors navigateur ; dans le navigateur, à mesurer). À valider.
