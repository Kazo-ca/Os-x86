> [English version](../en/storage.md)

# Le stockage : manifeste d'images, téléchargement vérifié, dépôt OPFS

Jalon 0, chantier 3 (`j0/3-storage`). Le stockage est la pièce qui fait entrer une image dans le
navigateur et l'y garde : un manifeste publié à côté de morceaux de 32 Mio, un Web Worker qui
les télécharge, les vérifie (SHA-256) et les écrit dans l'origin private file system (OPFS), un
dépôt d'images par identifiant et version, un état de reprise, un verrou d'écriture, et la
structure des formats versionnés de la section 13 du plan. Décisions 169, 177 A, 188, 202 A,
203 A, E11 ; essais [E2](../essais/e2-stockage.md) (OPFS) et [E3](../essais/e3-distribution.md)
(distribution), dont ce chantier industrialise le code. Les recettes qui produisent les images et
leurs manifestes sont le chantier 4 ; l'ouverture d'une image par le moteur et les couches
d'écriture des disques, le chantier 6a (voir « Couches d'écriture » plus bas et [`moteur.md`](moteur.md)).

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Storage/` | `ImageManifest` (lecteur et validateur), `StorageLayout` (noms et chemins OPFS), `StoredObjectMetadata` (formats versionnés), `ResumePlan`, `DownloadStateMachine` et `DownloadProgress`, `ImageStore` (la façade), `ImageCatalog` et `KnownImage` (les images connues, chantier 6b), `IStorageWorkerChannel` et `JsStorageWorkerChannel` (l'interop JS), le protocole, les exceptions |
| `src/OsX86/wwwroot/storage/` | `storage-host.js` (module importé par le C#, crée le worker) et `storage-worker.js` (téléchargement, SHA-256, OPFS, verrou) |
| `tests/OsX86.Tests/Storage/` | tests `unit` (manifeste, métadonnées, plan de reprise, machine d'état, `ImageStore` contre un worker simulé, canal JS contre un `IJSRuntime` factice) et test `guest` (`ImageDownloadTests`, `storage.html`, `ChunkImageServer`) ; `ImageCatalogTests` (chantier 6b) |

Comme pour le moteur, les fichiers de `wwwroot/` sont servis sous `_content/OsX86/`.

## Le manifeste (`manifest.json`)

Le schéma fait foi pour les recettes du chantier 4 comme pour ce lecteur. Format 1 :

```json
{
  "format": 1,
  "image": "alpine-test",
  "version": "1.0.0",
  "engine": { "v86": "0.5.473" },
  "size": 838860800,
  "chunkSize": 33554432,
  "chunks": [ { "sha256": "<64 hex minuscules>" }, { "sha256": "..." } ],
  "files": { "kernel": { "offset": 0, "size": 12345 }, "initramfs": { "offset": 12345, "size": 678 } }
}
```

- `format` : entier, le numéro de format ; cette version ne lit que le format 1. Un autre numéro
  refuse l'image en le disant (`format-unsupported`) **sans rien écrire** (décision 169). Un ajout
  de champ compatible n'incrémente pas le numéro : les champs inconnus sont ignorés.
- `image` : identifiant en minuscules, chiffres et tirets (1 à 64 caractères) ; `version` :
  version sémantique. Ce sont aussi les noms des dossiers OPFS.
- `engine` : les versions de moteur pour lesquelles l'image est construite, `v86` au moins. Le
  lecteur l'expose ; la règle « même version majeure » (169) est appliquée par
  `ImageBoot.CreateStartOptions` depuis le chantier 6a, avant toute lecture ou écriture.
- `size` : taille de la charge utile en octets (au plus 100 000 morceaux de 64 Mio) ; `chunkSize` :
  taille de chaque morceau sauf le dernier (64 Kio à 64 Mio, parce que le worker garde un morceau
  en mémoire par flux parallèle ; les recettes utilisent 32 Mio, la bonne taille d'après E3).
- `chunks` : un SHA-256 en hex minuscules par morceau, dans l'ordre, exactement
  `ceil(size / chunkSize)` entrées (100 000 au plus : cinq chiffres dans les noms). Les morceaux
  sont les fichiers `chunk-00000.bin`, `chunk-00001.bin`, … à côté du manifeste.
- `files` : les fichiers logiques de la charge utile (`kernel`, `initramfs`, `rootfs`, …) avec
  leur position et leur taille ; peut être vide.

Les recettes du chantier 4 (`pack-image.py`) ajoutent des champs facultatifs que ce lecteur
ignore sans erreur, comme tout champ inconnu : `rawSize` et `blockSize` (la charge utile est un
flux zstd « seekable » d'une image disque brute ext4, attachée en `hda`) et un `sha256` par
entrée de `files`. Le dépôt stocke les morceaux tels quels ; la décompression n'est pas de son
ressort.

`ImageManifest.Parse(texte)` valide tout cela ; `ImageManifest.Parse(octets, sha256Attendu)` compare
d'abord l'empreinte au SHA-256 que le C# connaît (décision 203 A : le manifeste est la racine de
confiance, l'appelant doit connaître son empreinte) et refuse l'image (`manifest-integrity`) si
elle diffère. Le manifeste lui-même est borné à 1 Mio (le worker refuse d'en lire plus), doit
être de l'UTF-8 valide sans marque d'ordre des octets, et une clé en double dans `files` ou
`engine` le rend invalide.

## Comment ça marche

```
C# (ImageStore) ──JSON──> JsStorageWorkerChannel ──import──> storage-host.js ──postMessage──> storage-worker.js
                <──JSON── [JSInvokable] OnWorkerMessage <──── callback <──── postMessage <────┘  (fetch, SubtleCrypto, OPFS)
