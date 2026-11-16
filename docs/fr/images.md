> [English version](../en/images.md)

# Les images invitées

Jalon 0, chantier 4 (`j0/4-images`). Une image est ce que la machine virtuelle démarre : un
noyau, un initramfs et une racine pour Linux, un disque pour DOS. Ce chantier livre les recettes
qui les construisent, l'outil qui les met dans la forme publiée (morceaux de 32 Mio et manifeste,
lus par le stockage du chantier 3), le test d'image de la décision 210 A, la collecte des sources
et la publication par la CI. Aucune image ni archive n'est commise (`images/.gitignore`) : tout
est reconstruit depuis des sources officielles épinglées par leur SHA-256.

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `images/tools/pack-image.py` | l'outil de paquetage : `pack`, `check` (ou `--check`), `extract`, `info` |
| `images/tools/image-check.py` | le test d'image de 210 A : inventaire des dossiers inscriptibles et les 22 contrôles R134.5 |
| `images/tools/publish-layout.py` | la mise en forme de la publication (`images/<image>/<version>/`, `index.json`, refus au-dessus de 100 Mio pour Pages) |
| `images/tools/Dockerfile` | le conteneur `osx86-tools` (e2fsprogs, mtools, python3) que les recettes utilisent |
| `images/linux-base/` | la recette du Linux de base (201 A) : `Dockerfile`, `init.sh`, `verify-console.sh`, `modules.keep`, `osx86.modules`, `probes/`, `build-linux-base.py` |
| `images/linux-base/slim.sh` | le dégraissage commun à linux-base et à l'image Wine (inventaire des paquets et des licences compris) |
| `images/wine/` | la recette de l'image Wine (chantier 9) : `build-wine.py`, `install-wine.sh`, `winehq-trixie.sources`, `make-prefix.sh`, `prefix-copy.sh`, `trace-hot.sh`, `preload.sh`, `slim-wine.sh`, `osx86-hello.c` |
| `images/alpine-test/` | l'image Alpine de test du chantier 2, désormais paquetée ; elle embarque l'agent du chantier 5 |
| `images/agent/` | l'agent invité (chantier 5) : sources C, `Dockerfile`, `build-agent.py` ; voir [`agent.md`](agent.md) |
| `images/freedos/` | la recette FreeDOS : `build-freedos.py`, `FDAUTO.BAT` |
| `images/build/<image>/` | la sortie (ignorée par git) ; `pack/` est la forme publiée |
| `tests/OsX86.Tests/Images/` | tests `unit` des outils et test `guest` du pack Alpine |
| `.github/workflows/images.yml` | construction par la CI, artefacts, publication Pages et R2 |

```bash
python -I images/agent/build-agent.py                 # l'agent invité (Docker linux/386), voir agent.md
python -I images/alpine-test/build-alpine-test.py     # ISO officiel ; embarque l'agent s'il est construit
python -I images/freedos/build-freedos.py             # archive officielle, mtools ou osx86-tools
python -I images/linux-base/build-linux-base.py       # Docker (linux/386), 5 Go libres exigés
python -I images/wine/build-wine.py                   # part de linux-base ; ≈ 2,5 Go d'images Docker
python -I images/tools/pack-image.py check images/build/linux-base/pack
```

Python 3.14 (module `compression.zstd`) ou le paquet `zstandard` ; Docker Desktop ou dockerd
pour linux-base. Les images et conteneurs Docker créés s'appellent tous `osx86-*` ; rien d'autre
n'est touché.

## Le format : un disque brut ext4, pas une arborescence 9p

Le plan parlait d'une racine 9p (Docker → `fs.json` + fichiers `.zst`, la recette des essais
E17 et E7). Le chantier retient **un disque brut ext4 attaché comme `hda`** (IDE de v86), pour
trois raisons mesurées :

| Fait | Mesure | Source |
| --- | --- | --- |
| v86 n'a qu'un périphérique virtio-9p : une racine 9p interdit tout second montage | « no channels available » au second montage | E8 |
| Relire un programme installé coûte 3,5 à 9 fois plus sur 9p que sur ext4 | 3 000 fichiers relus : 15,7 s sur 9p, 1,7 s sur ext4 ; `stat` des 866 fichiers de Wine : 9,5 à 12,8 s | E8 |
| Le transport 9p plafonne et coûte par message | 8 à 11 Mo/s, ≈ 4 Kio par `Tread` ; racine 9p ≈ 14 s du boot de 49 s | E8, E17 |

