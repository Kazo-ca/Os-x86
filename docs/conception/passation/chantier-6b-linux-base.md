# Chantier 6b (j0/6b-linux-base-default) : note de passation

PR #11 (`j0/6b-linux-base-default` → `j0/base`), fusionnée le 2026-11-15 (7ac68e3). Documentation de référence : `docs/fr/vm.md`,
section « linux-base par défaut (chantier 6b) », et `docs/fr/stockage.md`, section « Catalogue des
images connues (chantier 6b) » (miroirs `docs/en/vm.md`, `docs/en/storage.md`).

## Ce que la suite peut prendre tel quel

- **`x86 vm create <nom>` sans `--profile` prend `linux-base`** ; `alpine-test` reste un profil de test.
- **Images** : `StoreBootImageSource(ImageStore, ImageCatalog)` est l'`IBootImageSource` d'un hôte
  avec stockage. Image complète dans l'OPFS : utilisée ; absente ou partielle : téléchargée par le
  worker du chantier 3 depuis l'origine du catalogue ; progression par `VmManager.ImageProgress`.
  `BootImage(ImageManifest)` situe noyau, initramfs et disques par `ImageStore.Locate` ;
  `BootImage.CreateStartOptions(mémoire, ensemble)` passe par `ImageBoot.CreateStartOptions`.
- **Catalogue** : `ImageCatalog.Parse` (format 1 : image, version, `manifestUrl`, `manifestSha256`).
  Aucune adresse en dur. Le catalogue de production attend l'hébergement (R2 ou Pages).
- **Couche par VM** : `VmRecord.LayerSet` = `vm-<guid sans tirets>`, rangé dans le registre,
  supprimé par `VmManager.RemoveAsync` avant l'enregistrement (`vm-busy` si un moteur la tient,
  nouveau code `storage-failed`). `IBootImageSource.DeleteLayersAsync`.
- **Hôte** : `VmHost.Configure(VmHost.ForBrowser(js, catalogue))` installe moteur, stockage et
  catalogue avant la première commande ; `X86Runtime` le lit. **Le chantier 8 (Feature) doit faire
  de même** depuis la Feature.
- **x86 shell sans préfixe** (décision documentée dans `vm.md`) : si ni `--prefix` ni le profil ne
  donnent de préfixe et que le profil déclare un port série, la console série root de l'invité,
  avec un avis sur la sortie d'erreur ; sinon `no-prefix`. Quand les usagers de préfixe existeront
  (chantiers 9 et 10), donner un préfixe par défaut au profil `linux-base`.

## Pièges trouvés (dans Kazo lui-même, premiers essais)

- **ADR 0070 de Kazo** : une commande tourne par défaut dans un worker du pool, runtime .NET à
  part : les statiques du thread principal (VmHost, VM en marche) n'y sont pas. `X86FeatureApp`
  déclare maintenant `[AppManifest(Execution = AppExecutionHost.MainThread)]`
  (`"execution":"mainThread"` dans le `manifest.json` commis).
- **Dossier privé** : `KazoDirectory.CreateDirectoryAsync` remonte jusqu'à `~/.config`, que l'app
  ne peut pas lire : le premier `x86 vm create` d'un usager échouait (`CapabilityDenied`).
  `KazoX86Files` passe par `SettingsTextFile.EnsureDirectoryAsync`, comme les réglages de Kazo.
- **Ressources statiques liées** : un `Content` hors du projet avec `Link="wwwroot\..."` est servi
  vide (200, 0 octet) par le serveur de développement. Les packs sont déclarés par
  `DefineStaticWebAssets` avec le dépôt comme racine ; le `BasePath` est ignoré pour le projet
  courant, d'où le chemin servi `images/build/<image>/pack/`, le même que dans le dépôt.
- **CI** : le test `guest` du chantier (`X86ShellLinuxBaseGuestTests`) a besoin du pack linux-base.
  `guest-tests.yml` ne le construit pas (sauté) ; le job `guest` d'`images.yml` le lance grâce à son
  filtre `FullyQualifiedName~LinuxBaseGuestTests`. Si la session du chantier 9 change ce filtre,
  garder ce test dedans.

## Mesures (Chrome sans tête, Ryzen 7 3700X, origine locale)

| Mesure | Valeur |
| --- | --- |
| Premier `x86 shell` jusqu'à une réponse, téléchargement compris | 16,0 à 18,1 s |
| Téléchargement et vérification du pack (69,7 Mio, local) | < 1 s (~280 Mio/s ; ~120 Mio/s dans Kazo) |
| Second `x86 shell`, image déjà là | 16,1 à 17,6 s |
| Test `guest` entier (trois démarrages) | 49 s |

## Gardé après relecture

- La couche est supprimée avant l'enregistrement de la VM : si l'écriture du registre échoue
  ensuite, la VM reste listée avec un disque revenu à l'image (rare ; l'inverse laisserait une
  couche orpheline).
- Un téléchargement tourne sous le verrou de cycle de vie de sa VM : `x86 vm stop` de cette VM
  attend sa fin ; une VM qui attend le téléchargement d'une autre ne voit pas la progression.

## Non vérifié

Origine réelle (réseau, hébergement) ; Firefox et Safari ; un usager de Kazo autre que l'invité ;
Ctrl+C du terminal de Kazo vers la console série ; une mise à jour de version d'image avec une
couche existante (la couche est refusée par sa version, aucune migration n'est prévue, 188).