```

Le C# décide, le worker exécute. `ImageStore.DownloadAsync(source, progression)` :

1. **`fetch-manifest`** : le worker récupère `manifest.json` (`cache: 'no-store'`, sans
   identifiants), le hache avec `SubtleCrypto` et compare à l'empreinte attendue ; le C# refait la
   comparaison et valide le manifeste. Rien n'est écrit tant que le manifeste n'est pas accepté.
2. **`read-state`** : le worker lit ce qu'il y a déjà dans l'OPFS pour cette image et cette
   version (état de reprise, métadonnées). Le C# calcule le **plan de reprise** (`ResumePlan`) :
   les morceaux vérifiés sont gardés, les autres sont demandés dans l'ordre ; un état qui appartient
   à un autre manifeste (empreinte différente) ou qui nomme un morceau hors du manifeste est jeté
   (`reset`) et tout est repris.
3. **`download`** : le worker prend le verrou de l'image, écrit la copie locale du manifeste et les
   métadonnées, ouvre `image.bin` avec un `FileSystemSyncAccessHandle` (taille fixée à `size`),
   puis, pour chaque morceau demandé, `fetch` le fichier `chunk-NNNNN.bin` dans un tampon
   préalloué, le hache (`SubtleCrypto.digest('SHA-256')`, 1,1 à 1,25 Go/s d'après E3 ; le SHA-256
   de .NET interprété ferait 35 Mo/s), compare au manifeste, l'écrit à sa position, `flush()`, et
   seulement alors l'ajoute à l'état de reprise. Les messages `progress` donnent les octets et les
   morceaux vérifiés, le total, et le débit reçu sur les cinq dernières secondes. Quand tous les
   morceaux sont vérifiés, les métadonnées passent à `complete: true` et le worker répond `done`.

Deux morceaux à la fois par défaut (`ImageSource.Parallelism`, 1 à 4). E3 a mesuré un seul flux ;
un second couvre la latence de la requête suivante sans doubler la mémoire (un tampon de
32 Mio par flux). Le vrai hébergement R2 n'a pas été mesuré (202 A) : à ajuster quand il le sera.

### Reprise, réessais, annulation

- **Erreur réseau ou 5xx** (aussi 408 et 429) : le morceau est réessayé avec un délai croissant
  (500 ms, 1 s, 2 s, … 30 s au plus, avec une gigue), 8 tentatives ; au-delà, le téléchargement
  échoue avec `network`. Une coupure coûte au plus le morceau en cours (E3). Un serveur qui ne
  répond plus coûte une tentative après 30 s sans octet (chaque `fetch` a son propre
  `AbortController`, interrompu par ce chien de garde ou par l'annulation). Un 404 ou un 403
  échoue tout de suite (`http`).
- **Empreinte fausse** : le morceau est redemandé jusqu'à trois fois, puis `chunk-integrity`.
- **Rechargement de la page, fermeture du navigateur, plantage** : l'état de reprise est dans
  l'OPFS (`download.json` et `download.1.json`, écrits en alternance avec un numéro de séquence :
  l'empreinte du manifeste et les index vérifiés ; une écriture déchirée n'abîme qu'une copie, la
  plus récente valide est lue), écrit après le `flush()` du morceau, donc il ne nomme jamais un
  morceau qui n'est pas sur le disque. Un `image.bin` absent ou d'une autre taille que celle du
  manifeste invalide l'état : tout est repris. Au téléchargement suivant, le plan ne demande que
  les morceaux manquants.
- **Annulation** : le jeton du C# envoie `cancel` ; le worker interrompt les `fetch` en cours,
  ferme le fichier, libère le verrou et répond `cancelled` (le C# attend cet accusé jusqu'à 5 s
  pour que le verrou soit libre quand l'appelant reprend la main). Les morceaux vérifiés restent.
- **Quota** : un `QuotaExceededError` du navigateur devient `quota-exceeded`.

`DownloadPhase` et `DownloadStateMachine` nomment les phases (`FetchingManifest`, `Planning`,
`Downloading`, `Completed`, `Failed`, `Cancelled`) et les transitions permises ; rien ne peut avoir
été écrit avant `Downloading`.

## Le dépôt dans l'OPFS

```
osx86/
  images/
    <image>/
      <version>/
        manifest.json     la copie du manifeste publié
        image.bin         les morceaux concaténés (la charge utile)
        download.json     l'état de reprise : {"format":1,"seq":3,"manifestSha256":"…","verified":[0,1,2]}
        download.1.json   sa copie alternée (le numéro de séquence le plus haut fait foi)
        image.meta.json   les métadonnées versionnées (voir plus bas), complete:true à la fin
