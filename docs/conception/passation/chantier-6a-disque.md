# Chantier 6a (j0/6a-disk) : note de passation

PR #8 (`j0/6a-disk` → `j0/base`), fusionnée le 2026-11-15 (299931f). Documentation de référence :
`docs/fr/moteur.md`, section « Disques et démarrage décrit par le manifeste » (miroir
`docs/en/engine.md`), et les sections 6a de `images.md`, `stockage.md` et `agent.md`.

## Ce que la suite peut prendre tel quel

- **Démarrer une image du dépôt** : `ImageBoot.CreateStartOptions(manifeste, new ImageBootOptions { LayerMode, LayerSet })`
  puis `engine.StartAsync`. Le noyau, l'initramfs et les disques sont lus à leur position dans
  `image.bin` ; rien n'est extrait. La règle « même version majeure » (169) refuse une image d'une
  autre version de v86 avant toute lecture (`engine-version-mismatch`).
- **Couche d'écriture** : `EngineWriteLayerMode.Discard` (défaut, en mémoire) ou `Persistent`
  avec un `LayerSet` (un identifiant de VM : `osx86/layers/<ensemble>/<disque>.layer`).
  `ImageStore.ListLayersAsync` et `DeleteLayersAsync` mesurent et suppriment ; `locked` tant
  qu'un moteur tourne dessus. Une couche n'est rouverte que par la même version d'image et la
  même version majeure de v86 : une mise à jour d'image demandera une migration ou un point de
  restauration (188), rien n'est prévu pour l'instant.
- **linux-base comme image par défaut** : elle démarre en 19 à 27 s jusqu'à `OSX86-READY`, son
  agent répond, et les 22 contrôles R134.5 tiennent à l'exécution. Elle n'a **aucun usager de
  préfixe** : `LinuxBaseGuestTests` en ajoute deux en mémoire (montages liés sur `/etc/passwd` et
  `/etc/group` depuis le shell série root). Les chantiers 9 et 10 doivent créer les usagers
  `osx86-<préfixe>` pour de bon (dans l'image, ou par une opération de l'agent).
- **Écran texte (chantier 11)** : le worker tient l'écran texte VGA à partir des événements
  `screen-put-char` (CP437 vers Unicode) ; `V86Engine.ReadTextScreenAsync` le rend. Le clavier
  est encore désactivé dans la configuration de v86 (`disable_keyboard`) : la voie Terminal de
  DOS devra l'activer et envoyer les scancodes (essai E14).

## Pièges trouvés

- **Fins de ligne** : Git sous Windows extrayait les recettes en CRLF ; `mkinitfs` lisait alors
  `sd_mod.ko*\r` et l'initramfs de linux-base n'avait ni `sd_mod` ni `ext4`. `.gitattributes`
  impose LF à tout `images/**`. Une image construite en local avant ce correctif est cassée : la
  reconstruire (le bon pack fait 73,07 Mo, pas 70,67).
- **`/dev` en 0660** : le `mdev` de l'initramfs Alpine laisse tous les nœuds en 0660 et linux-base
  n'a pas d'udev. `init.sh` remet `/dev/null`, `zero`, `full`, `random`, `urandom`, `tty`, `ptmx`
  en 0666. L'image Wine (chantier 9), qui part de l'étape `base`, hérite de ce correctif ; tout
  autre nœud dont un programme d'usager aura besoin (son, `/dev/dri`) est à ouvrir de même.
- **`hidepid=invisible`** : un usager ne voit pas les processus des autres, ni l'agent. Un
  script d'usager qui cherche un pid (`pgrep`) ne le trouvera pas ; l'hôte doit le lui passer.
- **Tampon de disque v86** : v86 appelle aussi `get_and_cache`, `get_from_cache` et `get_buffer`
  sur `hda` (chemin de démarrage du BIOS), pas seulement `get` et `set`.
- **Décodeur zstd de `v86.wasm`** : il s'arrête net (trap) sur une trame invalide au lieu de le
  dire ; `disk.js` vérifie l'en-tête avant et jette l'instance après un trap.

## Mesures (Chrome sans tête, Ryzen 7 3700X)

| Mesure | Valeur |
| --- | --- |
| linux-base jusqu'à `OSX86-READY` | 19 à 27 s |
| `ping` de l'agent | médiane 4,4 à 5,8 ms |
| Lecture séquentielle de `hda` hors invité | 358 Mo/s |
| Lecture aléatoire de 4 Kio hors invité | 465 lectures/s |
| Cache de trames / décodeur | 32 Mio (plein après le démarrage, 402 succès pour 48 échecs) / 13 Mio |
| Couche après l'écriture d'un fichier | 217 Kio |
| FreeDOS jusqu'à `C:\>` | 3,2 s |

La lecture aléatoire est le point faible : un échec du cache décompresse une trame de 1 Mio. Si
l'image Wine le montre, des trames plus petites (`--block-size` de `pack-image.py`) ou un cache
plus grand (`FrameCacheMiB`) règlent la chose sans changer le format.

## Non vérifié

Firefox et Safari ; le chargement des modules sous Kazo (chantier 8) ; une couche de plusieurs
Gio (l'invité n'a écrit que quelques blocs dans les tests) ; une coupure de courant en pleine
écriture (au pire un bloc déchiré, la table n'étant jamais en avance sur les données).
