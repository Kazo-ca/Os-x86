# Chantier 9 (j0/9-wine-image) : note de passation

PR #12 (`j0/9-wine-image` → `j0/base`), fusionnée le 2026-11-16 (59efa7f). Documentation de
référence : `docs/fr/images.md`, section « wine (chantier 9 ; 201 A, 152 A, 204 A) » et
« Mesures de l'image Wine » (miroir `docs/en/images.md`).

## Ce que la suite peut prendre tel quel

- **Construire l'image** : `python -I images/wine/build-wine.py` (part de linux-base, dont
  `build-linux-base.py` est importé ; `--from install|prefix|slim` pour reprendre à une étape,
  `--skip-docker-build`, `--fetch-sources` pour les sources copyleft). Sortie :
  `images/build/wine/pack/` (manifeste avec `boot.wine` : `prefixTemplate`, `prefixCopy`,
  `templateWindowsUser`, `hotFiles`, `preloadOff`). CI : jobs `wine-changes`, `wine` (artefact
  `pack-wine`, jamais publié, hors du motif `image-*` du job R2) et `guest-wine`.
- **Bouteille gabarit** : `/usr/lib/osx86/wine/prefix-template`, faite par `wineboot` dans Docker,
  usager Windows `osx86-template`, doublons des fichiers de Wine, Mono et Gecko remplacés par des
  liens (≈ 2 Mo de fichiers propres), `winemenubuilder` coupé. Mono et Gecko sont des
  installations partagées sous `/usr/share/wine` : jamais copiés dans un préfixe.
- **Copie par préfixe** : `/usr/lib/osx86/wine/prefix-copy <usager> [<destination>]` (root ;
  copie dans un dossier de root sous `/run`, renomme l'usager Windows dans le profil et le
  registre, `chown`, puis `mv -T`). L'image n'a aucun usager de préfixe : le test les ajoute en
  mémoire depuis la console série.
- **Préchargement** : crochet d'init `/usr/lib/osx86/init.d/50-preload-wine` (liste
  `/usr/lib/osx86/wine/hot-files.list`, 224 fichiers, 108 Mo) ; `osx86.preload=off` sur la ligne
  de commande du noyau le coupe. L'init de linux-base lance désormais `/usr/lib/osx86/init.d/*`.
- **Programme console de test** : `/usr/lib/osx86/wine/osx86-hello.exe` (source
  `images/wine/osx86-hello.c`, MinGW-w64 de Debian ; code de sortie 7).

## Pour le chantier 10 (préfixes, `x86 run`)

- **Le `wineserver` d'un préfixe doit vivre à part.** Lancé par une commande `exec-in-prefix`, il
  appartient au cgroup de cette commande, que l'agent termine 2 s après sa fin (SIGHUP puis
  `cgroup.kill`, 208 A) : une seconde commande qui s'y connecte dans ce délai reste bloquée
  (480 s observées). `WineGuestTests` lance `wineserver -f -p` comme lancement propre avant les
  commandes ; à trancher : wineserver persistant par préfixe, exemption, ou autre.
- **Dates de l'ext4.** Wine compare `.update-timestamp` du préfixe à la date de `wine.inf` et
  refait la bouteille si elles diffèrent (plusieurs minutes dans la VM). L'image passe la date de
  `wine.inf` comme `SOURCE_DATE_EPOCH` à `mkfs.ext4` et la vérifie par `debugfs`. Un changement
  de version de Wine dans l'image changera cette date : un préfixe copié de l'ancien gabarit a
  des liens vers les fichiers de la nouvelle version, à traiter avec la migration des préfixes.
- `wineboot --init` relance les services à chaque session (`services.exe`, `plugplay`,
  `winedevice`) : c'est l'essentiel des ≈ 7 à 10 s du premier `wine cmd`.

## Mesures (local, Chrome headless, Ryzen 7 3700X)

- Pack **365,8 Mo (348,9 Mio)** pour un objectif de 250 Mio (écart ≈ 99 Mio) ; ext4 1 244 Mio.
  Compressé : DLL PE de Wine 143 Mo, Mono 64 Mo, Gecko 45 Mo, bibliothèques i386 14 Mo.
- Invite 15 à 16,5 s ; premier programme console **13 à 17 s après l'invite** (critère de 204 A,
  moins de 60 s : atteint ; 152 A n'est pas reposée). Le préchargement gagne ≈ 1,2 s s'il a fini
  avant, coûte 1 à 2 s s'il tourne en même temps.

## Licences (plan, section 12)

Wine : LGPL-2.1 ou ultérieure. Mono 10.4.1 : MIT X11 pour l'essentiel, parties sous LGPL et GPL
avec exception (SharpZipLib), MS-PL, zlib, BSD (`COPYING` de wine-mono). Gecko 2.47.4 : MPL 2.0
et les licences de `toolkit/content/license.html`. Copyleft dans les trois : leurs sources
(`sources.json`, `--fetch-sources`) et ces textes sont à publier avec l'image à la première
publication.

## Non vérifié

Firefox, Safari ; Mono et Gecko en usage (aucun programme .NET ni HTML lancé) ; publication R2
(pas encore d'origine) ; une machine plus lente que celle des mesures.
