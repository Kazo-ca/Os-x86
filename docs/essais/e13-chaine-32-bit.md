# Essai E13 — Chaîne 32 bits : avenir d'Alpine x86, des Wine i386, du x86_64 en WebAssembly et d'un vrai Windows dans v86

Date : 2026-11-10. Étude de lecture (ligne E13 des études prévues), sources primaires seulement : listes de diffusion, pages de paquets, dépôts Git, notes de version. Rien n'a été construit ni téléchargé (pas d'image, pas de binaire). Les points non confirmés par une source primaire sont marqués **non vérifié**. Nourrit D4 (banc d'essai x64), D9, D35 (Wine 11.0 épinglé), C6, C9, C18 (vrai Windows en V1 « si peu de changements », consignes §4).

## But

Savoir sur quoi repose la chaîne retenue par E1 (Alpine x86 + `linux-virt` + Wine i386 dans v86), combien de temps elle tiendra, ce qu'il en coûterait de construire Wine nous-mêmes, où en est le x86_64 dans un navigateur pour le banc d'essai de D4, et si un vrai Windows 10 32 bits dans v86 est réaliste pour C18. Livrable demandé par la ligne E13 : un **plan de repli écrit**.

## Alpine et le x86

Ce qui est vérifiable aujourd'hui sur `pkgs.alpinelinux.org`, `alpinelinux.org/releases/` et `alpinelinux.org/downloads/` :

| Branche | Sortie | Fin de support | `linux-virt` x86 | `wine` x86 (community) | Mainteneur |
|---|---|---|---|---|---|
| v3.21 | 2024-12-05 | 2026-11-01 | — (non relevé) | 9.17-r1 (2024-11-28) ; **aarch64 absent** | Natanael Copa |
| v3.22 | 2025-05-30 | 2027-05-01 | 6.12.115-r0 (2026-11-01) | 10.7-r0 (2025-05-05) | Natanael Copa |
| v3.23 | 2025-12-03 | 2027-11-01 | 6.18.60-r0 (2026-11-01) | 10.19-r0 (2025-11-15) | Natanael Copa |
| v3.24 | 2026-06-09 | 2028-06-01 | 6.18.62-r0 (2026-11-07) | **11.0-r0** (2026-01-18) | Natanael Copa |
| edge | — | — | 7.3.3-r0 (2026-11-08) | 11.0-r0 (2026-01-18) ; x86, x86_64, aarch64 | Natanael Copa |

Constats :

- **x86 est toujours une architecture complète d'Alpine** : images Standard, Extended, Netboot, Virtual et mini-rootfs en 3.24.4 (2026-11-05) ; `linux-virt` x86 est reconstruit chaque semaine sur toutes les branches, edge compris (7.3.3 le 2026-11-08). Les annonces de 3.23.0 (2025-12-03) et 3.24.0 (2026-06-09) sur `lists.alpinelinux.org` ne mentionnent **aucune** dépréciation du x86 ; les seules dépréciations de 3.24 sont `qemu-binfmt`, `pkg_resources`, GTK 2, Qt5, libsoup 2.
- Alpine n'a pas de notion publique de « tiers » d'architectures comparable à Debian ; les pages du wiki (notes de version, Architecture, Requirements) répondent 403 à un client automatisé — la base d'instructions exacte du x86 Alpine (i586 ou i686, PAE ou non) est **non vérifiée** ici. Le fil `alpine-devel` de 2015 (« what 32 bit architecture should we aim for », `lists.alpinelinux.org/alpine-devel/4856.html`) penchait pour `-march=i586` sans PAE obligatoire : à confirmer sur l'APKBUILD de `gcc`.
- **Wine x86 est construit nativement** : `arch="aarch64 x86 x86_64"` dans `community/wine/APKBUILD` (miroir `github.com/alpinelinux/aports`), avec `mingw-w64-gcc` pour les DLL PE ; sur x86 la seule particularité est `-no-pie`. Mainteneur unique : Natanael Copa (fondateur d'Alpine). Wine n'est pas mis à jour entre deux branches : 3.24 et edge sont **tous deux à 11.0** depuis janvier 2026 alors que Wine est à 11.21 (voir plus bas). Wine edge n'a pas suivi les versions de développement en 2026 : non vérifié si c'est un choix ou un oubli.
- Alpine 3.21 (celle que v86 recommande dans sa recette Docker) sort du support **le 2026-11-01**, c'est-à-dire cette semaine.

Sources : `https://pkgs.alpinelinux.org/packages?name=wine&branch=v3.24` (et v3.23, v3.22, v3.21, edge) ; `https://pkgs.alpinelinux.org/package/edge/main/x86/linux-virt` ; `https://alpinelinux.org/releases/` ; `https://alpinelinux.org/downloads/` ; `https://lists.alpinelinux.org/~alpine/announce/%3C20260609230455.2543c365%40ncopa-desktop.lan%3E/raw` ; `https://raw.githubusercontent.com/alpinelinux/aports/master/community/wine/APKBUILD`.

## Autres sources de Wine 32 bits

| Source | Version(s) 32 bits disponibles (2026-11-10) | Licence | Maintenance / avenir | Note pour nous |
|---|---|---|---|---|
| **WineHQ, dépôt Debian `trixie` `binary-i386`** (`dl.winehq.org/wine-builds/debian/dists/trixie/main/binary-i386/`) | `wine-stable-i386` 8.0.2 → **11.0.0.0** ; `wine-devel-i386` et `wine-staging-i386` 8.11 → **11.21** (fichier `Packages` du 2026-10-31) | LGPL 2.1+ | Construit par WineHQ à chaque version (toutes les 2 semaines) ; `forky` et `sid` aussi présents | **Meilleure source de rechange** : i386 natif, staging compris, glibc ≥ 2.38 (Debian/Ubuntu, pas musl : il faudrait un invité Debian i386 ou un chroot glibc dans Alpine) |
| **Debian 13 `trixie`** | `wine32` 10.0~repack-6 (i386, armhf) | LGPL | i386 « n'est plus une architecture régulière » : ni noyau ni installateur officiels, « seul but restant : faire tourner du code hérité » via multiarch sur amd64 ; base d'instructions relevée à **SSE2** | Les paquets i386 existent mais Debian dit aux systèmes 32 bits de **ne pas** passer à trixie. Pour `forky` (14) i386 est « candidat » avec un seul porteur et des builds qui touchent la limite d'espace d'adressage (`release.debian.org/forky/arch_qualify.html`) |
| **Ubuntu** | `wine32` 9.0~repack-4build3 (24.04), 10.0~repack-11 (26.04 `resolute`), i386 | LGPL | Depuis 2019, i386 = ensemble choisi de paquets pour la compatibilité (`ubuntu.com/blog/statement-on-32-bit-i386-packages-for-ubuntu-19-10-and-20-04-lts`) ; pas de système i386 installable | Même usage que Debian : bibliothèques, pas un OS 32 bits |
| **Fedora** | Wine 11.0 (F43), 11.20/11.21 (F44), 11.21 (rawhide) ; **construit en « nouveau WoW64 »** (bug RH 2393888, 2025-09) | LGPL | Proposition F44 « drop i686 » retirée en juin 2025 (non vérifié au-delà) ; Wine ne tire plus de paquets i686 | Pas de source i686 utile pour nous |
| **Wine amont, nouveau WoW64** (annonce 11.0, `ANNOUNCE.md` du tag `wine-11.0`, 2026-01-13) | Build `--enable-archs=x86_64,i386` : exécute les programmes 32 bits **sans bibliothèques 32 bits**, 16 bits inclus ; `WINEARCH=win32` **déprécié** en mode nouveau WoW64 ; le chargeur `wine64` disparaît | LGPL | Direction amont : tout passe par un Wine 64 bits ; une build « i386 seule » reste possible (Alpine x86, WineHQ i386) mais n'est plus la voie privilégiée | **Le nouveau WoW64 exige un hôte x86_64** (il « thunke » vers du 64 bits) : inutilisable dans v86 (32 bits). Il ne devient intéressant que le jour où un invité Linux x86_64 tourne dans Kazo (palier 2 de D4) |
| **Kron4ek/Wine-Builds** (GitHub) | Builds `amd64`, `amd64-wow64` et **`x86`** (vanilla, staging, staging-tkg, proton) ; glibc ≥ 2.27 | Scripts MIT, Wine LGPL | Un mainteneur ; versions x86 encore publiées pour 11.x : **non vérifié** (le README ne les date pas) | Source de secours pour une build i386 staging prête, glibc |
| GE-Proton / Lutris | Proton-GE 11-10 (2026-10) ; Lutris renvoie à GE-Proton + umu | Mélange LGPL / Valve | Orienté jeu, amd64 + WoW64 seulement | Hors sujet pour un invité 32 bits |

Versions amont : Wine **11.0** stable (tag `wine-11.0`, 2026-01-13, Alexandre Julliard), **11.21** développement (`ANNOUNCE.md` sur `master` du miroir GitHub). Le rythme est bien « une version de développement toutes les deux semaines, une stable par an (janvier) ».

Point d'attention pour le bogue E1 (fenêtres 1x1) : l'annonce 11.0 décrit des changements dans winex11 (activation des fenêtres déléguée au gestionnaire, EWMH, EGL par défaut, mémoire partagée entre processus). WineHQ fournit `wine-devel-i386` 11.21 et `wine-staging-i386` 11.21 pour trixie : c'est le moyen le moins cher de tester « même programme, autre Wine » sans rien construire (en chroot glibc dans l'invité, ou avec un invité Debian i386 jetable).

## Construire Wine nous-mêmes

Hypothèses : build i386 seule (`./configure` sans `--enable-win64`, comme l'APKBUILD x86 d'Alpine), sur un hôte x86_64 Linux, DLL PE croisées avec MinGW. Ordres de grandeur **estimés** (aucune mesure faite ; les seules mesures publiques trouvées datent de 2008 : 3 à 5 min sur un quadricœur pour un Wine dix fois plus petit — `winehq.org/pipermail/wine-devel/2008-June/066115.html`).

| Poste | Estimation | Base |
|---|---|---|
| Source | 31,6 Mio `.tar.xz` (`dl.winehq.org/wine/source/11.0/`), ~0,5 Gio décompressé | mesuré sur le listing |
| Dépendances de build | ~35 paquets `-dev` (alsa, cups, dbus, fontconfig, freetype, gnutls, gstreamer, krb5, libgphoto2, libpcap, libusb, libx*, mesa, opencl, pcsc-lite, pulseaudio, sane, sdl2, udisks2, v4l, vulkan, wayland) + `mingw-w64-gcc` + bison/flex | APKBUILD Alpine ; GLFS `wine.html` |
| Compilation croisée depuis x86_64 | **Pas vraiment une cross-compilation** : la partie Unix (ELF i386) exige un compilateur et des bibliothèques i386 (multilib ou conteneur `--platform=linux/386`, comme E1) ; la partie PE est déjà croisée par MinGW. Le plus simple : `docker build --platform=linux/386 alpine:3.24` + `abuild` de l'APKBUILD existant | — |
| Temps de build | **non vérifié** ; hypothèse 15 à 40 min sur 8 à 16 cœurs x86_64 natifs, 2 à 5× plus sous émulation (Docker 386 sur une machine x86_64 est natif, donc pas de pénalité) | extrapolation 2008 × croissance du code |
| Produit | ~220 Mio installés (mesure E1 pour 11.0-r0) ; 68 Mio en apk | E1 |
| Mise à jour | Suivre `wine-X.Y` toutes les 2 semaines = 26 builds/an ; suivre la stable = 1 build/an + correctifs. Une CI (GitHub Actions avec QEMU user-mode ou runner 386) coûte 1 à 2 jours à mettre en place, puis ~1 h de machine par version | hypothèse |
| Risque | Tests Wine désactivés chez Alpine (`!check`, « most of the tests fail ») : on hériterait d'une build jamais testée sur musl/i386 ; la régression E1 vient peut-être de là (non vérifié) | APKBUILD |

Conclusion : construire Wine i386 nous-mêmes est **faisable et bon marché** (quelques heures de mise en place, puis automatisable) tant qu'un compilateur i386 et une base Alpine x86 existent ; ce n'est pas un verrou. Le vrai coût est de **tester** chaque build dans v86 (7 min de `wineboot` par essai, E1).

## Invité x86_64 en WebAssembly

| Moteur | État (2026-11-10) | Performance annoncée | Exigences navigateur | Verdict pour D4 |
|---|---|---|---|---|
| **v86** (copy/v86) | 32 bits seulement : « 64-bit extensions are not supported, 64-bit Linux kernels don't work » (`Readme.md`). Aucun fork 64 bits trouvé (forks `@aptre/v86`, `viorelcanja/v86-1` gardent la même limite) | — | Aucune : fil principal ou Worker, pas de SharedArrayBuffer | Pas de x86_64 ; v86 reste le moteur 32 bits |
| **QEMU Wasm** (`github.com/ktock/qemu-wasm`, Kohei Tokunaga, NTT) | Amont QEMU : TCI 32 bits fusionné en **10.1** ; « Enable 64bit WebAssembly guests (TCI) » dans la PULL accélérateurs du **2026-01-16** (Philippe Mathieu-Daudé, « Applied, thanks » de R. Henderson) ; **backend TCG wasm64** (JIT, 33 patchs, v4 du 2026-01-26, `tcg/wasm64/`) **toujours en revue**, pas de v5 trouvée. Le dépôt ktock est « experimental software » | Un seul chiffre public : graphique « pigz 10 Mo sur x86_64 émulé » vs Bochs-wasm (FOSDEM 2025, Core i7-10510U, Chrome 130) — **les valeurs ne sont pas dans le texte des diapositives** (non vérifié). Le JIT ne compile un bloc qu'après ~1 000-1 500 exécutions parce que « les navigateurs ne peuvent pas créer des milliers de modules » ; future work : « still slower than other backends » | **pthreads Emscripten → SharedArrayBuffer → COOP/COEP obligatoires** (`emscripten.org/docs/porting/pthreads.html`) ; MTTCG ; wasm64 (Memory64) pour les invités 64 bits, avec repli `--enable-wasm64-32bit-address-limit` (wasm32, 4 Gio max) pour Safari ; Node ≥ 24 pour le mode wasm64 | **Seul candidat sérieux** pour Linux x86_64 ; à mesurer nous-mêmes (D4) car aucun chiffre primaire |
| **container2wasm** (ktock) | Bochs-wasm pour x86_64, TinyEMU pour riscv64, QEMU Wasm pour le navigateur ; v0.5.0 | « other platforms […] slow because of additional emulation » ; pas de chiffre | Comme QEMU Wasm ; réseau testé « only on Chrome » | Emballage de QEMU Wasm, pas un moteur de plus |
| **JSLinux** (bellard.org) | **x86_64 avec AVX-512 et APX** depuis ~2026-03 (Alpine 3.23.2 x86_64 en démo) ; mais la couche x86_64 **n'est pas publiée** : TinyEMU 2019-12-21 (MIT) ne fait que RISC-V + x86 32 bits | Aucun chiffre ; réseau bridé à 40 ko/s | Non documenté | Preuve qu'un x86_64 interprété tourne en wasm ; **inutilisable** (code fermé) |
| Box64/Box86 en wasm | Aucune trace (recherche GitHub/web) | — | — | N'existe pas |
| Bochs-wasm seul | Interpréteur, utilisé par container2wasm | — | — | Trop lent a priori ; mesurer si QEMU Wasm échoue |

Conséquences : un **invité Linux x86_64** dans Kazo est réaliste en 2026 avec QEMU Wasm TCI (déjà dans QEMU 10.2+ pour les invités 64 bits, non vérifié pour le numéro exact de version), au prix d'un COOP/COEP sur toute la page Kazo (incompatible avec des iframes tierces non `cross-origin isolated`), d'un Worker à threads, et d'une vitesse probablement bien inférieure au JIT de v86 tant que le backend TCG wasm64 n'est pas fusionné. Un **Windows x64 invité** dans QEMU Wasm n'a aucun rapport public de réussite trouvé (non vérifié) : à considérer comme recherche, conformément à la décision 17 (Windows x64 « sans date »).

## Vrai Windows 32 bits dans v86

Fait vérifié : v86 a le **bit NX depuis le commit `6db8b157974dbaf1b54d2c2ec12dd71ddc1891e9` du 2026-10-04** (« NX bit support — windows 10 works now », auteur copy, 24 fichiers, test `kvm-unit-tests/x86/nx.c`), issu de la PR #1484 de DixelU (ouverte 2026-01-11, fusionnée via #1647). Première version npm qui le contient : **`v86@0.5.470`** (publiée 2026-10-04 07:54 UTC, 19 min après le commit) ; E1 tournait sur 0.5.473 et l'avait donc déjà. L'issue #845 (ouverte 2023-04) est fermée « completed » le 2026-10-15.

| Exigence Windows 10 32 bits (sources Microsoft) | v86 aujourd'hui | Source |
|---|---|---|
| PAE + NX + SSE2 (noyau refuse de démarrer sinon, bugcheck 0x5D ; NX en 32 bits exige PAE) | PAE : oui ; **NX : oui depuis 2026-10-04** ; SSE2/SSE3 : oui (« Pentium 4 level, full SSE3 ») ; SSSE3 absent (#1038 ouvert) | `learn.microsoft.com/.../dn482072(v=win.10)` ; v86 Readme ; commit 6db8b15 |
| 1 Go de RAM (32 bits), 32 Go de disque (22H2), DirectX 9 / WDDM 1.0, 800x600 | RAM : `memory_size` libre (E1 : 1 Gio) ; disque : IDE seulement, **pas d'AHCI** (pas de `ahci.js`) — Windows 10 sait démarrer en IDE ; VGA Bochs VBE sans WDDM → pilote de base Microsoft | `support.microsoft.com/.../windows-10-system-requirements` ; `api.github.com/repos/copy/v86/contents/src` |
| ACPI (HAL ACPI obligatoire depuis Vista) | `acpi.js` ≈ 200 lignes : PIIX4 PM timer, PM1, GPE, IRQ 9 ; **pas de tables** dans v86 (elles viennent de SeaBIOS) ; option `acpi: true` marquée « experimental » sur copy.sh ; v86 exige ACPI pour Vista+ (`docs/windows-nt.md`) | `src/acpi.js` ; `docs/windows-nt.md` |
| APIC / multiprocesseur | APIC et IOAPIC en Rust (`apic.rs`, `ioapic.rs`, « partial ») ; **monocœur** | Readme |
| Réseau | NE2000 et virtio-net ; « Windows 7 doesn't support the ne2k card » (#473, copy) → virtio-net avec pilotes Red Hat/virtio-win, non vérifié sous v86 | discussion #951 |
| Rapports de démarrage | Aucune démo Windows 7/8/10 sur copy.sh (dernière : Windows 2000). PR #1484 : Windows 7 CD minimal (01/2026), **installation Windows 7 normale démarre (DixelU, 2026-05-17)**, 8.1 « seems to boot fine » (05/2026), **Tiny10 2303 s'installe et démarre (2026-09-24)** ; message de commit du mainteneur « windows 10 works now ». Aucun rapport indépendant, aucune mesure de vitesse, aucune doc `docs/windows-10.md` | PR #1484 ; commit |
| Licence | Microsoft ne vend plus Windows 10 ; fin de support **2025-10-14** ; la page de téléchargement 22H2 propose encore des ISO 32 bits (liens non rendus pour un client automatisé : non vérifié) ; l'image doit être **fournie par l'usager** avec sa propre licence, et ne jamais transiter par nos serveurs | `learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro` ; `microsoft.com/en-us/software-download/windows10ISO` |

**Verdict pour C18 (« vrai Windows en V1 si peu de changements »)** : techniquement, *démarrer* un Windows 10 32 bits dans v86 est devenu plausible le 2026-10-04 et ne demande **aucun changement de notre côté** sur le moteur (v86 officiel, 0.5.470+). Mais ce qui est attesté tient en une ligne de commit et un fil de PR, sur Tiny10 (image remaniée) et Windows 7 ; rien sur la vitesse (E1 montre 62 s pour booter Alpine et 7 min pour `wineboot` : un Windows 10 complet, qui fait des dizaines de Go d'E/S IDE au premier démarrage, se comptera en dizaines de minutes voire en heures — non mesuré), rien sur ACPI « experimental », l'affichage (VGA de base), le son (SB16 sans pilote 10), le réseau (virtio-net à vérifier), ni sur l'activation. Ce qui coûterait des changements significatifs n'est pas dans v86 mais **dans Os-x86** : disque IDE de 32 Go (E2 : COW, OPFS, quotas), état gabarit impossible (licence : chaque usager apporte son image), installateur Windows à piloter dans la VM, pas de `virtio-console`/9p côté Windows pour notre pont. Donc : **« peu de changements » n'est pas tenu pour un Windows 10 utilisable en V1** ; par contre un palier « Windows 10/7 32 bits *démarre* dans la VM générique d'Os-x86, image apportée par l'usager, sans intégration » est un essai de 1 à 2 jours (E-nouveau, à planifier après E1 corrigé) qui ne bloque rien. Recommandation : **report en V2 accepté par les consignes**, avec cet essai de faisabilité en V1 pour fixer les chiffres.

## Plan de repli

1. **Si Alpine abandonne x86** (aucun signe aujourd'hui ; surveiller `alpine-announce` à chaque branche, mai/juin et novembre/décembre, et la présence de `linux-virt` x86 dans edge) : (a) rester sur la dernière branche Alpine x86 supportée tant qu'elle reçoit des correctifs (deux ans après sa sortie), en figeant l'image gabarit ; (b) passer l'invité sur **Debian i386** (paquets i386 trixie/forky, noyau à construire ou `linux-image` 686 de bookworm, SSE2 requis — v86 l'a) ou sur **Buildroot i386** (recette `v86/tools/docker/` + noyau sur mesure, 100 % sous notre contrôle, mais plus de `apk`) ; (c) l'image étant servie par nous (D35), l'usager ne voit rien du changement. Coût estimé : 2 à 4 semaines pour refaire la recette et l'image gabarit.
2. **Si Wine i386 disparaît des dépôts** (Alpine, puis WineHQ) : (a) **WineHQ `binary-i386`** (trixie/forky/sid, stable 11.0 et devel/staging 11.21) est la première roue de secours — il faudrait un chroot glibc ou un invité Debian ; (b) **construire Wine i386 nous-mêmes** à partir de l'APKBUILD d'Alpine (voir plus haut : des heures, pas des semaines), épinglé sur la stable annuelle, CI à 1 h par version ; (c) à terme, Wine amont ne garantit que le nouveau WoW64 sur x86_64 : la vraie sortie est le **palier Linux x86_64** (D4), où Wine WoW64 fait tourner les programmes 32 bits sans paquets i386. Donc la disparition de Wine i386 ne tue pas V1 (nous pouvons construire) mais raccourcit la durée de vie du palier 32 bits.
3. **Si v86 reste 32 bits seulement** (c'est le cas et rien n'indique un changement) : (a) le palier x86_64 passe par **QEMU Wasm** (TCI 64 bits déjà amont, TCG wasm64 à suivre) avec COOP/COEP et SharedArrayBuffer, derrière l'abstraction moteur déjà prévue (C9) ; (b) le banc D4 compare v86 (32 bits, JIT) et QEMU Wasm (x86_64, TCI puis TCG) sur les mêmes charges (boot Alpine, `wineboot`, Bloc-notes) ; (c) si QEMU Wasm est trop lent, le palier x86_64 reste « recherche » et **le 32 bits est la seule production** : Os-x86 doit alors être explicitement vendu comme « programmes Windows 32 bits et DOS », ce qui est déjà la cible de V1. Un fork de v86 pour y ajouter le mode long est hors de portée (cœur en Rust/JIT entièrement 32 bits ; aucune tentative publique).

## Limites de l'étude

- Les wikis d'Alpine (`wiki.alpinelinux.org`), de WineHQ (`gitlab.winehq.org`) et de QEMU (`wiki.qemu.org`) bloquent les clients automatisés (403/Anubis) : notes de version 3.23/3.24, page « Debian-Ubuntu » de WineHQ et changelogs QEMU 10.2/11.0 n'ont pas été lus ; les informations équivalentes viennent des listes de diffusion, des pages de paquets et des dépôts Git.
- Les chiffres de performance de QEMU Wasm ne sont que dans un graphique d'image (FOSDEM 2025) : non lus.
- Les rapports « Windows 10 démarre » viennent d'un seul contributeur et d'un message de commit ; pas de reproduction indépendante, pas de temps mesuré.
- Les coûts de build Wine sont des estimations, pas des mesures.
- Pas de vérification de la base d'instructions exacte (i586/i686, PAE) d'Alpine x86.

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider, pas une décision.

1. **D35 (Wine 11.0 épinglé)** : garder 11.0 comme version de référence, mais **ne pas épingler la source** : prévoir dès maintenant que le Wine de l'image puisse venir d'Alpine, de WineHQ i386 ou de notre propre build, choisi par la recette d'image. Pour le bogue E1, tester en priorité `wine-devel-i386` 11.21 et `wine-staging-i386` 11.21 de WineHQ trixie, puis Alpine 3.23 (10.19), avant d'ouvrir un rapport amont. À valider.
2. **D9 / image gabarit** : la base Alpine 3.24 x86 est saine jusqu'en juin 2028 ; mettre dans le journal une **règle de suivi** : à chaque branche Alpine, vérifier `linux-virt` x86 et `wine` x86, et rebâtir l'image gabarit au moins une fois par an. À valider.
3. **C6 (v86 officiel ou fork)** : rien dans cette étude ne pousse vers un fork ; le NX est arrivé en amont sans nous. Rester sur v86 officiel, version ≥ 0.5.470. À valider.
4. **D4 / C9 (banc d'essai x64)** : le banc doit comparer **v86 vs QEMU Wasm TCI 64 bits** (et TCG wasm64 si fusionné d'ici là), avec le coût d'isolation d'origine (COOP/COEP) chiffré côté Kazo, et poser dès maintenant la question « Kazo peut-il être cross-origin isolated ? » à l'équipe Kazo, car c'est un prérequis dur et indépendant du moteur. À valider.
5. **C18 (vrai Windows en V1)** : verdict ci-dessus : **report en V2**, avec un essai de faisabilité (boot d'un Windows 7 ou 10 32 bits apporté par l'usager dans la VM générique, temps mesuré, sans intégration) ajouté aux études prévues. À valider.
6. **Plan de repli** : l'inscrire tel quel au journal (section « risques »), avec les trois déclencheurs et les trois réponses ; le revoir à chaque branche Alpine et à chaque Wine stable (janvier). À valider.
7. **Nouveau WoW64** : noter que le jour où un invité Linux x86_64 tourne, **un seul Wine** (WoW64) couvre les programmes 16, 32 et 64 bits, ce qui simplifie D41 (une bouteille par programme) — mais seulement à ce palier. À valider.
