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
| `images/alpine-test/` | l'image Alpine de test du chantier 2, désormais paquetée |
| `images/freedos/` | la recette FreeDOS : `build-freedos.py`, `FDAUTO.BAT` |
| `images/build/<image>/` | la sortie (ignorée par git) ; `pack/` est la forme publiée |
| `tests/OsX86.Tests/Images/` | tests `unit` des outils et test `guest` du pack Alpine |
| `.github/workflows/images.yml` | construction par la CI, artefacts, publication Pages et R2 |

```bash
python -I images/alpine-test/build-alpine-test.py     # ISO officiel, pas de Docker
python -I images/freedos/build-freedos.py             # archive officielle, mtools ou osx86-tools
python -I images/linux-base/build-linux-base.py       # Docker (linux/386), 5 Go libres exigés
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
tmpfs montés par l'init (`/run`, `/tmp`, `/var/tmp`, `/var/log`, `/home`) ; le disque des
préfixes vient avec les chantiers 9 et 10 (211 A). Coût : la racine décompressée est un fichier
ext4 (138 Mio pour linux-base) et non des fichiers dédoublonnés par hachage ; c'est ce que la
compression par blocs ci-dessous rattrape, puisque l'OPFS garde les octets compressés.

**Le démarrage réel de linux-base n'est pas encore possible** : le worker du chantier 2 ne lit que
le noyau et l'initramfs de l'OPFS ; la prise en charge de `hda` (un tampon v86 adossé à un
`SyncAccessHandle`, qui décompresse les trames à la demande) est le chantier 6. Le pack Alpine de
test, lui, démarre dans le test `guest` de ce chantier.

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
            "agent": {"path": "/usr/lib/osx86/agent", "port": "virtio0"}},
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

Quatre étapes Docker, toutes en `linux/386` :

1. **kernel** : `alpine:3.24`, `apk fetch linux-virt busybox musl` (noms de fichiers épinglés,
   SHA-256 conservés dans `/usr/lib/osx86/provenance`), le noyau `linux-virt` 6.18.63-r0 extrait
   du paquet, l'initramfs officiel d'Alpine refait par `mkinitfs -F "base osx86"` (la feature
   `osx86.modules` ajoute `sd_mod`, les pilotes ATA et ext4 : `ata_piix` est intégré au noyau,
   `sd_mod` et `ext4` sont des modules), et les modules de `modules.keep` avec leurs dépendances
   (56 fichiers sur 868, décompressés parce que le `kmod` de Debian ne lit pas les `.ko.gz`,
   essai E17).
2. **probes** : deux programmes C statiques des contrôles R134.5 (`osx86-rawsock`,
   `osx86-ptrace`), parce que l'image n'a pas de compilateur.
3. **base** : `debian:trixie-slim` i386 plus `kmod` et `procps`, le noyau, l'initramfs et les
   modules, `init.sh` en `/sbin/init`, `verify-console.sh`, la place de l'agent
   (`/usr/lib/osx86/agent`, lancé par l'init s'il existe), le durcissement. Le chantier 9 (Wine)
   part de cette étape.
4. **slim** : `apt`, `perl-base` et leurs bibliothèques purgés, pages de manuel, locales,
   convertisseurs `gconv` (sauf Unicode, CP437, CP1252, Latin-1), fuseaux (sauf UTC) et base
   dpkg retirés ; les fichiers `copyright` restent. L'inventaire des paquets
   (`packages.tsv`, `licenses.tsv`) est écrit à cette étape, après la purge.

`build-linux-base.py` exporte le conteneur (`docker export`), lance dans `osx86-tools`
l'extraction avec propriétaires et modes, le test d'image, puis `mkfs.ext4 -d` (sans journal,
UUID et graine fixes, `SOURCE_DATE_EPOCH`), écrit `sources.json`, paquette et mesure.

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

### alpine-test

Inchangée dans sa construction (ISO officiel 3.22.8, lecteurs ISO 9660 et cpio maison,
`init.sh` en `/init`) ; `build-alpine-test.py` appelle maintenant `pack-image.py` et écrit
`images/build/alpine-test/pack/` (entrées `kernel` et `initramfs`, `boot.json`, `sources.json`).
Le `manifest.json` de `images/build/alpine-test/` reste la description de construction que lit
la page de test du chantier 2.

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
  `nosuid,nodev,noexec` et jamais `chown`.

Les tests `unit` d'`ImageCheckTests` construisent un tar synthétique avec le vrai `init.sh` et le
cassent de cinq façons (setuid, `/run/lock` inscriptible, init sans sysctls ni options de
montage, `/root` ouvert) pour vérifier que chaque fuite est vue.

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
- **alpine-test** : les aports `3.22-stable` (`main/linux-lts`, `main/busybox`, `main/musl`) et
  l'ISO ; **freedos** : l'archive officielle, les licences par paquet du dossier `PACKAGES` du
  disque et les sources de la distribution 1.4 sur ibiblio.

## Publication (202 A)

`images.yml` tourne sur tout changement de `images/**`, des tests d'images ou du workflow :
quatre jobs construisent et vérifient alpine-test, freedos, linux-base (Docker sur le runner,
i386 natif) et les tests des outils, et gardent chaque pack en artefact. Sur un push vers
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
| alpine-test | noyau 7,70 Mo + initramfs 7,58 Mo | **15,14 Mo** | 1 | le bzImage est déjà compressé |
| freedos | disque 33,55 Mo | **17,25 Mo** | 1 | le disque contient 17 Mio de paquets zip |
| linux-base | noyau 8,11 Mo + initramfs 5,06 Mo + ext4 138,41 Mo (export 102,35 Mo) | **73,05 Mo** | 3 | sans Wine ; 56 modules noyau ; dégraissage : voir `measurements.json` |

L'objectif de 250 Mio de 201 A porte sur l'image Wine (chantier 9), qui part de l'étape `base` ;
E17 avait mesuré 380 Mio avec Debian complet, 130 Mio de modules et `apt`. Les mesures sont dans
`images/build/<image>/measurements.json` et dans `manifest.build.sizes`.

## Tests

- `dotnet test --filter "Category=unit"` : `PackImageTests` (morceaux, schéma, `check` qui
  détecte un octet changé et un manifeste modifié, `extract`, refus des mauvais arguments),
  `ImageCheckTests` (tar synthétique, 22 contrôles, cinq cassures). Ils lancent Python avec `-I`
  ; sans Python 3.14 ni `zstandard` ils sont ignorés en local et échouent dans la CI.
- `dotnet test --filter "Category=guest"` : `PackedAlpineGuestTests` vérifie le pack Alpine,
  l'extrait et le démarre dans le worker jusqu'à l'invite série (9 s dans Chrome).
- La CI (`ci.yml`, `guest-tests.yml`) installe Python 3.14 ; `images.yml` construit les images.
