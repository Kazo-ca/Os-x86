> [English version](../en/engine.md)

# Le moteur v86 dans un Web Worker

Jalon 0, chantier 2 (`j0/2-engine-v86`). Le moteur est la pièce qui fait tourner une machine
virtuelle : v86 dans un Web Worker, piloté depuis le C# par un petit protocole JSON. Ce chantier
livre l'abstraction `IEngine`, son implémentation v86, le worker, le pont d'affichage, les ports
série et virtio-console, le v86 vendorisé avec ses correctifs, une image Alpine de test et les
tests. Le registre des VM, le stockage des images et l'agent invité viennent avec les chantiers 3,
5 et 6 ([ordre du jalon](../conception/jalon-0-execution.md)).

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Engine/` | `IEngine`, `EngineState`, `EngineStateMachine`, `EngineStartOptions`, `ConsoleId`, `IEngineConsole`, `LineFramer`, les exceptions |
| `src/OsX86/Engine/V86/` | `V86Engine` (l'implémentation), `IEngineWorkerChannel` et `JsEngineWorkerChannel` (l'interop JS), `V86Console`, le protocole |
| `src/OsX86.Feature/wwwroot/engine/` | `v86-host.js` (module importé par le C#, crée le worker), `v86-worker.js` (héberge v86), `zstd-seekable.js` (table de recherche zstd) et `disk.js` (disques, cache de trames, couches d'écriture), chantier 6a ; `vga-text.js` (écran texte avec attributs et curseur), chantier 11 |
| `src/OsX86/Engine/ImageBoot.cs`, `EngineDisk.cs` | le démarrage décrit par le manifeste et la règle de la version majeure (169) ; sources, disques et couches des options de démarrage (chantier 6a) |
| `src/OsX86.Feature/wwwroot/v86/` | v86 vendorisé : `libv86.js` (corrigé), `v86.wasm`, `seabios.bin`, `vgabios.bin`, `LICENSE`, `COPYING.LESSER`, `VENDOR.json` |
| `tools/v86/` | `vendor-v86.py` (télécharge, vérifie, applique les correctifs) et `patches/` |
| `images/alpine-test/` | `build-alpine-test.py` et `init.sh` : l'image Alpine de test (jamais commise) |
| `tests/OsX86.Tests/Engine/`, `tests/OsX86.Tests/Guest/` | tests `unit` et test `guest` |

Les fichiers de `wwwroot/` sont des ressources web statiques du projet de la Feature, `OsX86.Feature`, servies
sous `_content/OsX86.Feature/` (l'adresse que Kazo OS donne aux fichiers d'une app, ADR 0206 de Kazo).

## Comment ça marche

```
C# (V86Engine) ──JSON──> JsEngineWorkerChannel ──import──> v86-host.js ──postMessage──> v86-worker.js ──> V86
                <──JSON── [JSInvokable] OnWorkerMessage <──── callback <──── postMessage <────┘   (OPFS, console, VGA)