```

`ImageStore` : `ListAsync` (chaque version avec sa taille, son occupation réelle, `Complete`,
son format et ses versions de moteur ; `IsSupported` dit si cette version d'Os-x86 peut
l'ouvrir), `DeleteAsync` (refusé par `locked` pendant un téléchargement), `EstimateAsync`
(`navigator.storage.estimate()` : usage et quota de l'origine, plus l'occupation du dépôt),
`ReadManifestAsync` (la copie locale, validée et épinglée à l'empreinte que l'état de reprise a
enregistrée : un format inconnu ou une copie modifiée est refusée), `VerifyAsync` (relecture et
hachage de chaque morceau sur place sous le verrou, 1 s pour 800 Mio d'après E3), et
`ImageStore.Locate(manifeste, "kernel")` qui rend le chemin OPFS de `image.bin` avec la position et
la taille du fichier logique. Depuis le chantier 6a, le manifeste lit aussi `rawSize` et
`blockSize` de chaque fichier (`ImageFileEntry.IsCompressed`) et la section `boot`
(`ImageBootSection` : `kind`, `cmdline`, `disks`, `readyMarker`, `prompt`, `memoryMiB`), et le
moteur lit le noyau, l'initramfs et les disques à leur position dans `image.bin`, sans extraction.

### Couches d'écriture (chantier 6a)

Les écritures d'un disque ne touchent jamais `image.bin` : elles vont dans une couche par disque,
`osx86/layers/<ensemble>/<disque>.layer`, avec ses métadonnées versionnées
`<disque>.layer.meta.json` (`StoredObjectMetadata.ForLayer`, type `layer`). Le moteur les écrit
(format décrit dans [`moteur.md`](moteur.md)) ; le stockage les mesure et les supprime :

- `ImageStore.ListLayersAsync(ensemble)` : une `StoredLayer` par disque (taille, métadonnées) ; une
  couche qu'un moteur tient ouverte est rendue avec `InUse` et sans taille (le moteur la donne).
- `ImageStore.DeleteLayersAsync(ensemble)` : supprime l'ensemble sous le verrou
  `osx86:layer:osx86/layers/<ensemble>` que le moteur prend aussi ; `locked` tant qu'un moteur
  tourne dessus.
- `StorageLayout.LayerDirectory` et `LayerPath` donnent les chemins ; le nom d'ensemble suit la
  règle des identifiants d'image.
- Depuis le chantier 6b, **une VM a son ensemble** : `vm-<32 chiffres hexadécimaux de son GUID>`,
  rangé avec elle dans le registre (`VmRecord.LayerSet`) et supprimé par `x86 vm remove`
  ([VM](vm.md#linux-base-par-défaut-chantier-6b)).

## Catalogue des images connues (chantier 6b)

L'origine des images est une **configuration**, jamais une adresse écrite dans le code :
l'hébergement (R2, Pages) n'est pas encore décidé. `ImageCatalog` liste, pour chaque version
d'image que l'app sait télécharger, l'adresse de son manifeste publié et l'empreinte SHA-256 que ce
manifeste doit avoir (203 A : l'empreinte vient de la configuration, jamais de l'origine) :

```json
{"format": 1, "images": [
  {"image": "linux-base", "version": "0.1.0",
   "manifestUrl": "images/build/linux-base/pack/manifest.json",
   "manifestSha256": "d5c9c8f0…"}
]}
```

- `ImageCatalog.Parse(texte, adresseDeBase)` lit ce format 1 (propriétés inconnues ignorées,
  commentaires et virgules finales acceptés, 256 Kio au plus) ; un autre format est refusé
  (`format-unsupported`), une entrée invalide ou en double aussi (`manifest-invalid`, qui nomme le
  problème). L'adresse doit finir par `/manifest.json` ; relative, elle est résolue contre
  l'adresse de la page, puisqu'un worker résoudrait une adresse relative contre son propre script.
- `SourceOf(image, version)` rend l'`ImageSource` du téléchargement (dossier du manifeste,
  empreinte), avec la règle de `ImageSource` : https, http seulement en boucle locale.
- `StoreBootImageSource` (couche VM) s'en sert : image complète dans le dépôt, utilisée telle
  quelle ; absente ou partielle, téléchargée depuis l'origine du catalogue (reprise comprise) ;
  absente du catalogue, `image-unavailable`.

**Source locale.** L'hôte de développement `OsX86.Os` sert chaque pack construit dans
`images/build/<image>/pack/` au même chemin (manifeste et morceaux seulement, jamais publiés), et la build inscrit l'empreinte du `manifest.sha256` du pack en métadonnée
d'assemblée (`OsX86.DevPack`). Au démarrage, `DevImages` lit chaque manifeste, le vérifie contre
cette empreinte et en fait le catalogue. `-p:OsX86DevImages=false` les retire. Les tests `guest`
font de même avec un serveur statique local (`StaticFileServer`). Le catalogue de production
viendra avec l'hébergement des images.

### Verrou d'écriture (E11) et `flush()` (177 A)

Un seul écrivain par version d'image : le worker demande `navigator.locks.request("osx86:image:<image>@<version>")`
et le garde pendant tout le téléchargement ou la suppression ; un second écrivain attend 2 s au
plus (le temps que le navigateur rende le verrou d'un worker tué par un rechargement) puis reçoit
`locked` au lieu d'attendre indéfiniment. Le verrou est libéré par le navigateur si la page est fermée ou
plante, sans fichier de verrou ni échéance à gérer : c'est pourquoi il est préféré à un fichier de
verrou. Sans l'API Web Locks (le worker le dit dans `ready`), l'exclusivité de
`createSyncAccessHandle()` sur `image.bin` joue le même rôle : le second ouvreur échoue, traduit en
`locked`. La vérification prend le même verrou, pour ne pas lire sous un téléchargement. Le
worker ouvre `image.bin` avant d'écrire quoi que ce soit d'autre, et réessaie cinq fois à 200 ms
quand le navigateur n'a pas encore rendu le handle d'un worker terminé (rechargement de page).

`flush()` : après chaque morceau avant que l'état le nomme, toutes les 5 s tant qu'un fichier
est ouvert en écriture (classe `WritableFile` du worker, prête pour les disques des prochains
chantiers), et à la fermeture.

## Formats versionnés (plan, section 13)

Chaque objet stocké porte un fichier de métadonnées à côté de lui, aujourd'hui `image.meta.json`,
demain le même fichier à côté de chaque disque, couche et point de restauration
(`StoredObjectKind` : `image`, `disk`, `layer`, `restore-point`). Format 1 :

```json
{ "format": 1, "kind": "image", "image": "alpine-test", "imageVersion": "1.0.0",
  "engine": { "v86": "0.5.473" }, "created": "2026-11-12T10:00:00+00:00", "complete": true }
