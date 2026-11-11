# Essai E4 — fenêtre par fenêtre : agent XComposite dans l'invité contre découpage du canvas

Date : 2026-11-10. Code jetable dans `essais/e4-fenetres/` (ligne E4 des études prévues ; nourrit la question 112 et D26). Cet essai reprend l'invité de E1 (Alpine 3.24 x86, Xorg sur le VGA de v86) et mesure deux façons de montrer chaque fenêtre X11 de l'invité comme une fenêtre séparée dans la page : (a) un agent dans l'invité qui suit les fenêtres de premier niveau (propriétés ICCCM/EWMH), les redirige avec XComposite et envoie les pixels de chaque fenêtre à part ; (b) le simple découpage de l'écran VGA de v86 selon la géométrie des fenêtres. **Les fenêtres mesurées sont des clients X11 natifs (xterm, xclock, xeyes, xmessage), pas des fenêtres Wine** : le point « fenêtres Wine à 1x1 pixel » de E1 n'a pas été résolu dans la boîte de temps prévue (voir plus bas).

## But

Savoir ce que coûte, en CPU invité, en CPU page, en octets par seconde et en délai, une fenêtre Kazo par fenêtre X11 de l'invité, et ce que Kazo devrait ajouter pour ce mode (fenêtres sans propriétaire, icône, transients, menus, ordre z, focus), pour fixer le calendrier de « une fenêtre Kazo par vraie fenêtre Windows » (question 112, D26). Critère de l'étude : deux fenêtres affichées comme deux fenêtres Kazo, déplaçables, sans perte visible.

## Ce qui a été construit

Tout est dans `essais/e4-fenetres/` (README de 10 lignes).

- `Dockerfile` : même base que E1 (mêmes instructions, couches Docker réutilisées) + `xprop xwd xclock xeyes xmessage xrefresh xsetroot libxcomposite libxdamage libxfixes`, et une étape de compilation à part (gcc, Xlib i386) pour l'agent. `build.ps1` : export, `fs2json.py --zstd`, `copy-to-sha256.py --zstd`, puis **dédoublonnage contre l'arborescence de E1** : seuls les fichiers nouveaux sont gardés (29 fichiers, 6,8 Mio) ; `server.mjs` sert tout fichier manquant depuis `essais/e1-wine-v86/images/` en lecture seule. Image complète : 3 815 fichiers, `alpine-fs.json` 304 Kio.
- `guest/e4agent.c` (≈ 330 lignes, C, Xlib + Xcomposite + Xdamage) : au démarrage `XCompositeRedirectSubwindows(root, Automatic)` ; `SubstructureNotify` sur la racine, `PropertyChange` et un objet `XDamage` par fenêtre cliente (celle qui porte `WM_STATE`, cherchée sous le cadre si un gestionnaire de fenêtres reparente). À chaque tick (20 Hz) : relecture de l'arbre si besoin (`XQueryTree`, ordre z), `XGetImage` des fenêtres marquées sales par Damage, comparaison avec la trame précédente (`memcmp`), écriture BGRX 32 bpp dans `/root/e4/w-<id>.<pair|impair>.raw` (double tampon, `fsync`), puis `windows.json` : géométrie en coordonnées racine, `mapped`, `override_redirect`, `_NET_WM_NAME`/`WM_NAME` (Latin-1 converti en UTF-8), `WM_CLASS`, `WM_TRANSIENT_FOR`, `_NET_WM_WINDOW_TYPE`, `_NET_WM_STATE`, taille de `_NET_WM_ICON` (icône écrite une fois en ARGB), `WM_HINTS.input`, numéro de trame, horodatages (dommage, capture, horloge monotone), compteurs `/proc/stat`, CPU de l'agent (`getrusage`) et du serveur X (`/proc/<pid>/stat`). Mode `geometry` : ni redirection ni capture, seulement la liste et les dommages (base du découpage).
- **Canal hôte ↔ invité : la racine 9p elle-même.** L'invité écrit dans `/root/e4/` ; la page lit avec `emu.read_file()` (le système de fichiers 9p de v86 vit en mémoire dans la page : aucune copie réseau), et injecte des fichiers avec `emu.create_file()` (utilisé pour pousser `demo.sh` et l'agent recompilé dans l'invité au démarrage). Les commandes vers l'invité (clics, déplacements, focus) passent par la console série (`xdotool`), comme dans E1.
- `boot.html` : v86 comme E1 (1 Gio, VGA 8 Mio, racine 9p, `?state=`), plus un « bureau » de 1024x768 où chaque fenêtre invité est une `div` absolue (barre de titre + `canvas`) aux coordonnées invité. Mode **composite** : lecture du `.raw` quand le numéro de trame change, conversion BGRX → RGBA, `putImageData`. Mode **découpage** : `drawImage` du canvas VGA de v86 (rectangle de la fenêtre) quand l'agent (mode `geometry`) signale un dommage sur cette fenêtre (runs 1-2 : à chaque nouveau `seq`, toutes fenêtres). Barre de titre glissable (la `div` suit la souris, puis `xdotool windowmove` ; l'agent confirme la géométrie), clic sur la toile → `xdotool mousemove x y click n` aux coordonnées invité, focus → `windowraise`/`windowfocus`, ordre z → `z-index`. Détection de changement par empreinte des pixels (1 pixel sur 8) pour la latence. Statistiques : ms de gestionnaire par seconde (lecture, dessin), trames appliquées, octets lus, CPU invité (total, agent, X).
- `guest/demo.sh` : Xorg fbdev **sans gestionnaire de fenêtres** (les clients se placent eux-mêmes ; les décorations sont celles de la page), `xterm -geometry 60x14+40+40` (titre « Terminal invité », son pty écrit dans `/root/e4-pty`), `xclock -digital -update 1`, `xeyes`, `xmessage` déclaré transient de l'xterm (`WM_TRANSIENT_FOR` et `_NET_WM_WINDOW_TYPE_DIALOG` posés par `xprop`), puis l'agent.
- `drive.mjs` : pilote interactif (serveur + Chrome headless sur profil temporaire + petit serveur HTTP de contrôle : commande série, `page.evaluate`, lecture de fichiers invité, clic/glisser Playwright, capture). `run.mjs` : mesure automatique, 2 runs. `server.mjs` : comme E1 (COOP/COEP, Range, compteur) + repli sur E1.