```

- **`v86-host.js`** est le seul module JavaScript que le C# importe (Kazo n'autorise à une app que
  l'import de son propre module ; les appels sur l'objet obtenu sont libres). `createHost(callback)`
  crée le worker, lui dit où est v86 (à côté du module, même origine exigée) et relaie les messages
  dans les deux sens sans les lire. `attachCanvas(canvas)` transfère un `OffscreenCanvas` au worker.
- **`v86-worker.js`** charge `libv86.js` avec `importScripts` (une seule fois, même origine), lit le
  noyau et l'initramfs dans l'OPFS (`createSyncAccessHandle`, qui n'existe que dans un worker :
  essai E2 ; 512 Mio au plus par fichier), crée l'émulateur et rapporte son état. Il ne fait
  tourner qu'un invité à la fois ; la mémoire demandée est bornée (16 à 4096 Mio).
- **Le protocole** est décrit en tête de `v86-worker.js` : commandes `start`, `stop`, `pause`,
  `resume`, `query-state`, `write` ; messages `ready`, `state`, `error`, `console`, `screen-size`,
  `event`. Les octets des consoles voyagent en base64 dans le JSON (au plus ~1,5 Mio par écriture).

### États

`EngineState` : `Stopped` (arrêté), `Starting` (démarrage), `Running` (en marche), `Paused` (en
pause), `Stopping` (arrêt en cours). `EngineStateMachine` dit quelle opération est permise dans
quel état et quels rapports du worker sont légaux. `StartAsync` revient quand l'invité tourne,
`StopAsync` quand tout est démonté (rien si déjà arrêté ; il rejoint un arrêt en cours et
abandonne un démarrage ou une pause en attente), `PauseAsync` et `ResumeAsync` exigent
respectivement `Running` et `Paused`. Une seule opération à la fois. Une opération annulée avant
d'atteindre le worker remet l'état d'avant ; annulée après, le worker finit et ses rapports
suivants remettent l'état à jour.

### Erreurs

`EngineStateException` (code `invalid-state`) pour une opération interdite dans l'état courant ;
`EngineException` avec un code de `EngineErrorCodes` pour ce que le worker rapporte :
`opfs-unavailable`, `image-not-found`, `image-read-failed`, `v86-load-failed`, `v86-init-failed`,
`download-error`, `protocol`, `worker-error`, et `stopped` quand le moteur s'arrête pendant une
opération, `disposed` quand il est libéré. Tout est aussi remonté par l'événement `ErrorReported` ;
un message mal formé venant du worker est signalé sans faire échouer l'opération en cours.

### Consoles

`GetConsole(ConsoleId.Serial0)` est le port série (`ttyS0`, la console de démarrage) ;
`GetConsole(ConsoleId.Virtio(n))`, `n` de 0 à 3, sont les ports virtio-console (`hvc0` à `hvc3`
sous Linux, le canal de l'agent et des ptys du plan). Chaque port se lit en flux brut
(`ReadRawAsync`) ou par lignes (`ReadLinesAsync`, UTF-8, LF, le CR final retiré, lignes coupées à
64 Kio) et s'écrit (`WriteAsync`, `WriteLineAsync`, permis pendant le démarrage, la marche et la
pause). Un lecteur est inscrit dès l'appel (pas à la première énumération), garde au plus 4 096
morceaux ou lignes (les plus anciens sont jetés s'il ne suit pas), se termine avec son jeton et se
complète quand le moteur est libéré. À l'arrêt, la ligne partielle en attente (l'invite) est
livrée comme une ligne.

### Affichage

`AttachDisplayAsync(canvas)` transfère le contrôle d'un `<canvas>` au worker ; `ScreenSizeChanged`
rapporte les changements de mode VGA (colonnes et lignes en mode texte, pixels sinon, `bpp` 0 =
texte : le signal « DOS graphique » de 164 A, essai E14). Dans ce chantier le worker ne fait que
redimensionner et noircir le canvas ; le rendu arrive avec les invités graphiques du jalon 1.
Le chantier 11 s'en sert : un passage à `bpp ≠ 0` pendant un programme DOS l'arrête (217 A).

## La voie DOS : écran avec attributs, clavier, disque en mémoire (chantier 11)

Ce que le chantier 11 ajoute au moteur pour `x86 run` d'un programme DOS
([`programmes.md`](programmes.md#programmes-dos-chantier-11)) :

- **`ITextModeEngine`** (`EngineTextScreen.cs`), implémentée par `V86Engine` : `ReadTextScreenAsync`
  rend désormais un `EngineTextScreen` avec `Attributes` (un octet VGA par cellule), `Cursor`
  (`EngineTextCursor` : ligne, colonne, montré), `BlinkEnabled` et `Graphical` ; `SendScancodesAsync`
  envoie des scancodes PS/2 du jeu 1 (commande `keys`).
- **`vga-text.js`** : le seul fichier qui lit des champs internes de v86 (218 A) — le périphérique
  VGA (`emulator.v86.cpu.devices.vga`), sa mémoire texte, l'adresse de départ, le curseur, le mode
  d'attributs — en suivant `text_mode_redraw()` et `update_cursor()` de `vga.js` ; sans ces champs
  (une autre version de v86), il retombe sur les caractères de `screen-put-char`, sans attributs ni
  curseur (`source: "events"`). La table CP437 y est passée de caractères de contrôle aux glyphes VGA
  (`☺`, `♥`, `⌂`…).
- **Réponse `screen`** : `cols`, `rows`, `lines`, `attributes` (base64), `cursor` (`{row, col,
  visible}` ou `null`), `blink`, `graphical`, `source`.
- **Clavier** : l'option de démarrage `keyboard` (`EngineStartOptions.Keyboard`) ; sans elle, le
  worker refuse `keys` (`invalid-state`). `disable_keyboard` de v86 reste vrai : il ne coupe que
  l'adaptateur du DOM (absent d'un worker), le clavier PS/2 de l'invité est toujours là et
  `keyboard_send_scancodes` l'alimente. Au plus 256 scancodes par message ; les pauses (Alt tenu) sont
  faites côté C#.
- **Disque en mémoire** : `EngineFileSource.InMemory(octets)` (au plus 64 Mio) devient la source
  `{inline: base64}` ; `disk.js` en fait une `InlineEntry` (pas de fichier OPFS), à mettre sur une
  couche jetable. C'est le disque du programme d'une exécution DOS (`hdb`).

## Disques et démarrage décrit par le manifeste (chantier 6a)

Le chantier 6a (`j0/6a-disk`) fait démarrer les images publiées telles que le stockage les garde :
tout vient de `osx86/images/<image>/<version>/image.bin` dans l'OPFS, sans fichier extrait.

### Démarrer une image

```csharp
ImageManifest manifest = await store.ReadManifestAsync("linux-base", "0.1.0", ct);
EngineStartOptions options = ImageBoot.CreateStartOptions(manifest, new ImageBootOptions
{
    LayerMode = EngineWriteLayerMode.Persistent,   // Discard par défaut : rien n'est gardé
    LayerSet = "vm-1",                             // osx86/layers/vm-1/
});
await engine.StartAsync(options, ct);
```

- `ImageBoot` lit la section `boot` du manifeste (`kind`, `cmdline`, `disks`, `memoryMiB`) :
  `linux` donne le noyau et l'initramfs comme intervalles de `image.bin`
  (`EngineFileSource.Seekable` : position, taille, `rawSize`, `blockSize`), `dos` démarre `hda`
  par le BIOS. Les disques de `boot.disks` (`hda`, `hdb`) reçoivent leur couche d'écriture.
- **Règle « même version majeure » (169)** : l'image doit être faite pour la même version majeure
  de v86 que le moteur (`V86Engine.V86Version`, égale à `VENDOR.json`, vérifié par un test ; avant
  1.0 la mineure fait foi, 0.5.x et 0.6.x diffèrent). Sinon `EngineException` de code
  `engine-version-mismatch`, avant toute lecture ou écriture. Un manifeste sans `boot`, d'un type
  inconnu, avec un disque inconnu ou sans le fichier nommé donne `boot-unsupported`.
- `EngineStartOptions` gagne `Kind`, `Kernel`, `Initrd`, `Disks` et `FrameCacheMiB` ; les chemins
  `KernelPath` et `InitrdPath` du chantier 2 restent (fichier entier). `IEngine` ne change pas.

### Le disque en lecture

`V86Disk` (`disk.js`) est le tampon que v86 reçoit comme `hda` : un `FileSystemSyncAccessHandle`
sur `image.bin`, ouvert une fois pour tous les fichiers logiques. À l'ouverture, la table de
recherche de l'entrée est lue en deux lectures (le pied de 9 octets, puis la table qu'il
dimensionne) et comparée à `rawSize` et `blockSize` du manifeste ; une table abîmée ou en
désaccord donne `image-corrupt`. Une lecture de secteurs est découpée en trames, chaque trame
manquante est lue dans `image.bin` et décompressée, puis gardée dans un **cache LRU borné**,
partagé par les disques : 32 Mio par défaut, de 2 à 512 Mio (`FrameCacheMiB`), avec ses
compteurs (octets, trames, succès, échecs, évictions). Le noyau et l'initramfs sont décompressés
entiers en mémoire avant la création de l'émulateur.

**Décodeur zstd : celui de v86.** `v86.wasm` exporte le décodeur que v86 emploie pour ses propres
images `.zst` (`zstd_create_ctx`, `zstd_read`, …). `disk.js` instancie `v86.wasm` une seconde
fois dans le worker, avec des importations factices, comme le fait le `zstd_decompress_worker`
de v86. Rien n'est ajouté au dépôt : ni code, ni licence, ni empreinte (le `v86.wasm` est déjà
épinglé par `vendor-v86.py`). L'instance est à part parce que le noyau doit être décompressé
avant que l'émulateur existe, et pour qu'une lecture de disque ne rentre jamais dans le wasm du
processeur en marche. Ce décodeur s'arrête net (trap) sur une trame invalide au lieu de le dire :
l'en-tête de trame est donc vérifié avant (nombre magique, taille annoncée égale à celle de la
table) et l'instance est jetée après un trap. Coût : 13 Mio de mémoire wasm (mesuré).

### La couche d'écriture

L'image publiée n'est jamais écrite. Les blocs que l'invité écrit vont dans une couche :

- **Jetable** (`EngineWriteLayer.Discard`, par défaut) : les blocs restent en mémoire et
  disparaissent à l'arrêt. Pour les tests et les VM sans état.
- **Persistante** : un fichier `osx86/layers/<ensemble>/<disque>.layer`. Un en-tête de 4 Kio
  (`OSX86-LAYER`, format 1, blocs de 4 Kio, taille du disque, nombre de blocs, positions), une
  **table de blocs** (un u32 par bloc : 0 = non écrit, n = emplacement n − 1), puis les blocs
  écrits, ajoutés dans l'ordre de leur première écriture. Choisi plutôt qu'un fichier creux de la
  taille du disque : l'OPFS n'expose pas les trous, un `truncate` à la taille du disque se compte
  dans le quota et ne dit pas ce qui a été écrit ; la table le dit, et le fichier ne pèse que les
  blocs écrits plus 0,1 % de table (124 Kio pour les 130 Mio de linux-base). Un bloc partiel est
  lu entier (couche ou image), modifié et écrit entier ; un bloc est écrit avant son entrée de
  table, donc la table n'est jamais en avance sur les données.
- **Formats versionnés** : `<disque>.layer.meta.json` est un `StoredObjectMetadata` de type
  `layer` (format, image, version de l'image, `engine.v86`), écrit à la création. À la
  réouverture : autre version d'image ou autre taille de disque → `layer-mismatch`, autre
  version majeure de v86 → `engine-version-mismatch`, autre format → `format-unsupported` ;
  rien n'est écrit (169).
- **Un seul écrivain** (E11) : verrou `navigator.locks` `osx86:layer:osx86/layers/<ensemble>`,
  attendu 2 s puis `layer-locked`, plus l'exclusivité du `SyncAccessHandle`.
- **`flush()`** toutes les 5 s tant que la couche est ouverte, à la pause et à l'arrêt (177 A).
- **Taille et suppression** : `ImageStore.ListLayersAsync(ensemble)` et
  `ImageStore.DeleteLayersAsync(ensemble)` (voir [`stockage.md`](stockage.md)) ; pendant la marche,
  `V86Engine.GetDiskStatisticsAsync` donne les compteurs des disques, de la couche et du cache.

### Protocole et C#

La commande `start` porte `kind`, `kernel` et `initrd` (sources), `disks` (source et couche par
disque) et `cacheMiB`. Trois commandes s'ajoutent : `read-screen` (l'écran texte VGA, tenu par le
worker à partir des événements `screen-put-char`, CP437 vers Unicode, comme l'essai E14),
`query-disks` (les compteurs) et `disk-benchmark` (lectures hors invité, pour les mesures).
`V86Engine.ReadTextScreenAsync` et `GetDiskStatisticsAsync` les exposent. Nouveaux codes
d'erreur : `image-corrupt`, `disk-io-failed`, `quota-exceeded`, `layer-locked`, `layer-mismatch`,
`format-unsupported`, `engine-version-mismatch`, `boot-unsupported`.

**Correctif du canal** : `JsEngineWorkerChannel` installe son rappel avant `createHost`, comme
`JsStorageWorkerChannel` ; un `ready` posté par un worker rapide n'est plus perdu (test
`A_ready_posted_before_createHost_returns_is_not_lost`).

### Mesures

Chrome sans tête (Playwright), Windows 11, Ryzen 7 3700X, linux-base 0.1.0 avec 256 Mio, cache
de 32 Mio.

| Mesure | Valeur |
| --- | --- |
| Copie du pack linux-base (73 Mo) dans l'OPFS par la page de test | 0,3 à 0,6 s |
| linux-base : démarrage jusqu'à `OSX86-READY` | 19 à 27 s (quatre démarrages : 24, 25, 26, 27 s ; 19 s sur un autre passage) |
| `ping` de l'agent, aller-retour | médiane 4,4 à 5,8 ms |
| Lecture séquentielle de `hda` hors invité (128 Mio) | 358 Mo/s |
| Lecture aléatoire de 4 Kio hors invité (4 000 lectures sur 130 Mio) | 465 lectures/s (presque chaque lecture décompresse une trame de 1 Mio) |
| Démarrage de linux-base : lectures de l'invité sur `hda` | 438 lectures, 10,5 Mo lus, 16,4 Mo compressés lus dans `image.bin`, 61 trames décodées en 154 ms |
| Mémoire : cache de trames, décodeur | 32 Mio (plein après le démarrage), 13 Mio |
| Couche après l'écriture d'un fichier | 217 Kio (en-tête, table de 124 Kio, blocs écrits) |
| FreeDOS : démarrage jusqu'à `C:\>` | 3,2 s |

La lecture aléatoire est le point faible : une lecture de 4 Kio qui tombe hors du cache coûte la
décompression d'une trame de 1 Mio. Pour l'invité, qui lit surtout près de ce qu'il vient de lire,
le cache suffit (402 succès pour 48 échecs au démarrage). Si un usage montre le contraire, des
trames plus petites (`--block-size` de `pack-image.py`) ou un cache plus grand règlent la chose
sans changer le format.

### Ce que le démarrage réel a trouvé

Démarrer linux-base et rejouer `verify-console.sh` dans la VM a trouvé deux défauts d'image,
corrigés dans les recettes (voir [`images.md`](images.md)) : une extraction Git Windows en CRLF
qui privait l'initramfs de `sd_mod` et `ext4`, et `/dev` laissé en 0660 par le `mdev` de
l'initramfs (aucun usager ne pouvait ouvrir `/dev/null`).

## v86 vendorisé

v86 0.5.473 (paquet npm, commit `315b2b0`), licence BSD 2 clauses, avis conservé dans `LICENSE` ;
les BIOS (`seabios.bin`, `vgabios.bin`) sont ceux que les mainteneurs de v86 construisent depuis
SeaBIOS `rel-1.16.2` (LGPL-3.0, `COPYING.LESSER` conservé). `VENDOR.json` donne la provenance
(adresses, SHA-256 avant et après correctifs). Rien de ce qui est téléchargé n'est exécuté par le
script : il hache en écrivant (taille bornée, fichier `.part` renommé une fois vérifié), copie,
corrige.

```bash
python -I tools/v86/vendor-v86.py            # régénère src/OsX86.Feature/wwwroot/v86
python -I tools/v86/vendor-v86.py --check    # la CI : reconstruit à part et compare octet à octet
```

Correctifs locaux (`tools/v86/patches/`, appliqués dans l'ordre, documentés en tête de chaque
fichier) :

| Correctif | Origine | Pourquoi |
| --- | --- | --- |
| `0001-speaker-drop-stale-dac-requests-pr-1661.patch` | [copy/v86#1661](https://github.com/copy/v86/pull/1661), ouverte, porté sur le `libv86.js` compilé | Sans lui, un gel de 300 ms du fil qui fait tourner v86 tue `aplay` (essai E5) ; avec, la lecture va au bout sans rejoue |

Pour passer à une version plus récente : changer `V86_VERSION`, `V86_COMMIT` et les SHA-256 dans
`vendor-v86.py`, relancer, et retirer les correctifs que l'amont a fusionnés (un correctif qui ne
s'applique plus arrête le script).

## Image Alpine de test

`python -I images/alpine-test/build-alpine-test.py` télécharge l'ISO `alpine-virt` officiel
(SHA-256 épinglé, celui publié à côté de l'ISO), en extrait le noyau `linux-virt` et l'initramfs,
remplace `/init` par `images/alpine-test/init.sh` (un shell busybox sur `ttyS0`, les ports
`hvc0` à `hvc3` libres) et écrit `images/build/alpine-test/` (ignoré par git) avec un
`manifest.json`. Démarrage jusqu'à l'invite `osx86# ` : environ 7 s dans Chrome. Détails dans
[`images/alpine-test/README.md`](../../images/alpine-test/README.md).