```

`StoredObjectMetadata.Parse` refuse un `format` inconnu en le disant, sans écrire (169). Les
migrations de la décision 188 (lire le format majeur précédent, convertir après un point de
restauration) viendront avec le premier changement de format ; ce chantier pose la structure et le
lecteur. L'état de reprise porte aussi son `format`.

### Ce qui n'est pas protégé

Comme le dit la section 13 du plan : une réinitialisation d'usine de Kazo efface l'OPFS ; Safari
efface le stockage d'une origine après sept jours sans interaction (E10) ; le JavaScript d'une app
Kazo de la même origine peut lire et écrire le même OPFS (D61.1, 182) ; et aucun navigateur
n'accorde `persist()` sans geste de l'usager (E2), donc une image peut être évincée par le
navigateur : `EstimateAsync` sert à l'afficher, la persistance et l'avertissement d'éviction
relèvent de l'ADR 0250 de Kazo et du chantier 8.

## Erreurs

`StorageException` avec un code de `StorageErrorCodes` : `opfs-unavailable`,
`manifest-fetch-failed`, `manifest-integrity`, `manifest-invalid`, `format-unsupported`,
`chunk-integrity`, `network`, `http`, `write-failed`, `quota-exceeded`, `locked`, `not-found`,
`protocol`, `worker-error`, `disposed`. Une annulation est une `OperationCanceledException`. Une
erreur du worker hors de toute requête (plantage, message mal formé) est remontée par
`UnroutedErrorReported` et fait échouer les requêtes en attente.

## Tests

- `dotnet test --filter Category=unit` : le manifeste (validation champ par champ, formats
  inconnus, empreintes), les métadonnées, le plan de reprise, la machine d'état, `ImageStore`
  contre `SimulatedStorageWorkerChannel` (téléchargement complet, reprise, remise à zéro,
  refus avant toute écriture, erreurs, annulation, liste, suppression, estimation,
  vérification), `JsStorageWorkerChannel` contre un `IJSRuntime` factice.
- `dotnet test --filter Category=guest` : `ImageDownloadTests` ouvre
  `tests/OsX86.Tests/Storage/storage.html` dans un Chromium sans tête ; `ChunkImageServer` sert
  une image de 800 Mio générée à la volée (motif déterministe, jamais sur le disque, jamais
  commise ; `OSX86_STORAGE_TEST_MIB` pour changer la taille) et simule, comme E3, deux 503 et
  trois connexions coupées après 20 Mio. L'image se télécharge, se vérifie sur place, est listée
  et comptée dans l'estimation ; un second worker est refusé par le verrou ; après un
  rechargement de la page en plein téléchargement, l'état de reprise ne nomme que des morceaux
  servis et le téléchargement suivant ne demande que les manquants. Mesuré sur la machine de
  développement : 800 Mio en 7 s avec les pannes, reprise de 22 morceaux sur 26 en 3,7 s. Hors de
  la suite `unit` ; la CI le lance sur les chemins du moteur et du stockage, chaque nuit et à la
  demande (`guest-tests.yml`).
- À la main : servir la racine du dépôt et ouvrir `tests/OsX86.Tests/Storage/storage.html`.

## Décisions prises dans ce chantier

- Le C# décide, le worker exécute : la validation du manifeste et le plan de reprise sont en C#
  (testables sans navigateur) ; le worker ne télécharge que la liste qu'on lui donne et revérifie
  les noms avant de créer un dossier.
- Verrou par `navigator.locks` (attente bornée à 2 s), libéré par le navigateur, plutôt qu'un
  fichier de verrou avec échéance ; repli sur l'exclusivité du `SyncAccessHandle`.
- Deux morceaux en parallèle par défaut, quatre au plus ; à remesurer sur R2.
- Une seule charge utile `image.bin` par version plus un plan des fichiers logiques, plutôt qu'un
  fichier OPFS par fichier logique : l'écriture reste séquentielle par morceau et la position d'un
  fichier vient du manifeste.
- `storage-worker.js` tient en un fichier d'environ 1 000 lignes (dont 60 de protocole en tête) : un
  worker se charge par URL, et le couper en deux aurait ajouté un `importScripts` et un second
  téléchargement pour séparer ce qui ne se réutilise pas ailleurs.
- Non vérifié ici : Firefox et Safari (E3 a vu Firefox se comporter comme Chrome sur le
  téléchargement ; Safari n'a pas de Mac pour le mesurer), le vrai R2 (origine à obtenir, 202 A),
  le chargement du module sous Kazo (chantier 8).