Le dossier partagé de Kazo (D22, E8) a donc le périphérique 9p pour lui seul ; la racine est
montée en lecture seule (`root=/dev/sda rootfstype=ext4 ro`), les endroits inscriptibles sont des
tmpfs montés par l'init (`/run`, `/tmp`, `/var/tmp`, `/var/log`) ; depuis le chantier 10, `/home`
(les préfixes) est sur le disque de la VM, seul endroit inscriptible de la racine
([`programmes.md`](programmes.md#où-vivent-les-préfixes-dans-linvité-choix)). Coût : la racine décompressée est un fichier
ext4 (138 Mio pour linux-base) et non des fichiers dédoublonnés par hachage ; c'est ce que la
compression par blocs ci-dessous rattrape, puisque l'OPFS garde les octets compressés.

**linux-base démarre depuis l'OPFS depuis le chantier 6a** : le worker lit le noyau, l'initramfs
et la racine `hda` dans `image.bin` et décompresse les trames à la demande (voir
[`moteur.md`](moteur.md), « Disques et démarrage décrit par le manifeste ») ; `OSX86-READY` en 19 à
27 s, l'agent répond, et `verify-console.sh` tient ses 22 contrôles dans la VM.

### Deux défauts trouvés en démarrant l'image (chantier 6a)

- **Fins de ligne.** Une extraction Git sous Windows écrivait `osx86.modules` et `modules.keep` en
  CRLF ; `mkinitfs` lisait alors `sd_mod.ko*\r` et ne trouvait rien : l'initramfs n'avait ni
  `sd_mod` ni `ext4`, la racine ne se montait pas. La CI construit sous Linux et n'était pas
  touchée ; le pack de 70,67 Mo mesuré au chantier 5 était cette construction cassée.
  `.gitattributes` impose désormais LF à tout `images/**`.
- **Modes de `/dev`.** Le `mdev` de l'initramfs Alpine laisse chaque nœud de `/dev` en 0660 et
  linux-base n'a pas d'udev : aucun usager ne pouvait ouvrir `/dev/null`, ce qui faisait aussi
  échouer en silence les commandes de `verify-console.sh` écrites avec `2>/dev/null`. `init.sh`
  remet `/dev/null`, `zero`, `full`, `random`, `urandom`, `tty` et `ptmx` en 0666.
  `verify-console.sh` compte un processus caché par `hidepid` comme protégé et reçoit le pid de
  l'agent en troisième argument (un usager ne le voit pas sous `hidepid=invisible`).

## La forme publiée et le manifeste

`pack-image.py` sérialise les entrées (noyau, initramfs, racine ou disque) en un seul bloc, le
découpe en morceaux de 32 Mio `chunk-00000.bin`, `chunk-00001.bin`, … (cinq chiffres, le dernier
plus court) et écrit `manifest.json` et `manifest.sha256` (le SHA-256 du manifeste, publié à
côté).

Chaque entrée est un **flux zstd « seekable »** (le format `contrib/seekable_format` de zstd) :
des trames indépendantes de 1 Mio décompressé (`blockSize`), suivies d'une table de recherche
dans une trame ignorable (par trame : taille compressée et décompressée, u32 ; pied de 9 octets
avec le nombre de trames et le nombre magique `0x8F92EAB1`). Un `zstd -d` ordinaire décompresse
toute l'entrée ; le moteur, lui, lit la table depuis la fin de l'entrée et ne décompresse que les
trames qu'une lecture de secteurs touche (le zstd wasm de v86 sait le faire ; un cache de trames
décompressées suffit). L'OPFS garde les morceaux tels quels : le téléchargeur du chantier 3 n'a
pas besoin de zstd.

```json
{
  "format": 1,
  "image": "linux-base",
  "version": "0.1.0",
  "engine": {"v86": "0.5.473"},
  "size": 73045750,
  "chunkSize": 33554432,
  "chunks": [{"sha256": "…"}, {"sha256": "…"}, {"sha256": "…"}],
  "files": {
    "kernel":    {"offset": 0,        "size": 7978719,  "rawSize": 8106496,   "blockSize": 1048576, "sha256": "…"},
    "initramfs": {"offset": 7978719,  "size": 5052944,  "rawSize": 5062322,   "blockSize": 1048576, "sha256": "…"},
    "rootfs":    {"offset": 13031663, "size": 60014087, "rawSize": 138412032, "blockSize": 1048576, "sha256": "…"}
  },
  "boot":  {"kind": "linux", "cmdline": "root=/dev/sda rootfstype=ext4 ro console=ttyS0 …", "console": "serial0",
            "disks": {"hda": "rootfs"}, "readyMarker": "OSX86-READY", "prompt": "osx86# ", "memoryMiB": 256,
            "agent": {"path": "/usr/lib/osx86/agent", "port": "virtio0", "consolePort": "virtio1", "protocol": 1}},
  "build": {"recipe": "…", "sizes": {…}}
}
```

- Le morceau *i* est l'intervalle `[i × chunkSize, min((i + 1) × chunkSize, size))` du bloc ;
  `size` est la somme des morceaux ; les entrées se suivent sans trou et couvrent le bloc.
- `files` porte les noms `kernel`, `initramfs`, `rootfs` (Linux) ou `disk` (DOS). `offset` et
  `size` sont dans le bloc stocké ; `rawSize` est la taille décompressée ; `sha256` celui du
  contenu décompressé.
- `boot` et `build` sont facultatifs et à l'usage du moteur et de la provenance ; un lecteur
  ignore les clés qu'il ne connaît pas. Le schéma fait foi pour le chantier 3 et ne change pas
  sans accord.
- `check` recalcule tout : taille et SHA-256 de chaque morceau, table de chaque entrée,
  décompression et SHA-256 du contenu, `manifest.sha256`.

## Les recettes

### linux-base (201 A)

Quatre étapes Docker, toutes en `linux/386`, plus l'image de l'agent (`images/agent`, construite
d'abord par `build-linux-base.py`) dont l'étape `base` copie les deux binaires :

1. **kernel** : `alpine:3.24`, `apk fetch linux-virt busybox musl` (noms de fichiers épinglés,
   SHA-256 conservés dans `/usr/lib/osx86/provenance`), le noyau `linux-virt` 6.18.63-r0 extrait
   du paquet, l'initramfs officiel d'Alpine refait par `mkinitfs -F "base osx86"` (la feature
   `osx86.modules` ajoute `sd_mod`, les pilotes ATA et ext4 : `ata_piix` est intégré au noyau,
   `sd_mod` et `ext4` sont des modules), et les modules de `modules.keep` avec leurs dépendances
   (56 fichiers sur 868, décompressés parce que le `kmod` de Debian ne lit pas les `.ko.gz`,
   essai E17).
2. **probes** : deux programmes C statiques des contrôles R134.5 (`osx86-rawsock`,
   `osx86-ptrace`), parce que l'image n'a pas de compilateur.
3. **base** : `debian:trixie-slim` i386 plus `kmod` et `procps` (installés par `docker run` puis
   `docker commit` dans le script, de même que `gcc` pour l'étape `probes` : aucun `apt-get` dans
   une étape de construction, voir « Publication »), le noyau, l'initramfs et les
   modules, `init.sh` en `/sbin/init`, `verify-console.sh`, l'agent du chantier 5
   (`/usr/lib/osx86/agent`, root, 0755, lancé par l'init ; `/usr/bin/osx86-open-url`), le
   durcissement. Le chantier 9 (Wine) part de cette étape.
4. **slim** : `apt`, `perl-base` et leurs bibliothèques purgés, pages de manuel, locales,
   convertisseurs `gconv` (sauf Unicode, CP437, CP1252, Latin-1), fuseaux (sauf UTC) et base
   dpkg retirés ; les fichiers `copyright` restent. L'inventaire des paquets
   (`packages.tsv`, `licenses.tsv`) est écrit à cette étape, après la purge.

`build-linux-base.py` construit et vérifie l'agent, exporte le conteneur (`docker export`),
vérifie que l'export porte exactement ce binaire (SHA-256, root, 0755), lance dans `osx86-tools`
l'extraction avec propriétaires et modes, le test d'image, puis `mkfs.ext4 -d` (avec un journal de
8 Mio et 256 Mio de place libre pour les préfixes depuis le chantier 10, UUID et graine fixes, `SOURCE_DATE_EPOCH`), écrit `sources.json`, paquette et mesure.

**Init** (`init.sh`, PID 1 après l'initramfs) : montages (`/proc` avec `hidepid`, `/dev`
devtmpfs, `/dev/pts`, `/dev/shm`, `/run` en `mode=0755`, `/tmp` et `/var/tmp` en 1777 `nosuid`,
`/var/log`, `/home` `nosuid,nodev`, cgroup v2 pour 206 A), `chmod 755 /`, ports `ttyS0` et
`hvc0` à `hvc3` en 0600, `/dev/fb0` et `/dev/input/*` en 0600, sysctls (`user.max_user_namespaces=0`,
`kernel.yama.ptrace_scope=1`, `dmesg_restrict`, `kptr_restrict`, liens protégés), marqueur
`OSX86-GUEST …` puis `OSX86-READY` sur la console série, l'agent s'il existe, et un shell root
sur `ttyS0` (invite `osx86# `, repli de 212 A).

**Durcissement dans la recette (210 A)** : `/` en 0755, aucun programme setuid ni setgid (bits
retirés de `su`, `mount`, `passwd`… : la VM est pilotée par l'agent en root), `/root` en 0700,
`HOME_MODE` et `DIR_MODE` à 0700 pour les usagers des préfixes, `/run/lock` supprimé (le test
d'image l'avait trouvé en 1777).

### wine (chantier 9 ; 201 A, 152 A, 204 A)

L'image Wine est linux-base plus Wine : elle part de l'étape `base` de linux-base (image Docker
`osx86-linux-base-full`, construite par `build-linux-base.py`, que `build-wine.py` importe au lieu
de le recopier) et finit par le même dégraissage (`images/linux-base/slim.sh`, partagé). Toutes les
étapes qui exécutent du code 32 bits passent par `docker run` puis `docker commit` (le profil
seccomp des étapes `RUN` de BuildKit refuse `socket(2)` aux conteneurs 32 bits sur les runners
GitHub ; `wineserver` aussi a besoin d'un socket) ; images et conteneurs s'appellent `osx86-wine-*`
et `osx86-mingw`.

1. **Téléchargements épinglés** dans `images/build/wine/download/` (dossier à part) : la clé du
   dépôt WineHQ (`winehq.key`, SHA-256 épinglé ; empreinte `D43F 6401 4536 9C51 D786 DDEA 76F1
   A20F F987 672F`), `wine-mono-10.4.1-x86.tar.xz` et `wine-gecko-2.47.4-x86.tar.xz` (SHA-256
   épinglés). Mono 10.4.1 et Gecko 2.47.4 sont les versions que Wine 11.0 demande : son
   `dlls/appwiz.cpl/addons.c` épingle le SHA-256 de leurs `.msi`, et les `.msi` publiés à côté des
   archives ont été vérifiés contre ces valeurs au moment d'épingler les archives.
2. **`install-wine.sh`** (`osx86-wine-installed`) : `ca-certificates`, la clé et
   `winehq-trixie.sources` (dépôt `https://dl.winehq.org/wine-builds/debian`, suite `trixie`,
   i386 seulement, `Signed-By` sur la clé), puis `winehq-stable`, `wine-stable` et
   `wine-stable-i386` **= 11.0.0.0~trixie-1** (vérifié après coup), plus `libfreetype6`,
   `libfontconfig1` et `libgnutls30t64` (polices de gdi32, bcrypt et TLS) ; Mono et Gecko sont
   **dépaquetés une fois** sous `/usr/share/wine/mono/wine-mono-10.4.1` et
   `/usr/share/wine/gecko/wine-gecko-2.47.4-x86`, où Wine les cherche avant de proposer un
   téléchargement : installation partagée, jamais copiée dans un préfixe. Les `.deb` de WineHQ ne sont pas
   épinglés un à un : apt les vérifie contre le `Release` signé par la clé épinglée, et la version
   est imposée (comme les paquets Debian de linux-base).
3. **`osx86-hello.c`** compilé par le MinGW-w64 de Debian (`gcc-mingw-w64-i686-win32`, conteneur
   amd64 `osx86-mingw`) : le programme console du test (il affiche ses arguments, le dossier
   Windows et l'usager, et rend le code 7) ; aucun binaire tiers.
4. **`make-prefix.sh`** (`osx86-wine-prefix`) : la **bouteille gabarit** (152 A), voir plus bas.
5. **`trace-hot.sh`** (conteneur jetable) : la liste des **fichiers chauds** (204 A), voir plus bas.
6. **`slim-wine.sh`** (`osx86-wine`) : dégraissage, pièces de l'image, puis `slim.sh` de
   linux-base (inventaire des paquets et des licences, purge d'`apt` et de `perl-base`, pages de
   manuel, locales…).
7. Export, test d'image de 210 A et ext4 dans `osx86-tools` (les fonctions de linux-base),
   vérifications propres à Wine sur l'export, `sources.json`, paquetage.

`--from prefix` ou `--from slim` reprend à l'étape 4 ou 6 sur les images Docker d'une construction
précédente (mise au point) ; `--skip-docker-build` réutilise `osx86-wine` tel quel.

**Dégraissage.** `wine-stable-i386` dépend en dur de GStreamer, PulseAudio, SANE, libgphoto2,
OpenCL, libpcap, Wayland et ALSA-plugins : 201 paquets et 1 Gio installés par apt. Ces
dépendances ne servent qu'à des modules de Wine qu'un programme console n'ouvre pas
(`winegstreamer`, `winepulse`, `sane`, `gphoto2`, `opencl`, `wpcap`, `winewayland`) : elles sont
purgées, ainsi que tout ce qui n'est plus dans la fermeture des dépendances de ce qui reste
(calculée par `apt-cache depends --recurse` : la base de linux-base, Wine, ses autres dépendances
dures et les trois bibliothèques choisies). Ces modules échouent alors à se charger, comme Wine le
fait sur un système sans ces bibliothèques. Partent aussi les outils de développement de Wine
(`winegcc`, `winebuild`, `widl`, `wrc`, `wmc`, `winedump`, `winemaker`…) et ses bibliothèques
d'import `.a`, ainsi que `ca-certificates`, `xz-utils`, la clé et la source apt. Les bits setuid et
setgid des paquets ajoutés sont retirés (210 A). Restent les bibliothèques X (`libx11-6`,
`libxext6`) pour les jalons graphiques, `libasound2`, `libudev1` et `libusb` (petites).

**La bouteille gabarit (152 A).** `wineboot -u` tourne dans le conteneur, en natif, comme root,
avec `WINEPREFIX=/usr/lib/osx86/wine/prefix-template`, `HOME=/root` (aucun dossier XDG : les
dossiers du profil sont de vrais dossiers) et l'usager Windows **`osx86-template`** ; jamais chez
l'usager. Ensuite :

- tout fichier du gabarit dont les octets sont ceux d'un fichier de Wine, de Mono ou de Gecko
  (les DLL intégrées que `wineboot` copie dans `system32`, les fichiers NLS, les polices…) devient
  un **lien symbolique** vers ce fichier : le gabarit ne garde que ce que `wineboot` a vraiment
  écrit (registre, quelques talons) ; Wine ne lit que l'en-tête d'une DLL intégrée de `system32`
  avant de charger la sienne, et un programme qui voudrait l'écraser reçoit `EACCES` (elle
  appartient à l'image) ;
- `.update-timestamp` doit valoir la date de modification de
  `/opt/wine-stable/share/wine/wine.inf` (Wine compare les deux à chaque démarrage et relance un
  `wineboot -u` complet s'ils diffèrent ; `wineboot` écrit le nombre suivi de CR LF) : vérifié
  dans le conteneur, dans l'export, **dans l'ext4** (`debugfs`, ajouté au conteneur `osx86-tools`)
  et dans la VM par le test `guest`. Premier essai dans la VM : `mkfs.ext4` avec
  `SOURCE_DATE_EPOCH=1` (réglage de linux-base pour une image reproductible) ramenait toutes les
  dates à 1970, Wine voyait un `wine.inf` « changé » et refaisait la bouteille au premier lancement
  (`rundll32 setupapi,InstallHinfSection DefaultInstall … wine.inf`, plusieurs minutes). L'image
  Wine passe donc à `mkfs.ext4` la date de `wine.inf` comme `SOURCE_DATE_EPOCH` : les dates plus
  récentes y sont ramenées, celle de `wine.inf` et les plus anciennes restent ; la valeur change
  avec chaque version de Wine, ce qui garde la détection d'une mise à jour de Wine ;
- Mono est enregistré comme installation partagée (paquet de support seulement) ; Gecko est
  trouvé à la demande dans `/usr/share/wine/gecko`.
- `winemenubuilder` est coupé dans le registre du gabarit (`DllOverrides`) : la trace du premier
  lancement le montrait écrire des fichiers `.desktop`, des types MIME et des icônes dans `~/.local`
  à chaque début de session, pour un bureau que la VM n'a pas (BoxedWine lit les raccourcis
  lui-même, BwLib) ; sa valeur `RunServices` est retirée (sans quoi `wineboot` tente de
  le lancer à chaque session).

**Copie par préfixe.** `/usr/lib/osx86/wine/prefix-copy <usager> [<destination>]` (root) copie le
gabarit pour l'usager invité `osx86-<préfixe>` (par défaut dans `$HOME/.wine`, qui ne doit pas
exister) : `cp -a` (les liens restent des liens : quelques Mio écrits), renommage de
`drive_c/users/osx86-template` et de l'usager dans `system.reg`, `user.reg` et `userdef.reg`
(Wine prend le nom de l'usager Windows dans `$USER` ; sans ce renommage, `%TEMP%` et les dossiers
du profil pointeraient vers `osx86-template`), puis `chown` à l'usager (Wine refuse un préfixe qui
n'est pas à lui). Depuis le chantier 10, `/usr/lib/osx86/prefix.d/50-wine` l'appelle à la création de chaque préfixe
(`prefix-admin`, [`programmes.md`](programmes.md)), et `/usr/lib/osx86/wine/prefix-session` démarre
la session Wine d'un préfixe (`wineserver` et services dans un lancement à part). Après un
changement de version de Wine dans l'image, un préfixe copié de l'ancien gabarit garde des liens
vers des fichiers de la nouvelle version : à traiter avec la migration des préfixes.

**Fichiers chauds et préchargement (204 A).** `trace-hot.sh` donne une copie du gabarit à un usager
`osx86-trace`, puis trace par `strace` (installé dans ce conteneur jetable seulement) ce qu'une
première session fait : `wine cmd /c echo`, puis `osx86-hello.exe`. Chaque fichier ordinaire
ouvert ou exécuté avec succès est gardé, dans l'ordre de son premier usage, ceux de la copie
ramenés au gabarit et les liens résolus ; la liste est filtrée après le dégraissage et rangée dans
`/usr/lib/osx86/wine/hot-files.list` (publiée aussi avec le pack). Le crochet
`/usr/lib/osx86/init.d/50-preload-wine`, lancé par l'init avant `OSX86-READY`, lit ces fichiers en
arrière-plan, en `nice 10`, et écrit `OSX86-PRELOAD done files=… start=… end=…` sur la console
série : l'invité les garde dans son cache de pages, et le premier `wine` lit la mémoire au lieu de
trames décompressées une à une. `osx86.preload=off` sur la ligne de commande du noyau le coupe
(pour mesurer sans). L'init de linux-base lance désormais, dans l'ordre, les exécutables de
`/usr/lib/osx86/init.d/` (avec la ligne de commande du noyau en arguments) et lit le nom de
l'image dans `/etc/osx86-image`.

**Manifeste.** `boot.memoryMiB` vaut 512 (E17 : l'invité utilise 142 Mio avec trois fenêtres Wine)
et `boot.wine` donne `prefixTemplate`, `prefixCopy`, `templateWindowsUser`, `hotFiles` et
`preloadOff` ; `build` porte les versions de Wine, Mono et Gecko et les mesures.

### alpine-test

ISO officiel 3.22.8, lecteurs ISO 9660 et cpio maison, `init.sh` en `/init` ;
`build-alpine-test.py` appelle `pack-image.py` et écrit
`images/build/alpine-test/pack/` (entrées `kernel` et `initramfs`, `boot.json`, `sources.json`).
Le `manifest.json` de `images/build/alpine-test/` reste la description de construction que lit
la page de test du chantier 2.

Depuis le chantier 5, la recette ajoute à l'initramfs l'agent de `images/build/agent/` quand il
est construit (le même binaire statique que dans linux-base ; `--require-agent` échoue s'il
manque, sinon l'image est faite sans lui), `/usr/bin/osx86-open-url` et deux usagers de préfixe
(`osx86-test`, `osx86-other`). L'init monte cgroup v2 et `/tmp`, passe `/` en 0755 (la racine de
l'initramfs est en 0700), ferme `ttyS0` et `hvc0` à `hvc3` (0600), crée les homes en 0700 et
lance l'agent sur `hvc0` ; `hvc2` et `hvc3` restent libres pour les tests du moteur. C'est
l'image du test `guest` de l'agent du chantier 5 ; linux-base a le sien depuis le chantier 6a.

### freedos (214 A, E14)

`FD14-LiteUSB.zip` de FreeDOS 1.4 (SHA-256 épinglé, celui de `verify.txt`), `FD14LITE.img`
(32 Mio, MBR et une partition FAT16) modifié par mtools, jamais démarré : `FDAUTO.BAT` remplacé
(invite texte au lieu de l'installateur, `FDAPM APMDOS` pour l'oisiveté, `OSX86-READY` à
l'écran), `SETUP.BAT` retiré. Entrée `disk`, `boot.kind` = `dos`, affichage texte VGA lu à
33 ms (214 A). mtools natif s'il est dans le PATH, sinon le conteneur `osx86-tools`.

## Le test d'image (210 A)

`image-check.py` lit l'arborescence exportée (un dossier extrait en root, ou un tar de
`docker export`) et l'init, et imprime un rapport (`image-check.json`, publié avec le pack) ; il
échoue si un contrôle casse. Il applique :

- l'inventaire de 210 A : dossiers inscriptibles par tous (seuls `/tmp` et `/var/tmp`, collants),
  `/` en 0755, `/run` monté `mode=0755`, `/dev/fb0` et `/dev/input/*` en 0600 dans l'init ;
- les **22 contrôles R134.5 d'E7**, dans leur contrepartie statique : les 22 ont été mesurés
  depuis une console d'usager dans la VM (`verify-console.sh`, gardé dans l'image à
  `/usr/lib/osx86/` avec ses deux sondes pour les tests `guest` des chantiers suivants) ; ici
  chaque contrôle regarde ce qui le garantit dans l'arbre et l'init : espaces de noms
  (`user.max_user_namespaces=0`, pas de `unshare` setuid), autre préfixe (`/root` et `/home/*`
  en 0700, `HOME_MODE`, `/tmp` collant), ports (`ttyS0`, `hvc0` à `hvc3` en 0600), socket brut,
  `chown`, `kill`, `nsenter` (aucun setuid, aucune capacité de fichier), `nosuid` sur `/home` et
  `/tmp`, `ptrace` (`yama` à 1), `/proc` avec `hidepid`, agent lancé en root, cgroup2 monté
  `nosuid,nodev,noexec` et jamais `chown` ;
- `agent_files_root_only` (chantier 5) : quand l'image porte l'agent, `/usr/lib/osx86/agent` et
  `/usr/bin/osx86-open-url` sont des fichiers exécutables de root que personne d'autre ne peut
  écrire ; une image sans agent ne casse rien (212 A). L'inventaire les liste (`inventory.agent`).

Les tests `unit` d'`ImageCheckTests` construisent un tar synthétique avec le vrai `init.sh` et le
cassent de sept façons (setuid, `/run/lock` inscriptible, init sans sysctls ni options de
montage, `/root` ouvert, agent inscriptible par tous, agent non exécutable) pour vérifier que
chaque fuite est vue.

## Sources et licences (plan, section 12)

Chaque pack publie `sources.json` à côté de l'image :

- **Debian** : pour chaque paquet binaire de linux-base, le paquet source, sa version exacte,
  les licences lues dans son fichier `copyright` (gardé dans l'image sous
  `/usr/share/doc/<paquet>/copyright`), le drapeau copyleft (GPL, LGPL, MPL…) et **l'adresse
  exacte sur `snapshot.debian.org`** (`https://snapshot.debian.org/package/<source>/<version>/`,
  où sont le `.dsc`, le `.orig.tar` et le `.debian.tar`). Choix fait plutôt qu'`apt-get source` :
  les sources de la base (glibc, coreutils, bash, util-linux…) pèsent plusieurs centaines de
  Mio par image et ne tiendraient ni dans un artefact léger ni sur Pages ; les adresses de
  `snapshot` sont stables et désignent exactement ce qui est installé. Une option de
  téléchargement pourra s'ajouter au moment de la publication R2 si Kazo le demande.
- **Alpine** : pour `linux-virt` (noyau, GPL-2.0), `busybox` (GPL-2.0, dans l'initramfs) et
  `musl` (MIT) : la version installée et le SHA-256 du `.apk`, l'APKBUILD d'aports (branche
  `3.24-stable`, lu sur le miroir GitHub parce que `gitlab.alpinelinux.org` répond 418 à un
  client sans navigateur) et les archives amont qu'il nomme avec leur SHA-512 ;
  `--fetch-sources` les télécharge dans `images/build/linux-base/sources/` (noyau ≈ 150 Mio :
  pour R2, jamais pour Pages).
- **wine** : tout ce qui précède pour la base, plus :
  - **Wine** (LGPL-2.1 ou ultérieure, fichier `copyright` des paquets WineHQ gardé dans l'image) :
    le paquet source WineHQ dont viennent les binaires (`wine_11.0.0.0~trixie-1.dsc`,
    `.orig.tar.gz`, `.debian.tar.xz` du dépôt `dl.winehq.org/wine-builds/debian/pool/main/w/wine/`)
    et l'archive amont `wine-11.0.tar.xz` ; les paquets WineHQ ne sont pas sur
    `snapshot.debian.org` (leur ligne de `debian.packages` renvoie au dépôt WineHQ) ;
  - **Mono 10.4.1** : licences mêlées, lues dans le `COPYING` de wine-mono (étiquette
    `wine-mono-10.4.1`) : MIT X11 pour l'essentiel ; parties de Mono sous LGPL ou GPL avec des
    exceptions (dont SharpZipLib, GPL avec exception ; le compilateur VB sous LGPL v2) ; FNA sous
    MS-PL et MIT ; FAudio, FNA3D, MojoShader et SDL sous zlib ; `jay` sous BSD ; winforms, wpf,
    System.Speech sous MIT. **Copyleft (LGPL, GPL) : sources à publier**,
    `wine-mono-10.4.1-src.tar.xz` (357 Mio) ;
  - **Gecko 2.47.4** : Gecko de Mozilla sous **MPL 2.0** avec les autres licences listées dans
    `toolkit/content/license.html` de ses sources (le `LICENSE` de wine-gecko y renvoie) ; aucun
    droit sur les marques de Mozilla. **MPL : sources à publier**, `wine-gecko-2.47.4-src.tar.xz`
    (197 Mio) ;
  - `osx86-hello.exe` : source du dépôt (`images/wine/osx86-hello.c`), compilateur noté.

  Les archives binaires de Mono et de Gecko n'embarquent aucun texte de licence : c'est un
  prérequis de la première publication (plan, section 12) de publier à côté de l'image les
  sources de Wine, Mono et Gecko et ces textes. `build-wine.py --fetch-sources` télécharge les
  sources de Wine, Mono, Gecko et des composants Alpine dans `images/build/wine/sources/` et note
  leur SHA-256 (aucune empreinte amont n'existe pour elles) : pour R2, jamais pour Pages.
- **alpine-test** : les aports `3.22-stable` (`main/linux-lts`, `main/busybox`, `main/musl`) et
  l'ISO ; **freedos** : l'archive officielle, les licences par paquet du dossier `PACKAGES` du
  disque et les sources de la distribution 1.4 sur ibiblio.

## Publication (202 A)

`images.yml` tourne sur tout changement de `images/**`, des tests d'images ou du workflow (les jobs
qui construisent ou lancent des conteneurs 32 bits, agent, alpine-test et linux-base, réparent
d'abord, par `.github/scripts/repair-i386-seccomp.sh`, la règle `socket` du profil seccomp du démon Docker du runner, qui refuse
`socket(2)` à tout conteneur 32 bits, mesuré par `strace` : profil officiel épinglé, règle rendue
inconditionnelle, rien d'autre ne change ; les étapes `RUN` de BuildKit gardent leur propre profil,
d'où les installations apt par `docker run`) :
cinq jobs construisent et vérifient l'agent (compilation, `--check`, test de fumée), alpine-test,
freedos, linux-base (Docker sur le runner, i386 natif) et les tests des outils, et gardent chaque pack en artefact. Sur un push vers
`j0/base` ou `main` :

- **GitHub Pages** reçoit les petites images de test (alpine-test, freedos) sous
  `images/<image>/<version>/` avec `images/index.json`, par `publish-layout.py`, qui refuse tout
  fichier de plus de 100 Mio et un site de plus de 900 Mio. Le job ne tourne que si la variable
  de dépôt `OSX86_PUBLISH_PAGES` vaut `true` ; **Pages n'est pas activé sur le dépôt** (réglage
  à faire par le mainteneur : source « GitHub Actions »), rien n'a été activé par ce chantier.
- **R2** derrière un domaine Kazo reçoit toutes les images (`rclone copy`, chemins versionnés,
  `Cache-Control: public, max-age=31536000, immutable`) si la variable `OSX86_R2_BUCKET` et les
  secrets `OSX86_R2_ACCOUNT_ID`, `OSX86_R2_ACCESS_KEY_ID`, `OSX86_R2_SECRET_ACCESS_KEY` existent.
  Aucun n'est défini : l'origine R2 n'a pas encore été obtenue de Kazo, le job n'a jamais tourné.
  La politique CORS du bucket devra exposer `Content-Range`, `ETag` et `Content-Length` (E3).

## Tailles mesurées (zstd niveau 19, trames de 1 Mio)

| Image | Décompressé | Pack zstd | Morceaux | Note |
| --- | --- | --- | --- | --- |
| alpine-test | noyau 7,70 Mo + initramfs 7,66 Mo | **15,21 Mo** | 1 | le bzImage est déjà compressé ; avec l'agent (+73 Ko) |
| freedos | disque 33,55 Mo | **17,25 Mo** | 1 | le disque contient 17 Mio de paquets zip |
| linux-base | noyau 8,11 Mo + initramfs 5,06 Mo + ext4 138,41 Mo (export 102,49 Mo) | **73,07 Mo** | 3 | sans Wine ; 56 modules noyau ; avec l'agent ; remesuré au chantier 6a (les 70,67 Mo du chantier 5 venaient d'une construction en CRLF, initramfs sans `sd_mod` ni `ext4`) ; dégraissage : voir `measurements.json` |
| wine | noyau 8,11 Mo + initramfs 5,06 Mo + ext4 1 304,43 Mo (1 244 Mio ; 1,13 Go de fichiers : Wine 663,2 Mo, Mono 236,2 Mo, Gecko 109,3 Mo, gabarit 2,1 Mo) | **365,84 Mo (348,9 Mio)** | 11 | chantier 9 ; objectif 250 Mio : écart ≈ 99 Mio, voir ci-dessous |

**Image Wine et objectif de 250 Mio (201 A).** Le pack fait 348,9 Mio, soit ≈ 99 Mio de plus que
l'objectif (E17 : 380 Mio avec Debian complet, `apt`, 130 Mio de modules, sans Mono ni Gecko).
Le dégraissage a retiré 176 paquets et les outils de développement de Wine ; ce qui reste est
pour l'essentiel Wine lui-même (DLL PE intégrées), Mono et Gecko, que 201 A et le plan
(section 1) mettent dans l'image. La répartition compressée est ci-dessous.

| Partie (zstd 19, un flux par partie) | Brut | Compressé |
| --- | --- | --- |
| DLL PE de Wine (`/opt/wine-stable/lib/wine/i386-windows`) | 645,8 Mo | 143,0 Mo |
| Mono (`/usr/share/wine/mono`) | 238,4 Mo | 63,7 Mo |
| Gecko (`/usr/share/wine/gecko`) | 109,3 Mo | 44,8 Mo |
| bibliothèques i386 de Debian (`/usr/lib/i386-linux-gnu`) | 41,5 Mo | 14,0 Mo |
| reste de Wine (`i386-unix`, `share`) | 17,4 Mo | 3,6 Mo |
| modules du noyau | 4,8 Mo | 1,4 Mo |
| noyau + initramfs (entrées du pack) | 13,2 Mo | 13,0 Mo |

La somme fait ≈ 284 Mo ; le pack en fait 365,8 : le reste est la base Debian, la bouteille et
surtout le coût des trames indépendantes de 1 Mio (chaque trame repart sans dictionnaire).
Pistes pour l'objectif, non prises ici parce qu'elles changent ce que 201 A met dans l'image ou le
format du chantier 4 : Gecko et Mono en paquets à part téléchargés à la demande (≈ 109 Mo), des
trames plus grandes pour la racine (moins de pertes, mais plus d'octets lus par accès), ou retirer
des DLL que la console n'ouvre jamais (Direct3D, `d3dcompiler`…, à mesurer par une trace plus large).
Les mesures sont dans `images/build/<image>/measurements.json` et dans `manifest.build.sizes`.

### Mesures de l'image Wine (chantier 9, 204 A)

Test `guest` `WineGuestTests`, en local (Windows 11, Ryzen 7 3700X, Chrome headless par
Playwright, pack copié dans l'OPFS, racine `hda` décompressée à la demande, 512 Mio de mémoire,
couche jetable). Temps comptés depuis l'invite série ; chaque ligne est un démarrage à froid ; la
copie du gabarit comprend l'ajout de l'usager en mémoire par la console série :

| Démarrage | Invite | Gabarit copié | `wine cmd /c echo` (lui-même) | `osx86-hello.exe` (lui-même) | **Premier programme** |
| --- | --- | --- | --- | --- | --- |
| sans préchargement | 15,2 s | 4,3 s | 12,4 s (8,0 s) | 14,6 s (2,2 s) | **14,6 s** |
| préchargement, programme tout de suite | 15,2 s | 5,5 s | 15,1 s (9,6 s) | 17,1 s (2,0 s) | **17,1 s** |
| préchargement, programme après sa fin | 15,3 s | 4,2 s | 11,1 s (6,8 s) | 13,3 s (2,2 s) | **13,3 s** |

Un passage précédent (copie du gabarit faite en place, sans dossier intermédiaire sous `/run`)
donnait 13,3, 13,9 et 12,2 s ; l'écart d'une passe à l'autre est de l'ordre de 1 à 3 s.

- **Critère de 204 A atteint** : premier programme console 12 à 17 s après l'invite (objectif :
  moins de 60 s), sans état gabarit ; 152 A n'a pas à être reposée.
- **Le préchargement gagne peu** : ≈ 1,2 s sur `wine cmd` quand il a fini avant (224 fichiers,
  108 Mo lus en 1,8 s d'uptime invité quand rien d'autre ne tourne), et coûte 1 à 2 s quand il
  tourne en même temps que le premier programme (11 à 14 s de lecture en concurrence). Le coût de 62 à 79 s d'E17
  venait de la racine 9p ; avec la racine ext4 lue depuis l'OPFS (chantier 6a), le premier `wine`
  est déjà à 8,6 s. Le préchargement reste dans l'image (gain mesuré, aucun coût visible sur
  l'invite) ; `osx86.preload=off` le coupe.
- Ce qu'il reste dans `wine cmd` : le démarrage de `wineserver`, `wineboot --init` (services,
  `plugplay`, `winedevice`), puis `cmd`. Le gabarit n'est jamais refait dans la VM (vérifié :
  `.update-timestamp` égal à la date de `wine.inf` dans l'invité).
- **Le `wineserver` du préfixe doit vivre à part.** Un `wineserver` lancé par une commande
  `exec-in-prefix` appartient au cgroup de cette commande, que l'agent termine 2 s après sa fin
  (SIGHUP puis `cgroup.kill`, 208 A) : une seconde commande qui s'y connecte dans ce délai reste
  bloquée (480 s dans un premier essai). Le test lance `wineserver -f -p` comme lancement propre
  avant les commandes ; le chantier 10 décide comment un préfixe garde le sien.
- En natif (Docker), `wine cmd` prend 1,2 à 1,5 s et `osx86-hello.exe` 1 s : la VM coûte un
  facteur 6 à 8.
- Non mesuré : Firefox, Safari, une machine plus lente, Mono et Gecko en usage (aucun programme
  .NET ni HTML lancé). La CI imprime les mêmes lignes dans le job `guest-wine`.

## Tests

- `dotnet test --filter "Category=unit"` : `PackImageTests` (morceaux, schéma, `check` qui
  détecte un octet changé et un manifeste modifié, `extract`, refus des mauvais arguments),
  `ImageCheckTests` (tar synthétique, 22 contrôles, sept cassures). Ils lancent Python avec `-I`
  ; sans Python 3.14 ni `zstandard` ils sont ignorés en local et échouent dans la CI.
- `WineGuestTests` (`guest`) démarre le pack Wine trois fois et lance `wine cmd /c echo` et
  `osx86-hello.exe` par `exec-in-prefix` (voir « Mesures de l'image Wine ») ; ignoré si le pack
  n'est pas construit, échoue dans le job `guest-wine` (`OSX86_REQUIRE_PACKS`).
- `dotnet test --filter "Category=guest"` : `PackedAlpineGuestTests` vérifie le pack Alpine,
  l'extrait et le démarre dans le worker jusqu'à l'invite série (9 s dans Chrome).
- La CI (`ci.yml`, `guest-tests.yml`) installe Python 3.14 ; `images.yml` construit les images.