## Tests

- `dotnet test --filter Category=unit` : machine d'états, trames série, protocole, `V86Engine`
  contre un worker simulé, `JsEngineWorkerChannel` contre un `IJSRuntime` factice. La CI exige
  80 % de lignes couvertes sur `OsX86` et `OsX86.Feature`.
- `dotnet test --filter Category=guest` : Playwright ouvre `tests/OsX86.Tests/Guest/guest.html`
  dans un Chromium sans tête, la page copie l'image dans l'OPFS, le worker la démarre, le test lit
  `OSX86-READY` et l'invite, écrit une commande et lit sa sortie, échange des octets sur `hvc1`
  dans les deux sens, met en pause (plus rien ne sort), reprend, arrête. Ignoré avec un message si
  l'image n'est pas construite ou si aucun navigateur ne se lance (`OSX86_BROWSER_CHANNEL` pour en
  imposer un). La CI le lance sur les chemins du moteur, chaque nuit et à la demande
  (`guest-tests.yml`).
- À la main : servir la racine du dépôt (par exemple `python -I -m http.server 8765 --bind
  127.0.0.1`) et ouvrir `tests/OsX86.Tests/Guest/guest.html`.

## Sources et licences (plan, section 12)

| Composant | Version | Licence | Source |
| --- | --- | --- | --- |
| v86 | 0.5.473, commit `315b2b0` | BSD-2-Clause | `https://registry.npmjs.org/v86/-/v86-0.5.473.tgz`, `https://github.com/copy/v86` |
| SeaBIOS (BIOS et VGA BIOS) | rel-1.16.2, construit par v86 | LGPL-3.0 | `https://git.seabios.org/seabios.git`, recette `bios/fetch-and-build-seabios.sh` de v86 |
| Noyau Linux `linux-virt`, BusyBox (image de test) | Alpine 3.22.8 x86, noyau 6.12.115-0-virt | GPL-2.0 | `https://gitlab.alpinelinux.org/alpine/aports` (branche `3.22-stable`, `main/linux-lts`, `main/busybox`) ; ISO `https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86/alpine-virt-3.22.8-x86.iso` |

