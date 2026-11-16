# Chantier 10 (j0/10-prefixes-and-detection) : note de passation (2026-11-16)

PR https://github.com/Kazo-ca/Os-x86/pull/14 (`j0/10-prefixes-and-detection` → `j0/base`), fusionnée (2342c91). Référence : `docs/fr/programmes.md`.

## Ce que la suite peut prendre tel quel

- `ProgramDetector` (src/OsX86/Programs) : pur, sans état, `Detect(span, ext)` et `DetectAsync(mem, ext, ct)`, nom `ca.kazo.x86.programs` : à déclarer comme détecteur de format quand Kazo aura l'ADR 0251 (252 A).
- `Ix86.DetectProgramAsync` et `Ix86.RunProgramAsync(ProgramRunOptions)` → `IVmConsole` (code de sortie par `Exited`) : ce que BoxedWine (chantier 13) et le Terminal appelleront. Permission `x86.programs.launch` : chantier 8.
- Préfixes : `PrefixId`, `Prefix`, `VmRecord.Prefixes`, `PendingPrefixDeletions` ; `VmManager.CreatePrefixAsync`, `DeletePrefixAsync`, `ListPrefixesAsync`, `ResolvePrefixAsync`, `ChooseVmAsync`. Profil livré `wine` (préfixe par défaut `default`).
- Agent protocole 2 : `prefix-ensure`, `prefix-delete`, `prefix-list`, `put-file`, `sync`. Le client accepte encore la version 1.
- Images : linux-base a `/usr/lib/osx86/prefix-admin` et un crochet `/usr/lib/osx86/prefix.d/` (la Wine y met `50-wine`) ; `/home` sur le disque, racine en lecture seule par point de montage, ext4 avec journal et 256 Mio libres (`OSX86_FREE_MIB`). L'étape slim de `build-wine.py` réinstalle init, agent et prefix-admin : `--from slim` suffit après un changement de ceux-ci.

## Pièges trouvés

- **v86 jette les écritures hôte → invité au-delà des tampons de réception** : le worker découpe par 1 Kio et chaque morceau prend un tampon de l'invité ; une ligne de 49 Kio s'est perdue. Requêtes de l'agent : rester sous ~8 Kio, une à la fois. Correctif possible plus tard dans le worker (file d'attente tant que la queue virtio n'a pas de tampon).
- **Console de Wine en mode brut** : LF = Ctrl+J ; le relais envoie maintenant CR. Elle passe le tty en brut à la première lecture et jette ce qui a été tapé juste avant. Les blancs arrivent en `CSI n C` (le filtre les rend en espaces).
- Le `wineserver` d'un `exec-in-prefix` meurt avec la commande : session Wine par préfixe lancée à part (`prefix-session start/wait`), arrêtée 30 s après le dernier programme ; une console du préfixe la garde.
- Dans `prefix-admin` (sh), une fonction partage les variables de l'appelant : `run_hooks` écrasait `changed`, d'où un code 10 au lieu de 0 à la création (corrigé).
- Une continuation de `Exited` peut passer après le délai d'une horloge manuelle : les tests attendent que le minuteur soit posé (`PendingTimers`) avant d'avancer l'horloge.
- GateGuard : un mot comme `truncate` ou `cgroup.kill` dans une commande Bash la fait prendre pour destructive ; passer par l'outil d'édition. Les heredoc Bash mangent les doubles barres obliques inverses.
- `ImageDownloadTests` instable (27 requêtes au lieu de 26 une fois sur trois), sans rapport.

## Mesures

Premier `x86 run` 37,0 à 37,7 s (téléchargement compris), second 3,3 à 3,6 s, après redémarrage 12,8 à 15,3 s ; Ctrl+C → code 58.
