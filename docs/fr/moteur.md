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
| `src/OsX86/wwwroot/engine/` | `v86-host.js` (module importé par le C#, crée le worker) et `v86-worker.js` (héberge v86) |
| `src/OsX86/wwwroot/v86/` | v86 vendorisé : `libv86.js` (corrigé), `v86.wasm`, `seabios.bin`, `vgabios.bin`, `LICENSE`, `COPYING.LESSER`, `VENDOR.json` |
| `tools/v86/` | `vendor-v86.py` (télécharge, vérifie, applique les correctifs) et `patches/` |
| `images/alpine-test/` | `build-alpine-test.py` et `init.sh` : l'image Alpine de test (jamais commise) |
| `tests/OsX86.Tests/Engine/`, `tests/OsX86.Tests/Guest/` | tests `unit` et test `guest` |

Les fichiers de `wwwroot/` sont des ressources web statiques de la bibliothèque `OsX86`, servies
sous `_content/OsX86/` (l'adresse que Kazo OS donne aux fichiers d'une app, ADR 0206 de Kazo).

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

## v86 vendorisé

v86 0.5.473 (paquet npm, commit `315b2b0`), licence BSD 2 clauses, avis conservé dans `LICENSE` ;
les BIOS (`seabios.bin`, `vgabios.bin`) sont ceux que les mainteneurs de v86 construisent depuis
SeaBIOS `rel-1.16.2` (LGPL-3.0, `COPYING.LESSER` conservé). `VENDOR.json` donne la provenance
(adresses, SHA-256 avant et après correctifs). Rien de ce qui est téléchargé n'est exécuté par le
script : il hache en écrivant (taille bornée, fichier `.part` renommé une fois vérifié), copie,
corrige.

```bash
python -I tools/v86/vendor-v86.py            # régénère src/OsX86/wwwroot/v86
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

## Décisions prises dans ce chantier

- Les fichiers JavaScript et v86 vivent dans `src/OsX86/wwwroot/` (x86-Lib), servis sous
  `_content/OsX86/` ; l'empaquetage dans le `.kazoapp` de la Feature est à vérifier au chantier 8
  (ADR 0221 de Kazo).
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