L'image de test n'est jamais publiée ; les images réelles et leurs sources suivent le chantier 4.

### Tests du chantier 6a

- `unit` : `DiskScriptTests` lance `tests/OsX86.Tests/Engine/disk-tests.js` sous Node 22.15 ou
  plus (table de recherche, plan de lecture, en-tête de trame, décodeur de `v86.wasm` et trame
  invalide, cache, entrées, couche jetable et persistante sur un faux OPFS, un seul écrivain,
  refus d'une autre version d'image, d'une autre version majeure ou d'un autre format sans rien
  écrire, quota, entrée faite par `pack-image.py`) ; `ImageBootTests` (options, commande `start`,
  règle 169, requêtes de `V86Engine`) ; `ImageBootSectionTests` et `LayerStoreTests` côté stockage ;
  le `ready` de `JsEngineWorkerChannel`.
- `guest` : `FreeDosGuestTests` (le pack copié dans l'OPFS, `hda` jusqu'à l'invite, écran texte
  lu) et `LinuxBaseGuestTests` : quatre démarrages, couche persistante puis jetable ; `OSX86-READY`,
  l'agent répond à `ping` par `AgentClient`, `verify-console.sh` lancé depuis une console de
  l'agent d'un usager ordinaire tient ses 22 contrôles R134.5, une écriture survit à un arrêt
  propre avec la couche et disparaît en mode jetable. linux-base n'a pas encore d'usager de
  préfixe (chantiers 9 et 10) : le test en ajoute deux en mémoire depuis le shell série root.
  Le job `guest` d'`images.yml` les lance sur les packs construits dans le même flux, sans les
  reconstruire.