## Protocole

Pour chaque run, `run.mjs` fait, dans un contexte Chrome neuf (profil temporaire supprimé ensuite) :

1. **Boot à froid** de l'image E4 jusqu'au prompt ; injection par 9p de `demo.sh` et de l'agent (`images/e4agent`, compilé par `build-agent.ps1`) ; `sh /root/demo-e4.sh composite 20` → `MARK x-ready`, `xterm-sized`, `clients-ready`, `agent-started`.
2. **CPU invité de base** : `/proc/stat` sur 10 s avec X et les quatre clients, agent arrêté.
3. **Mode composite** (agent 20 Hz, sondage page 50 ms) : capture du bureau ; **repos** 20 s (seule l'horloge change, 1 Hz) ; **latence** x10 : la page envoie sur la console série `printf 'ligne i' > <pty de l'xterm>` (le texte apparaît dans l'xterm sans passer par X11 input) suivi de `echo MARK chgi <uptime>` ; on note `t_send` (envoi), `t_mark` (réception du MARK, donc fin du chemin série aller), `t_seen` (la toile de l'xterm a changé dans la page), et, côté invité, `capture_t − damage_t` de l'agent ; **déplacement** x2 (`E4.moveWindow` → `xdotool windowmove` → l'agent renvoie la nouvelle géométrie) ; **recouvrement** : l'horloge est déplacée sur l'xterm, une ligne est écrite, et on compte dans la toile de l'xterm les pixels de la couleur dominante de la toile de l'horloge (son fond `#204`, rendu (32,0,64)), et, en vérité terrain, dans le rectangle de l'xterm sur le canvas VGA ; **charge** 15 s : `while :; do echo ...; done > <pty>` (l'xterm défile sans arrêt) + horloge.
4. **Mode découpage** : agent relancé en `geometry`, page en `crop`, mêmes phases.

Les temps page sont des `performance.now()` ; les temps invité des `CLOCK_MONOTONIC` de l'agent et des uptimes imprimés dans les `MARK`.

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (8 cœurs, v86 n'en utilise qu'un, dans le fil principal de la page), 64 Go. Un autre essai (E5) tournait par moments sur la même machine.
- Chrome 156.0.8078.80 (canal installé, piloté par Playwright, headless). v86 0.5.473. Serveur sur `localhost`.
- Disque : 7,5 Go libres au départ ; le garde-fou de 4 Go s'est déclenché à la deuxième reconstruction de l'image (Docker Desktop ne rend pas l'espace), d'où l'injection de l'agent et de `demo.sh` par 9p plutôt qu'une image reconstruite.

## Résultats

Runs retenus : 3 et 4 (`results-3.json`, `results-4.json`), plus un run 5 fait seulement pour corriger la mesure « MARK → vu » en découpage (voir « ce qui a raté ») ; les runs 1 et 2 (préliminaires, mêmes ordres de grandeur, trois défauts de mesure) sont conservés. Agent à 20 Hz, page sondée toutes les 50 ms. Fenêtres : xterm 544x256 (557 Kio par trame), xclock 220x60 (53 Kio), xeyes 200x150 (120 Kio), xmessage 260x90 (94 Kio). « CPU invité » = part du CPU émulé (un seul cœur) d'après `/proc/stat` ; « page » = millisecondes de gestionnaire par seconde dans le fil principal (v86 lui-même non compris).

### Démarrage et mise en place

| Mesure | Run 3 | Run 4 |
|---|---|---|
| Boot à froid, page → prompt | 60,8 s | 60,2 s |
| Injection par 9p de `demo.sh` + agent (28 Kio) | 0,9 s | 0,9 s |
| Xorg fbdev prêt | 14,9 s | 15,7 s |
| xterm à sa vraie taille (après X) | 37,3 s | 34,8 s |
| Quatre clients + propriétés posées (après X) | 39,3 s | 36,6 s |
| Agent lancé (redirection Composite, Damage, première capture des 4 fenêtres) | 4,2 s | 4,2 s |
| CPU invité, X + 4 clients, sans agent (10 s) | 2,4 % | 2,2 % |
| CPU invité, charge (xterm qui défile) sans agent (15 s) | 87,2 % | 85,9 % |

### Au repos : seule l'horloge change (1 fois par seconde), 20 s

| Mesure | Composite r3 | Composite r4 | Découpage r3 | Découpage r4 |
|---|---|---|---|---|
| Trames appliquées dans la page | 1,0 /s (horloge seule) | 1,0 /s | 1,0 /s | 1,0 /s |
| Octets invité → page | 52,8 Kio/s | 52,8 Kio/s | 0 (JSON seul) | 0 |
| Page : gestionnaire total | 1,7 ms/s | 1,6 ms/s | 3,0 ms/s | 3,1 ms/s |
| … dont dessin (`putImageData` / `drawImage`) | 0,1 | 0,1 | 0,1 | 0,1 |
| … dont détection de changement (mesure seulement) | 0,0 | 0,0 | 1,5 | 1,6 |
| CPU invité total | 2,0 % | 1,9 % | 1,4 % | 1,4 % |
| … agent | 1,7 % | 1,6 % | 1,1 % | 1,0 % |
| … serveur X | 0,4 % | 0,4 % | 0,3 % | 0,3 % |
| Agent : `XGetImage` par trame (horloge, 53 Kio) | ≈ 6 ms | ≈ 5 ms | — | — |
| Agent : écriture 9p par trame | ≈ 7 ms | ≈ 7 ms | — | — |

Le découpage coûte 1,5 ms/s de page hors détection, c'est-à-dire autant que le composite : au repos, la différence est dans l'invité (1,7 % contre 1,1 %) et dans les 53 Kio/s.

### Sous charge : xterm qui défile sans arrêt + horloge, 15 s

| Mesure | Composite r3 | Composite r4 | Découpage r3 | Découpage r4 |
|---|---|---|---|---|
| Trames appliquées (xterm + horloge) | 2,7 /s (28 + 15) | 2,6 /s (26 + 15) | 11,3 /s (165 + 16) | 12,4 /s (182 + 16) |
| Octets invité → page | 1,0 Mio/s | 0,9 Mio/s | 0 | 0 |
| Page : gestionnaire total | 2,8 ms/s | 2,6 ms/s | 9,2 ms/s | 8,2 ms/s |
| … dessin | 1,2 | 1,1 | 0,4 | 0,4 |
| … détection (mesure) | 0,2 | 0,2 | 5,8 | 5,3 |
| JSON lus tronqués (ignorés) | 10 / 300 | 13 / 300 | 93 / 300 | 77 / 300 |
| CPU invité total (charge comprise) | 93,3 % | 94,0 % | 90,2 % | 89,2 % |
| … agent | 9,6 % | 9,7 % | 9,4 % | 8,4 % |
| … serveur X | 30,7 % | 31,6 % | 28,9 % | 27,5 % |
| Agent : événements Damage reçus | 124 | 121 | 291 | 316 |
| Agent : trames écrites (xterm ≈ 557 Kio) | 117 (26,5 Mio) | 114 (25,3 Mio) | 0 | 0 |
| Agent : `XGetImage` par trame | 43 ms | 41 ms | — | — |
| Agent : écriture 9p par trame | 130 ms | 125 ms | — | — |

Sous charge, l'agent composite ne suit plus : 2,7 trames/s de l'xterm (une trame = ≈ 43 ms de capture + ≈ 130 ms d'écriture 9p, CPU émulé), là où le découpage, qui ne fait que relire le canvas VGA sur les dommages, en applique 11-12 par seconde. Le CPU invité de l'agent est pourtant le même (≈ 9 %) : en mode géométrie, il est pris par la lecture de l'arbre et des dommages à chaque tick, et par l'écriture du JSON. Les compteurs `agent_capture` cumulent depuis le lancement de l'agent ; les valeurs par trame ci-dessus sont des différences entre le début et la fin de la phase.

### Délai : une ligne écrite dans l'xterm → son affichage dans la page (10 essais, médiane [min–max], ms)

| Mesure | Composite r3 | Composite r4 | Découpage r3 | Découpage r4 | Découpage r5 |
|---|---|---|---|---|---|
| envoi série → vu dans la page (bout en bout, chemin série compris) | 926 [784–1325] | 917 [777–1037] | 1130 [870–1669] | 1079 [897–1270] | 1143 [909–1268] |
| envoi série → MARK reçu (= coût de la console série seule) | 765 [659–1089] | 761 [638–868] | (non mesuré : défaut de marques) | (idem) | 1070 [1055–1183] |
| MARK reçu → vu dans la page (**part affichage**) | 160 [125–275] | 160 [139–187] | (non mesuré) | (non mesuré) | 85 [−157–86] (8 valeurs) |
| Invité : premier dommage → capture écrite (agent) | 279 [222–398] | 288 [213–311] | — | — | — |
| Essais non vus (changement sous le seuil de l'empreinte) | 1 | 1 | 1 | 3 | 1 |

(Run 5, composite, pour comparaison dans les mêmes conditions : 1014 [875–1079] / 840 [709–904] / 166 [142–185].) La console série (une commande de 60 caractères tapée puis exécutée par le shell) pèse ≈ 0,76 s en composite et ≈ 1,07 s en découpage (la console et le redessin du canvas VGA se partagent le fil principal de la page) ; la part « affichage » est de ≈ 160 ms en composite et ≈ 85 ms en découpage (une valeur négative : le canvas VGA avait déjà changé avant l'arrivée du MARK). Le bout en bout reste ≈ 0,1 s plus long en découpage, à cause du chemin série. Le « dommage → capture » invité (≈ 280 ms) est plus long que « MARK → vu » parce que le premier dommage de l'xterm arrive avant que le MARK ne soit imprimé (l'xterm commence à repeindre pendant que le shell écrit encore) et parce que l'xterm repeint en plusieurs fois (défilement puis texte), chaque dommage relançant une capture.

### Déplacement et recouvrement

| Mesure | Composite r3 | Composite r4 | Découpage r3 | Découpage r4 |
|---|---|---|---|---|
| `E4.moveWindow` (div déplacée tout de suite) → géométrie confirmée par l'agent, 2 déplacements | 544, 556 ms | 566, 689 ms | 720, 742 ms | 849, 776 ms |
| Horloge posée sur l'xterm : pixels de l'horloge (32,0,64) dans la **toile de l'xterm** | **0** / 139 264 | **0** | **12 066** | **12 064** |
| … dans la toile de l'horloge (témoin) | 12 085 / 13 200 | 12 097 | 12 072 | 12 063 |
| … dans le rectangle de l'xterm sur le canvas VGA (vérité terrain : l'horloge est bien dessus) | 12 085 | 12 097 | 12 087 | 12 052 |

Le composite donne la fenêtre recouverte intacte (XComposite garde son pixmap complet) ; le découpage montre la fenêtre du dessus dans la fenêtre du dessous, comme attendu. Le déplacement est confirmé ≈ 0,55-0,7 s plus tard (composite) et ≈ 0,75-0,85 s (découpage) : c'est le temps de `xdotool windowmove` par la console série plus un tick d'agent (le découpage est plus lent parce que le `ConfigureNotify` déclenche aussi un redessin complet de la fenêtre, dommage compris).

Captures : `images/e4-run3-composite.png` (quatre fenêtres séparées, titres, « Dialogue … → 0x80000c DIALOG »), `images/e4-run3-composite-overlap.png` et `images/e4-run3-crop-overlap.png` (horloge sur l'xterm), `images/e4-run3-vga.png` (écran VGA de la VM), `images/e4-interactive-drag.png` et `images/e4-interactive-click.png` (glisser par la barre de titre avec la souris Playwright, clic dans xeyes qui fait bouger les yeux). Dossier `images/` ignoré par git.

## Fenêtres Wine 1x1 (boîte de temps de 45 min, depuis l'état sauvegardé de E1)

Chaque essai repart de `essais/e1-wine-v86/images/state.bin` (Wine démarré, Notepad mappé à 1x1), restauré en ≈ 3 s.

| Piste | Fait | Résultat |
|---|---|---|
| `HKCU\Software\Wine\X11 Driver` : `UseXRandR=N` et `UseXVidMode=N`, puis `wine winemine` (nouveau processus, même wineserver) | oui | fenêtre « WineMine » mappée à **1x1** après 45 s, comme Notepad |
| Trace `WINEDEBUG=+win,+x11drv,+xrandr,+event wine winemine` (947 lignes, `/root/trace.log`, grep dans l'invité) | oui | voir ci-dessous : le pilote reçoit des rectangles 1x1 |
| Xorg relancé avec `-extension RANDR` (xdpyinfo ne liste plus RANDR), `twm`, suppression de `HKLM\System\CurrentControlSet\Control\Video`, `Enum\DISPLAY` et `HKCU\Software\Wine\X11 Driver`, `wineserver -k`, `wine notepad` | oui | « Untitled - Notepad » mappée à **1x1** après 100 s ; les clés sont recréées sans aucun `DefaultSettings.XResolution`/`BitsPerPel` |
| `xrandr --output … --primary` | non | `xrandr` absent de l'état de E1 (le paquet est dans le Dockerfile, mais l'état a été fait avant son ajout) ; sans objet une fois RandR désactivé (ligne précédente) |
| Xvfb `-extension RANDR` | non | redondant avec Xorg sans RANDR |

Ce que dit la trace (lignes citées depuis `trace.log`, hwnd/fenêtre X) :

- le côté Win32 calcule des tailles correctes : `NtUserCreateWindowEx hwnd 0x10034 cs 0,0 0x0 { window (0,0)-(119,34), client (3,29)-(116,31) … }`, `get_visible_rect … -> visible_rect (3,22)-(116,31)` ; pour le bureau `calc_winpos … new_rects { window (0,0)-(1024,768) … }` ;
- mais **winex11 reçoit des rectangles réduits à 1x1** : `X11DRV_WindowPosChanging hwnd 0x10034 … rects { window (0,0)-(1,1), client (1,1)-(1,1), visible (0,0)-(1,1) }`, puis `window_set_config … requesting config (1,1)-(1,1)` et `window_set_wm_normal_hints … { pos 1,1 min 1,1 max 1,1 }` ; pour le bureau `rects { window (0,0)-(1,1), client (0,0)-(1,1), visible (0,0)-(1024,768) }` ;
- les `Ignoring … old WM_NORMAL_HINTS …/90, expected …/103` vus dans E1 ne sont que la conséquence (les hints demandés eux-mêmes disent 1x1) ;
- `X11DRV_UpdateDisplayDevices via "Xinerama" … adapter: 0, monitor count: 1`, `xinerama_init monitor 0x0: (0,0)-(1024,768) (primary)`, `xdpyinfo` 1024x768, 271x203 mm, 96 dpi : l'écran est vu correctement, mais aucun **mode d'affichage** n'est jamais écrit dans le registre (`DefaultSettings.*` absents sous `Control\Video\{…}\0000`, seulement `GraphicsDriver`, `DriverDesc`, `HardwareInformation.*`), et `Enum\DISPLAY` ne contient que `Default_Monitor`. Dans Wine 10/11, les rectangles passés au pilote sont convertis du repère « virtuel » au repère « brut » du moniteur (`map_rect_virt_to_raw`) : un moniteur sans mode courant donne vraisemblablement ce facteur dégénéré (le DC du bureau apparaît aussi en `dc_rect (0,0)-(73728,55296)`, soit 72 × 1024x768).

Conclusion : **non résolu**, et le problème n'est pas XRandR. Piste à creuser à part (hors boîte de temps) : pourquoi `X11DRV_UpdateDisplayDevices` n'ajoute aucun mode (chemin Xinerama/« default » de winex11 11.0 i386 d'Alpine), à comparer avec un autre Wine (10.x, wine-staging, build Debian i386) et, si besoin, un `winex11` instrumenté. Le reste de l'essai utilise des clients X11 natifs.

## Ce qui a raté ou a été contourné

- **Wine 1x1 : non résolu** (section précédente) ; l'essai est fait avec des clients X11 natifs, ce qui en limite la portée (voir Limites).
- **`emu.read_file` / `emu.create_file` de v86 veulent un chemin sans barre oblique initiale** (`root/e4/x`) : avec `/root/e4/x` on obtient « File not found ». Perdu ~20 min ; enveloppe dans `boot.html`.
- **`rename()` sur le 9p de v86 laisse un instant sans fichier** : l'agent écrivait `windows.json.tmp` puis renommait, et la page lisait « File not found » une fois sur deux. Remplacé par une écriture en place (un JSON tronqué est ignoré : 0 à 95 erreurs de lecture par phase, selon la charge) et un double tampon par fenêtre pour les pixels.
- **xterm met 34 à 38 s à ouvrir sa fenêtre** au premier lancement (fenêtre d'abord mappée à 1x1 pendant que Xft/fontconfig construit son cache) : `demo.sh` attend sa vraie taille. Pendant ces secondes, un client natif a lui aussi une fenêtre 1x1 : le symptôme ressemble à celui de Wine, mais la cause est différente (xterm finit par se redimensionner).
- **Titre Latin-1** : `WM_NAME` de xterm est en `STRING` ; l'agent le convertit en UTF-8 (sinon « invit� »).
- **`WM_TRANSIENT_FOR` posé par `xprop`** est de type `CARDINAL`, pas `WINDOW` : `XGetTransientForHint` le refuse ; l'agent lit la propriété quel que soit son type. Les vrais clients (et Wine) posent le bon type.
- **Image non reconstruite après les dernières corrections** : le garde-fou disque (< 4 Go) s'est déclenché (Docker Desktop garde l'espace des couches supprimées). `run.mjs` injecte `demo.sh` et l'agent recompilé (`images/e4agent`, 28 Kio, `build-agent.ps1`) dans l'invité par 9p en ≈ 1 s. L'image contient donc une version antérieure de l'agent (sans double tampon ni Latin-1).
- **Runs 1 et 2 (préliminaires, conservés)** : trois défauts de mesure corrigés pour les runs 3 et 4 : confirmation de déplacement jamais reçue (la fenêtre cliente de xterm est à +1,+1 de la position demandée, bordure de 1 px), marques `chg<i>` réutilisées entre les deux modes (latence « mark → vu » fausse en découpage), et découpage redessinant les 4 fenêtres à chaque `seq` (jusqu'à 165 ms/s de page sous charge, dont la plus grande part dans le `getImageData` de la détection de changement, qui est une dépense de mesure, pas de l'approche). La couleur de fond de l'horloge réellement rendue n'était pas `#220044` (recouvrement compté 0 partout) : les runs 3-4 prennent la couleur dominante de la toile de l'horloge.
- `xsetroot` absent de l'image (paquet à part) : fond X par défaut ; sans conséquence.
- Comme dans E1, v86 tourne dans le fil principal de la page, pas dans un Worker.

## Limites

- **Pas de fenêtre Wine** : tout ce qui touche à ce que Wine pose vraiment (`_NET_WM_ICON`, types de fenêtres, menus override-redirect, `WM_CLASS` = exécutable, dialogues) est inféré de la documentation de winex11, pas mesuré. Les coûts par trame sont ceux de fenêtres de 220x60 à 544x256 px ; une fenêtre Win32 typique (800x600 et plus) coûte proportionnellement plus en `XGetImage` et en écriture 9p.
- Clients natifs choisis pour leur légèreté ; xclock change 1 fois par seconde, la « charge » est un xterm qui défile (cas défavorable : la fenêtre change en entier plusieurs fois par seconde).
- Une seule machine, Chrome headless seulement (pas de Firefox, Edge, Safari, pas d'affichage à l'écran), `localhost`, deux runs retenus (3 et 4) plus deux préliminaires. Un autre essai (E5) tournait par moments sur la même machine : les temps de boot peuvent en souffrir, pas les mesures relatives.
- Pas de frappe clavier dans une fenêtre (seulement clics, focus, déplacement) ; pas de redimensionnement ; pas de fenêtres override-redirect ; pas de curseur en composite ; pas de « plusieurs fenêtres d'un même programme ».
- Le canal invité → page est le 9p en mémoire de v86 (gratuit en copie, mais une écriture de 557 Kio coûte ≈ 100 ms de CPU invité sous charge) : avec un Worker et un `SharedArrayBuffer`, ou un périphérique virtio dédié, les chiffres de l'agent seraient différents.
- La latence « envoi → vu » inclut le chemin de la console série (commande tapée caractère par caractère, ≈ 0,65 à 0,95 s) ; la part affichage seule est « MARK → vu » et « dommage → capture ».
- Le CPU page est mesuré comme ms de gestionnaire par seconde (`performance.now()` autour du sondage), pas en profil complet ; le coût de v86 lui-même (100 % d'un cœur) est hors de ces chiffres.

## Ce que Kazo doit ajouter (mode fenêtre par fenêtre)

Ce que l'agent voit côté X11, et ce que Kazo devrait savoir faire avec. « Vu dans l'essai » = exercé par le prototype sur de vrais clients X11 ; « non testé » = non exercé (en général parce qu'il faudrait des fenêtres Wine).

| Besoin côté Kazo | Ce que donne X11 / l'agent | Statut | Note |
|---|---|---|---|
| Une fenêtre Kazo par fenêtre X de premier niveau, créée/détruite au fil des `MapNotify`/`UnmapNotify` | `SubstructureNotify` sur la racine, `XQueryTree`, `map_state` | vu dans l'essai | 4 fenêtres suivies ; apparition/disparition gérées (div créée/retirée) |
| Titre | `_NET_WM_NAME` (UTF-8) sinon `WM_NAME` (`STRING` = Latin-1, ou `COMPOUND_TEXT`) | vu dans l'essai | xterm sans locale UTF-8 ne pose que `WM_NAME` en Latin-1 : « Terminal invité » s'affichait « invit� » avant conversion ; Kazo doit gérer les trois encodages. Wine pose `_NET_WM_NAME` : non testé |
| Classe / programme d'origine (pour l'icône de secours, le regroupement par programme, D41) | `WM_CLASS` (`xterm.XTerm`, `notepad.exe.notepad.exe` chez Wine), `_NET_WM_PID` | vu dans l'essai (`WM_CLASS`) ; `_NET_WM_PID` non lu | chez Wine la classe est le nom de l'exécutable : utile pour relier la fenêtre au programme lancé |
| Fenêtre **sans propriétaire** (programme lancé par un autre, ou fenêtre apparue sans lancement Kazo) | rien dans X11 ne dit « qui a lancé » ; seulement `WM_CLASS`, `_NET_WM_PID`, `WM_CLIENT_LEADER` | non testé | Kazo doit accepter une fenêtre dont il ne connaît pas le lanceur (ex. boîte de dialogue de Wine, `winemenubuilder`, un installeur) et lui donner un propriétaire par défaut (la VM) |
| Icône | `_NET_WM_ICON` (liste de tailles, ARGB 32 bits) ; sinon `WM_HINTS.icon_pixmap` | vu dans l'essai (icône 2x2 posée par `xprop`, lue et écrite en `.icon`) | les clients natifs n'en ont pas ; Wine pose `_NET_WM_ICON` depuis l'icône Win32 : non testé. Kazo doit prévoir une icône de repli |
| Boîte de dialogue / fenêtre liée (transient) | `WM_TRANSIENT_FOR` (+ `_NET_WM_WINDOW_TYPE_DIALOG`) | vu dans l'essai (xmessage déclaré transient de l'xterm ; l'agent remonte `transient_for` et le type) | Kazo doit garder la boîte au-dessus de son parent, la déplacer avec lui ou non (choix), et ne pas lui donner d'entrée propre dans la liste des fenêtres. Chez Wine : non testé |
| Menus, info-bulles, listes déroulantes | fenêtres `override_redirect` (pas de gestionnaire, pas de décoration, pas de focus) ; Wine pose aussi `_NET_WM_WINDOW_TYPE_MENU`/`_POPUP_MENU`/`_TOOLTIP`/`_DROPDOWN_MENU` | non testé (aucune fenêtre override-redirect n'est apparue : les menus de xterm demandent Ctrl+clic) | l'agent les marque `override` et la page les affiche sans barre de titre ; Kazo doit les afficher **sans décoration, sans focus, au-dessus**, et les fermer avec leur parent. C'est le point le plus délicat du mode fenêtre par fenêtre |
| Type de fenêtre (normale, dialogue, utilitaire, splash, barre d'outils) | `_NET_WM_WINDOW_TYPE` | vu dans l'essai (DIALOG) | Wine pose NORMAL/DIALOG/UTILITY : non testé |
| État (plein écran, maximisée, réduite, « attention ») | `_NET_WM_STATE`, `WM_STATE` (iconique), `_NET_WM_STATE_DEMANDS_ATTENTION` | lu, mais aucun état vu (clients natifs sans gestionnaire) | non testé ; Kazo doit traduire réduire/maximiser dans les deux sens (`_NET_WM_STATE` côté X, `wmctrl`-like via l'agent) |
| Ordre z | ordre des enfants de la racine (`XQueryTree`), `XRaiseWindow` | vu dans l'essai (`z-index` suit l'ordre X ; clic → `windowraise`) | sans gestionnaire de fenêtres, l'ordre X est celui que l'agent impose ; Kazo devient le gestionnaire : à chaque clic Kazo, `XRaiseWindow` dans l'invité |
| Focus clavier | `WM_HINTS.input`, `WM_TAKE_FOCUS`, `_NET_ACTIVE_WINDOW`, `XSetInputFocus` | partiellement (clic → `xdotool windowfocus`) ; frappe clavier non testée | sans gestionnaire, le focus suit le pointeur (`PointerRoot`) : Kazo (par l'agent) doit poser le focus explicitement, et transmettre les touches à la VM seulement quand une fenêtre de cette VM est active |
| Déplacement / redimensionnement depuis Kazo | `XMoveWindow`/`XResizeWindow` (ici `xdotool windowmove`) ; retour par `ConfigureNotify` | déplacement vu dans l'essai ; redimensionnement non testé | `WM_NORMAL_HINTS` (min/max/incréments : xterm en cellules) à respecter |
| Placement initial | `WM_NORMAL_HINTS` (position demandée), `_NET_WM_WINDOW_TYPE`, `WM_TRANSIENT_FOR` (centrer sur le parent) | les clients se placent eux-mêmes (pas de gestionnaire) | Kazo doit décider où ouvrir une nouvelle fenêtre (Wine place ses fenêtres aux coordonnées Win32 ; cascade à prévoir) |
| Curseur de souris | XFixes (`XFixesGetCursorImage`) ; en découpage, le curseur est dessiné dans le framebuffer | découpage : curseur X visible dans la capture ; composite : pas de curseur (non remonté) | Kazo doit afficher le curseur de l'invité (forme) : XFixes à ajouter à l'agent, non testé |
| Fenêtres qui débordent de l'écran de la VM | l'écran X est 1024x768 : tout ce qui dépasse est coupé, dans les deux approches | non testé | en composite, une fenêtre hors écran a tout de même un pixmap complet (XComposite) ; en découpage, non. Kazo doit soit tenir l'écran X plus grand que la vue, soit confiner |
| Fermeture par Kazo | `WM_DELETE_WINDOW` (`WM_PROTOCOLS`), sinon `XKillClient` | non testé | |
| Plusieurs fenêtres d'un même programme, plusieurs programmes | `WM_CLASS`, `WM_CLIENT_LEADER`, `_NET_WM_PID` | 4 programmes à une fenêtre chacun | non testé pour « un programme, N fenêtres » |

## Chiffres bruts

- `essais/e4-fenetres/results-3.json`, `results-4.json` (runs retenus), `results-5.json` (run de correction, latence découpage), `results-1.json`, `results-2.json` (préliminaires) ; journaux `images/run.log`, `run2.log`, `run3.log`. Chaque fichier contient, par mode et par phase, les compteurs de la page (`polls`, `json_err`, `handler/read/draw/detect ms`, trames, octets), les compteurs de l'agent (`agent_capture` début/fin : trames, octets, événements Damage, ms de `XGetImage` et d'écriture), le CPU invité (`/proc/stat`, agent, X), les 10 échantillons de latence avec leurs quatre horodatages, les déplacements, le recouvrement (couleur dominante, comptes), la liste des fenêtres avec toutes leurs propriétés.
- Image : 3 815 fichiers dans `alpine-fs.json` (304 Kio) ; 29 fichiers propres à E4 (6,8 Mio zstd), 3 786 servis depuis E1. Paquets ajoutés (taille installée) : xclock 39 Kio, xeyes 26, xmessage 18, xprop 34, xwd 29, xrefresh 13, xsetroot 17, libxcomposite 13, libxdamage 13, libxfixes 21 (+ leurs dépendances Xaw/Xmu déjà présentes ou comptées dans les 6,8 Mio). Étape de compilation (gcc, musl-dev, en-têtes X11 i386) : téléchargée par apk dans une image Docker intermédiaire supprimée ensuite, taille non relevée (de l'ordre de 100 à 150 Mio installés). `npm install playwright` : 19 Mio dans `node_modules/` (pas de navigateur téléchargé : canal Chrome installé). Agent compilé : 28 340 octets.
- État sauvegardé de la VM E4 (Xorg + 4 clients + agent) pendant la mise au point : 174 Mo (contre 607 Mo pour l'état E1 avec Wine), restauration en ≈ 12 s page comprise ; supprimé à la fin de l'essai (disque).
- Trace Wine : `/root/trace.log` dans l'état E1 modifié (947 lignes), non conservée hors de l'invité ; les lignes utiles sont citées plus haut.
- Ports utilisés : 8094/8095 (serveur + contrôle, VM Wine puis runs), 8096/8097 (VM E4 interactive) ; tous fermés à la fin.

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **Le critère de l'étude est atteint côté mécanisme, pas côté Wine.** Quatre fenêtres X11 de l'invité sont affichées comme quatre fenêtres séparées de la page, déplaçables par leur barre (la fenêtre invité suit), cliquables, avec titre, classe, relation transient et ordre z, sans perte visible, en composite comme en découpage. Mais ce sont des clients X11 natifs : tant que Wine 11 laisse ses fenêtres à 1x1 (E1, et les trois pistes XRandR de cet essai sont écartées), **rien n'est démontré avec une fenêtre Windows**. À valider : traiter « Wine 1x1 » comme un chantier à part (autre Wine, trace win32u), préalable à toute décision de calendrier sur 112.
2. **Composite contre découpage : le composite coûte peu et règle le recouvrement.** Au repos (une horloge qui change 1 fois par seconde) l'agent composite coûte ≈ 1,7 % du CPU invité émulé et 1,7 ms/s dans la page, pour 53 Kio/s ; le découpage ≈ 1,1 % et ≈ 1,5 ms/s (hors détection) et 0 octet. Sous charge (xterm qui défile), l'agent composite prend ≈ 9-10 % du CPU invité (X 31 %, la charge elle-même ≈ 87 % sans agent), 1 Mio/s, et la page 2,8 ms/s ; le découpage, 9 % (agent, lecture des dommages seulement) et ≈ 3,5 ms/s page hors détection. Seul le composite donne la bonne image d'une fenêtre recouverte (0 pixel de l'horloge dans la toile de l'xterm, contre 12 066 en découpage) ; le découpage montre en plus le curseur X. Proposition : **composite (XComposite + XDamage) comme mécanisme cible**, découpage comme repli ou premier jalon. À valider.
3. **Le goulot est l'écriture des pixels, pas la capture.** `XGetImage` d'une fenêtre 544x256 coûte ≈ 35-45 ms de CPU émulé, mais l'écriture de ses 557 Kio sur le 9p ≈ 100 ms sous charge (15 s de `write` pour 117-147 trames). Un canal dédié (virtio partagé, `SharedArrayBuffer` d'un Worker, compression des lignes inchangées, ou envoyer seulement la région Damage et non la fenêtre entière) devrait diviser ce coût par plusieurs fois. Débit observé : 2,7 trames/s d'une fenêtre entière sous charge ; pour 30 trames/s d'une fenêtre 800x600 (1,9 Mio) il faudrait ≈ 55 Mio/s, hors de portée du 9p tel quel. À valider : envoyer les rectangles Damage, et mesurer avec un Worker.
4. **Délai bout en bout.** Du changement dans l'invité (fin de la commande série) à l'affichage dans la page : ≈ 160-185 ms (médiane) en composite, dont ≈ 250-330 ms « dommage → capture écrite » vus de l'invité (la capture démarre avant que le MARK ne sorte ; l'xterm repeint en plusieurs fois). En découpage ≈ 85 ms (run 5), mais le bout en bout y est ≈ 0,1 s plus long parce que la console série et le redessin du canvas VGA se partagent le fil principal. Ce délai est dominé par le CPU émulé (capture + écriture) et le sondage à 50 ms de la page, pas par le réseau. À valider : un sondage piloté par un signal (fichier de séquence ou interruption virtio) plutôt que par minuterie.
5. **Déplacer une fenêtre depuis la page** : la `div` suit la souris sans délai ; l'invité confirme la nouvelle géométrie ≈ 0,55 s (composite) à 0,73 s (découpage) plus tard, le temps que `xdotool windowmove` passe par la console série et que l'agent relise l'arbre. Avec un canal de commandes propre (pas la console série), ce serait le temps d'un `XMoveWindow` + un tick de l'agent. À valider.
6. **Kazo doit devenir le gestionnaire de fenêtres de l'invité** (tableau ci-dessus) : ordre z, focus, placement, transients, fenêtres override-redirect (menus, info-bulles) sans décoration, fenêtres sans propriétaire, icône de repli, titres en trois encodages, curseur. La partie « afficher des rectangles de pixels » est la plus simple ; la partie « se comporter comme un WM » est celle qui décide du calendrier. Proposition : tant que Kazo n'a pas ces notions, **V1 reste à une fenêtre Kazo par programme (D26 tel quel)**, et le fenêtre par fenêtre passe en V1.x avec un plan séparé : (a) débloquer Wine 1x1, (b) agent composite + Damage avec canal dédié, (c) gestionnaire de fenêtres minimal côté Kazo (les lignes « non testé » du tableau), (d) remesurer avec Notepad + une boîte de dialogue + un menu. À valider (question 112 : calendrier, pas principe).
7. **Le 9p de v86 est un bon canal de prototypage** (lecture et écriture de fichiers invité depuis la page, injection d'un binaire en 1 s), avec deux pièges : chemins sans `/` initial, et `rename` non atomique vu de la page. Pour la production, pas comme canal d'image. À valider.
8. **Sans gestionnaire de fenêtres dans l'invité, les clients natifs se placent et se dimensionnent seuls** ; Wine, lui, attend un WM (E1 : sans twm, pas de reparentage). Si Kazo est le WM, l'agent doit répondre aux attentes de winex11 (`_NET_SUPPORTING_WM_CHECK`, `_NET_SUPPORTED`, accuser les `ConfigureRequest`) : non testé, à prévoir dans le plan (c). À valider.
