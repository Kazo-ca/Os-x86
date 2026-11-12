> [English version](../en/features.md)

# Fonctionnalités d'Os-x86

Tenu à chaque PR (plan, section 12). Les numéros entre parenthèses renvoient aux décisions du [registre](../conception/registre-reconcilie.md) ; les chantiers, à [`jalon-0-execution.md`](../conception/jalon-0-execution.md).

## Disponibles

| Fonctionnalité | Depuis | Note |
| --- | --- | --- |
| Commande `x86` : `--version`, `vm list` | 0.1.0 | Empaquetée comme commande de terminal en attendant le type d'app « Feature » de Kazo OS (ADR 0243) |
| Paquet de contrats `Os-x86.Interface` : `Ix86` (version, liste des VM) | 0.1.0 | Générique, aucun type Wine (29 C) ; API publique suivie dans `PublicAPI.Unshipped.txt` |
| Images invitées : outil de paquetage (`pack-image.py`, zstd par trames, morceaux de 32 Mio, manifeste format 1), recettes linux-base (Debian trixie i386, noyau `linux-virt` d'Alpine, init, durcissement 210 A), Alpine de test et FreeDOS, test d'image (22 contrôles R134.5), sources et licences, workflow `images.yml` (Pages et R2 préparés) | 0.1.0 | Chantier 4, [`images.md`](images.md) ; 201 A, 202 A, 210 A ; racine en disque ext4 (`hda`), 9p réservé au dossier partagé ; démarrage de linux-base au chantier 6 |
| Moteur v86 dans un Web Worker : `IEngine` (démarrage, arrêt, pause, états), console série et virtio-console (flux brut et par lignes), pont OffscreenCanvas, v86 0.5.473 vendorisé avec le correctif #1661, image Alpine de test, tests `unit` et `guest` | 0.1.0 | Chantier 2, [`moteur.md`](moteur.md) ; D3, 187 A ; interne à x86-Lib, pas encore exposé par `Ix86` |
| Stockage : manifeste d'image (format 1, SHA-256 par morceau de 32 Mio, empreinte attendue), Web Worker de téléchargement et de vérification (`SubtleCrypto`, écriture OPFS par `SyncAccessHandle`, reprise après coupure et rechargement, réessais, annulation, progression), dépôt d'images dans l'OPFS (liste, suppression, occupation, `estimate()`), verrou d'écriture par `navigator.locks`, `flush()` périodique, métadonnées de format versionné, test `guest` de 800 Mio avec pannes | 0.1.0 | Chantier 3, [`stockage.md`](stockage.md) ; 169, 177 A, 188, 203 A, E11 ; interne à x86-Lib, pas encore exposé par `Ix86` |

## En cours (jalon 0, programmes console)

| Fonctionnalité | Chantier | Décisions |
| --- | --- | --- |
| Agent invité (JSON par ligne, cgroup par lancement, console par pty) | 5 | 205 A, 206 A, 213 A |
| Registre des VM, cycle de vie, `x86 vm`, `x86 shell`, profils | 6 | 165, 166, 183, 190, 212 A |
| Pages admin, rôle, journal personnel | 7 | 137, 140 B, 156, 160 |
| Type « Feature », permissions déclarées, `GetServiceAsync<Ix86>()` | 8 | D33, D10, 176 A |
| Image Wine et bouteille gabarit | 9 | 152 A, 187 A, 204 A |
| Préfixes, détection par en-tête, `./hello.exe` console dans le terminal | 10 | 162 A, 193, 200 B |
| Programme DOS texte dans le terminal | 11 | 111, 214 A, 215 A, 217 A |
| Réseau privé, relais, fenêtre de permission, page des règles | 12 | 161, 174 A, 176 A |
| BoxedWine minimal (dépôt séparé) | 13 | 51 D, 62 B |

## Prévues

Jalons 1 à 4 du [plan](../conception/plan-2026-11-11.md), section 8 : BoxedWine et administration complètes, Linux et DOS graphiques, son, entrées, Windows graphique (J3), fin de V1.