## Décisions prises dans ce chantier

- Les fichiers JavaScript et v86 vivent dans `src/OsX86.Feature/wwwroot/`, servis sous
  `_content/OsX86.Feature/`. Ils étaient dans `src/OsX86/wwwroot/` (x86-Lib) jusqu'au chantier 8 :
  `KazoOsPack` n'emballe que les ressources du projet de l'app (ADR 0221 de Kazo), et une app
  installée n'est servie qu'à `_content/<son assemblage>/` (ADR 0206) ; décision 290 B.
- Le module hôte doit être importé depuis un contexte qui a le DOM : dans Kazo, une commande
  `execution: pool` tourne dans un worker sans `IJSRuntime` (ADR 0070), donc le moteur sera créé par
  la Feature (type d'app « Feature », ADR 0243), pas par la commande `x86`. Chantier 8.
- Le worker rapporte l'état ; le C# ne devine rien et refuse les opérations interdites avant de
  les envoyer.
- Le correctif #1661 est porté sur le fichier compilé plutôt que sur les sources de v86 : pas de
  chaîne de construction v86 (Rust, Closure) dans ce dépôt.
- L'image de test vient de l'ISO officiel plutôt que de Docker : pas de démon à lancer, une seule
  empreinte à épingler, et les fichiers de version (`releases/`) ne bougent pas, contrairement aux
  paquets `apk` d'une branche.
