# Essai E5 — son : SB16 dans v86, pont worker → page, saccades, latence, mélangeur

Date : 2026-11-10. Code jetable dans `essais/e5-son/` (ligne E5 des études prévues, questions 133 et 75). Cet essai mesure, dans Chrome headless piloté par Playwright, le son produit par un invité Linux (l'image de E1 plus ALSA et PipeWire) sur la Sound Blaster 16 émulée par v86 : continuité, trous, rejoues (bogue v86 #1660 et correctif #1661), latence entre une marque série et le premier échantillon rendu, CPU du renderer, avec v86 soit dans le fil principal de la page (haut-parleur natif de v86), soit dans un Web Worker avec un pont direct worker → AudioWorklet ; démarrage de l'audio après un geste ; `aplay` au repos, page sous charge, VM sous charge, worker gelé ; PipeWire avec deux flux et un muet par flux. Ne sont **pas** couverts (voir « Limites ») : une vraie carte son (tout est mesuré en JavaScript, rien n'a été écouté), Firefox, Edge et Safari, le son d'un programme Wine, la pile MIDI/OPL.

## But

Savoir si « son continu avec Kazo au repos et sous charge » est atteignable avec v86 : quelle architecture de pont audio tient quand le fil principal est occupé, ce que coûte le son en CPU, quelle latence attendre, et si un mélangeur dans l'invité (PipeWire) permet un muet par programme. Nourrit 133 (son dans Os-x86) et 75. En cas d'échec : son en V2, coupé par défaut.

## Ce qui a été construit

Tout est dans `essais/e5-son/` (README de 10 lignes).

- **Image invité** (`Dockerfile`, copie de celui de E1 plus une couche son) : Alpine 3.24 linux/386, `linux-virt` 6.18.62, Wine 11 et Xorg (couches de E1, inchangées), **plus** `alsa-utils` (`aplay`, `amixer`), `alsa-lib`, `pipewire` 1.6.8, `wireplumber` 0.5.15, `pipewire-alsa`, `pipewire-tools` (`pw-play`, `pw-cli`, `wpctl`) : +27 Mio installés (582 → 610 Mio), 38 paquets. **`snd-sb16` est déjà un module de `linux-virt`** (avec `snd-pcm`, `snd-mpu401-uart`, `snd-opl3-lib`, `snd-sb-common`, `snd-sb16-dsp`, `snd-timer`, `snd-hwdep`, `snd-rawmidi`, `virtio_snd` ; `CONFIG_SND_SB16=m`, `CONFIG_SND_ISA=y`) : pas besoin de `linux-lts` (76 Mio installés contre 29 Mio pour `linux-virt`, non utilisé). Export en arborescence 9p comme E1 (`build.ps1` → `fs2json.py --zstd`, `copy-to-sha256.py --zstd`) : 4 828 fichiers, **196,1 Mio en zstd**, `alpine-fs.json` 416 Kio ; le tar de 622 Mio est supprimé après conversion (C: n'avait que 7,5 Go libres au début, 4,0 Go à la fin).
- `guest/snd-init.sh` : `modprobe snd-sb16 port=0x220 irq=5 dma8=1 dma16=5 mpu_port=0x330 isapnp=0`, `amixer sset Master/PCM 100% unmute`, `MARK snd-ready` ; lancé au boot par `/etc/local.d/01-snd.start`. Le noyau voit « `SB16 - Sound Blaster 16 at 0x220, irq 5, dma 1&5` ».
- `guest/sound.sh` : `aplay` (`MARK aplay-start` juste avant `aplay -q -D plughw:0,0 /root/tone.wav`, `MARK aplay-done` après, ligne `APLAY rc=…` avec l'erreur éventuelle) ; `pipewire` (PipeWire + WirePlumber, deux `pw-play` nommés `fluxA` et `fluxB`, `wpctl set-mute` sur B seulement pendant ~4 s, marques `pw-ready`, `pw-two-streams`, `pw-muted-B`, `pw-unmuted-B`, `pw-done`) ; `cpu <s>` (boucle shell qui occupe le CPU invité).
- `gen-wav.py` (stdlib Python, exécuté par `build.ps1`) : `tone.wav`, 10 s, 44,1 kHz, stéréo, 16 bits, sinus 440 Hz ; **canal gauche en escalier de 8 niveaux** d'amplitude (pas ~2,2 dB, un niveau par tranche de 100 ms, cycle 0..7) pour que la page reconstruise l'ordre des tranches et détecte une **rejoue** (niveau qui recule) ou un **saut** ; canal droit à amplitude constante 0,5 (continuité, premier échantillon non nul).
- `boot.html` : deux modes. `?mode=main` : v86 dans le fil principal avec son haut-parleur natif (`SpeakerAdapter` → `SpeakerWorkletDAC`, le `DACProcessor` de v86 dans un AudioWorklet) ; la page branche un worklet « compteur » (`audio-worklet.js` en mode `meter`) sur `speaker_adapter.dac.node_output` (avant le mélangeur v86) et enveloppe `dac.queue` / `dac.pump` pour compter ce que la SB16 envoie. `?mode=worker` : v86 dans `vm-worker.js` (`importScripts("lib/libv86.js")`, sans écran, `disable_speaker/keyboard/mouse`) ; le worker enregistre sur le bus v86 `dac-send-data`, `dac-enable`, `dac-disable`, `dac-tell-sampling-rate` et les poste (buffers transférés) **directement à l'AudioWorklet** par un `MessagePort` (canal créé par la page, un bout transféré au worklet, l'autre au worker : le fil principal n'est plus sur le chemin des données) ; le worklet (`audio-worklet.js` en mode `dac`) rééchantillonne (interpolation linéaire, pas la sinc de v86), rend, et redemande des données (« pump ») au worker quand il a moins de 1 024 trames d'avance (seuil du `DACProcessor` de v86), en joignant le nombre de blocs déjà reçus ; `?guard=1` fait ignorer au worker toute demande émise avant qu'il n'ait envoyé son dernier bloc (le principe de la PR #1661) ; `?lead=<ms>` relève le seuil pour garder une avance. Bouton « Démarrer l'audio (geste) » (`AudioContext.resume()` dans un clic Playwright) ; `?state=` restaure un état ; tout est exposé dans `window.E5`.
- `audio-worklet.js` : dans les deux modes, RMS par bloc de 256 trames (L et R, horodaté en temps audio), premier bloc non silencieux, trames rendues vides alors que le DAC est actif (sous-alimentation), compteurs de blocs et de « pump ». La page relie l'horloge audio au temps absolu par `AudioContext.getOutputTimestamp()`.
- `server.mjs` : copie de celui de E1 (COOP/COEP, Range, compteur d'octets, dépôt d'état), port **8733** (8086 est E1, 8094–8097 étaient pris par E4 pendant l'essai).
- `run.mjs` : Playwright + Chrome installé, headless. Phase 0 : boot à froid en mode main jusqu'au prompt et `MARK snd-ready`, `save_state()` (256 Mio de RAM) → `images/state-snd.bin`. Puis, par run et par scénario : page depuis l'état, invité vivant (`MARK alive`), clic « Démarrer l'audio », 6 s de repos (CPU de base), lecture avec ou sans charge, attente de `aplay-done` / `pw-done`, rapport du worklet, CPU par processus Chrome avant/après (CDP `SystemInfo.getProcessInfo`, temps CPU cumulé : le renderer compte **tous ses fils**, v86, worklet, JIT wasm), analyse (trous, rejoues, latence). Pour PipeWire, `run.mjs` écrit d'abord dans l'invité `/etc/pipewire/pipewire.conf.d/sb16.conf` (puits ALSA statique `plughw:0,0`, car sans udev WirePlumber ne crée pas le puits ; horloge 44,1 kHz, quantum 2 048) par la console série.

Configuration v86 : 0.5.473 (npm, commit 315b2b0 du 2026-10-30), `memory_size` 256 Mio, racine 9p comme E1, SB16 par défaut (port 0x220, IRQ 5, DMA 1 et 5, MPU-401 0x330).

**Vérification #1660 / #1661.** L'issue copy/v86#1660 (3 nov. 2026, « SB16 replays already-played DMA blocks after the main thread stalls ») : quand le fil principal se fige quelques centaines de ms, le `DACProcessor` continue de poster des `pump` (un par échange de tampon à vide) ; à la reprise, les demandes s'exécutent à la file, chacune avance d'un bloc DMA, et la carte émulée lit des moitiés d'anneau que l'invité n'a pas encore remplies (rejoues). La PR #1661 (« speaker: drop stale DAC requests from the worklet », ouverte, **non fusionnée** au 2026-11-10) fait joindre au `pump` le nombre de tampons reçus et ignore les demandes antérieures au dernier envoi. Dans le `libv86.js` 0.5.473 utilisé, le worklet poste `{type:"pump"}` **sans compteur** et le fil principal exécute `dac-request-data` à chaque réception : **le correctif n'est pas présent** ; le pont worker de cet essai le réimplémente (`guard=1`) pour le mesurer.

## Protocole

Scénarios (`run.mjs`), chacun depuis l'état sauvegardé (invité au prompt, `snd-sb16` chargé), 2 runs :

| Scénario | v86 | Charge pendant la lecture | Lecture |
|---|---|---|---|
| `main-idle` | fil principal (haut-parleur natif v86) | aucune | `aplay` 10 s |
| `main-mainload` | fil principal | fil principal bloqué 300 ms toutes les 500 ms (boucle occupée, 14 s) — « Kazo sous charge » | `aplay` |
| `main-gesture` | fil principal | aucune ; **pas de clic** avant `aplay`, clic 3 s après | `aplay` |
| `worker-idle` | Web Worker + pont direct | aucune | `aplay` |
| `worker-mainload` | worker | fil principal bloqué 300/500 ms | `aplay` |
| `worker-guestload` | worker | boucle CPU dans l'invité (`sound.sh cpu 14 &`) | `aplay` |
| `worker-stall` | worker, sans garde | **fil du worker** bloqué 300/500 ms (la VM gèle) | `aplay` |
| `worker-stall-guard` | worker, garde #1661 | idem | `aplay` |
| `worker-stall-guard-lead` | worker, garde + avance de 250 ms | idem | `aplay` |
| `worker-gesture` | worker | aucune ; pas de clic avant, clic 3 s après | `aplay` |
| `worker-pipewire`, `main-pipewire` | worker / fil principal | aucune | PipeWire, deux flux, muet de B |

Mesures par scénario : état de l'`AudioContext` avant et après le clic ; latence `MARK aplay-start` (horodatée à réception de la ligne série : dans le worker, en temps absolu au moment de l'octet) → `dac-enable` (snd-sb16 lance la DMA) → premier bloc reçu par le worklet → premier bloc rendu non silencieux ; durée audible ; trous (blocs silencieux entre premier et dernier son) ; sous-alimentation du DAC dans la fenêtre sonore ; rejoues et sauts d'après l'escalier de niveaux ; trames reçues de la SB16 ramenées à son taux (10,03 s attendues pour 441 000 trames + remplissage du dernier bloc) ; code de retour d'`aplay` ; CPU du renderer et du service audio sur 6 s de repos puis sur la lecture.

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (8 cœurs), 64 Go. Pendant l'essai, une autre maquette (E4) tournait sur la même machine : les chiffres CPU sont ceux des processus de ce Chrome seulement, mais la machine n'était pas au repos.
- Chrome 156.0.8078.80 installé, piloté par Playwright 1.65 en **headless** (`channel: "chrome"`) ; `AudioContext` à 48 kHz, `baseLatency` 10 ms, `outputLatency` 40 ms, service audio Chrome (`audio.mojom.AudioService`) actif. Pas de carte son visible du test : rien n'a été écouté, tout est compté dans le worklet.
- Serveur sur `localhost` ; l'état (78 Mio bruts pour 256 Mio de RAM) est rechargé à chaque scénario en ~0,5 s.
- v86 0.5.473 ; image 196,1 Mio zstd ; Python 3.14, Node 24.

## Résultats

Deux runs complets (`results-1.json`, `results-2.json`), un run headed des deux scénarios « geste » (`results-11.json`), un run de mise au point (`results-9.json`, PipeWire avec sa configuration par défaut) et le boot (`results-boot.json`). Temps en secondes ou ms ; « CPU » = temps CPU du processus renderer de Chrome rapporté au temps réel (100 % = un cœur), moyenne sur 6 s de repos puis sur toute la lecture ; le service audio de Chrome est à 0,5 % partout.

### Boot, état, téléchargement

| Mesure | Valeur |
|---|---|
| Boot à froid (mode main, 256 Mio), page → `MARK boot-ready` / prompt | 60,1 s / 65,2 s (E1 : 60,8 / 62,4 s avec 1 Gio) |
| `snd-sb16` chargé au boot (`local.d`) | oui : « `Sound Blaster 16 at 0x220, irq 5, dma 1&5` », `MARK snd-ready` |
| Octets servis jusqu'au prompt | 21,7 Mo en 206 requêtes (18,7 Mo de `.zst`) |
| `save_state()` (256 Mio de RAM) | 78,3 Mo en 88 ms |
| Rechargement depuis l'état, page → `emulator-started` → invité répond | 0,5 s → 0,7 s (main et worker) |
| Par scénario : état + fichiers touchés par `aplay` | ≈ 78,3 Mo + 2,1 Mo de `.zst` (main) ; worker : 78,3 Mo + 4 Ko (fichiers déjà en cache) |

### `aplay` 10 s : continuité, latence, CPU

Latence = `MARK aplay-start` → premier bloc rendu non silencieux ; entre parenthèses pour le worker : part invité (marque → `dac-enable`) + part « pont » (`dac-enable` → premier bloc au worklet ; comprend le remplissage des premières périodes ALSA par `aplay`, le trajet worker → worklet lui-même n'est pas isolé). Reçu = trames envoyées par la SB16 ramenées à 44,1 kHz (10,03 s attendues).

| Scénario | Run | Latence (ms) | Audible (s) | Trous | Rejoues / anomalies d'ordre | Reçu (s) | `aplay` | CPU repos → lecture |
|---|---|---|---|---|---|---|---|---|
| `main-idle` | 1 | 341 | 10,01 | 0 | 0 / 0 | 10,03 | rc 0 | 18 % → 97 % |
| | 2 | 405 | 10,01 | 0 | 0 / 0 | 10,03 | rc 0 | 19 % → 104 % |
| `main-mainload` (page bloquée 300/500 ms) | 1 | 951 | 13,87 | 6 (max 277 ms) | **6 anomalies**, 2,2 s de trames en trop | **12,26** | rc 0 | 20 % → 107 % |
| | 2 | 997 | 14,53 | 7 (max 283 ms) | **7 anomalies**, 2,6 s en trop | **12,63** | rc 0 | 21 % → 100 % |
| | (run 9) | 637 | 0,93 | 1 (277 ms) | 1 | 0,65 | **rc 1, `pcm_write: I/O error`** à 1,5 s | 20 % → 199 % |
| `worker-idle` | 1 | 407 (350 + 59) | 10,01 | 0 | 0 / 0 | 10,03 | rc 0 | 12 % → 96 % |
| | 2 | 342 (284 + 61) | 10,01 | 0 | 0 / 0 | 10,04 | rc 0 | 15 % → 90 % |
| `worker-mainload` (page bloquée 300/500 ms) | 1 | 324 (268 + 56) | 10,00 | **0** | **0 / 0** | 10,03 | rc 0 | 13 % → 148 % |
| | 2 | 340 (285 + 57) | 10,01 | **0** | **0 / 0** | 10,03 | rc 0 | 13 % → 145 % |
| `worker-guestload` (boucle CPU dans l'invité) | 1 | 631 (568 + 63) | 10,00 | 0 | 0 / 0 | 10,03 | rc 0 | 12 % → 200 % |
| | 2 | 585 (525 + 62) | 10,01 | 0 | 0 / 0 | 10,03 | rc 0 | 11 % → 201 % |
| `worker-stall` (worker gelé 300/500 ms, sans garde) | 1 | 697 | 1,51 | 1 (256 ms) | 3 sauts | 1,24 | **rc 1, `I/O error`** à 2,0 s | 13 % → 184 % |
| | 2 | 672 | 1,63 | 1 (272 ms) | 3 sauts | 1,35 | **rc 1, `I/O error`** à 2,1 s | 12 % → 184 % |
| `worker-stall-guard` (idem, garde #1661) | 1 | 644 | 14,22 | 15 (max 288 ms, 3,9 s en tout) | **0 / 0** | 10,03 | rc 0 | 11 % → 95 % |
| | 2 | 638 | 14,20 | 15 (max 283 ms, 3,9 s) | **0 / 0** | 10,03 | rc 0 | 15 % → 96 % |
| `worker-stall-guard-lead` (garde + avance 250 ms) | 1 | 663 | 13,87 | 15 (max 277 ms, 3,9 s) | 0 / 0 | 10,03 | rc 0 | 13 % → 98 % |
| | 2 | 635 | 13,91 | 15 (max 288 ms, 3,6 s) | 0 / 0 | 10,03 | rc 0 | 13 % → 98 % |

Lecture :

- **Au repos, les deux voies donnent 10,01 s continues, sans trou ni rejoue, à 340–410 ms de latence** depuis la marque série (dont ~280–350 ms dans l'invité : `aplay` ouvre ALSA, `snd-sb16` programme la DMA ; ~55–65 ms ensuite jusqu'au premier bloc ; 0–3 ms du premier bloc au premier son rendu). Invité chargé : 585–630 ms.
- **Page bloquée 300 ms sur 500 (« Kazo sous charge ») : le pont worker → worklet n'en souffre pas du tout** (0 trou, 0 anomalie, 10,03 s reçues, deux runs), puisque ni les données ni les demandes ne passent par le fil principal. **La voie native v86 (fil principal) casse** : 6–7 trous de ~270 ms (la VM elle-même gèle), **2,2–2,6 s de trames en trop et des sauts d'ordre dans l'escalier** (la carte relit l'anneau : c'est #1660), et au run de mise au point `aplay` a abandonné sur `I/O error` après 1,5 s.
- **Worker gelé 300 ms sur 500 (la VM gèle, les demandes s'accumulent) : sans garde, `aplay` meurt en 2 s** (`pcm_write: I/O error`, 3 runs sur 3 : à la reprise, ~100 demandes en attente avancent la DMA de plusieurs périodes, `snd-sb16` voit son pointeur matériel dépasser l'application) ; **avec la garde équivalente à #1661, la lecture va au bout, 10,03 s reçues, 0 rejoue, 0 saut** ; il reste les trous de ~280 ms, qui sont le gel lui-même (la VM ne produit rien pendant qu'elle est figée).
- **L'avance de 250 ms n'a rien changé** : mêmes 3 294 demandes servies pour 864 blocs rendus, mêmes trous. La SB16 émulée ne fournit des données qu'au rythme où le pilote invité programme ses périodes (une demande servie sans période DMA en cours ne renvoie rien) ; constituer une avance supposerait que la carte lise plus vite que le temps réel ce que l'invité a déjà écrit dans son tampon ALSA, ce que `snd-sb16` interprète comme un dépassement. Non résolu dans le temps de l'essai.
- **CPU** : VM au repos 11–15 % (worker) ou 18–21 % (main : le canvas VGA ajoute ~2 % de GPU et le fil principal rafraîchit l'écran) ; **`aplay` fait monter le renderer à 90–105 %** d'un cœur — c'est l'invité qui sort de son état d'arrêt (86 IRQ/s, `aplay`, le noyau), pas le worklet (le service audio reste à 0,5 %). Page bloquée : 145–148 % (v86 + la boucle occupée). Invité chargé : 200 % (v86 à 100 % sur son fil plus, hypothèse non vérifiée, les fils de compilation wasm du JIT de v86 sollicités par la boucle shell). Le renderer compte tous ses fils ; la part propre au son n'est donc pas isolée.

### Geste, politique d'autoplay

| Scénario | Mode | Contexte avant tout clic | `resume()` sans geste | Clic → état | Son avant le clic ? |
|---|---|---|---|---|---|
| `main-gesture` ×2, headless | main (contexte créé par v86) | **running** (v86 appelle `resume()` à `emulator-started` ; Chrome affiche l'avertissement « not allowed to start » à la création, puis la reprise passe) | — | running → running | oui : 566–568 blocs rendus avant le clic |
| `main-gesture`, **headed** | main | running | résolue, running | running → running | oui |
| `worker-gesture` ×2, headless | worker (contexte créé par la page, jamais repris avant le clic) | suspended | — | suspended → running | non : 0 bloc avant le clic ; la lecture démarre au clic, 10,03 s reçues, `aplay` rc 0 (le modèle « tiré » met la carte en attente : 0,3–0,4 s d'attente ici, sans xrun invité) |
| `worker-gesture`, **headed** | worker | suspended | **résolue, running** | running → running | — |

Autrement dit : avec Chrome 156 piloté par Playwright, headless **ou headed**, `AudioContext.resume()` aboutit sans aucun geste ; la voie « geste → `resume()` → `running` » fonctionne (clic Playwright = activation), mais **le blocage réel d'une session d'usager n'a pas pu être observé** avec ce banc (Playwright ou son profil temporaire semble satisfaire la politique ; à vérifier à la main dans une fenêtre Chrome normale, et dans Firefox/Safari dont la politique diffère).

### PipeWire dans l'invité : deux flux, muet par flux

Configuration injectée par `run.mjs` : puits ALSA statique sur `plughw:0,0`, horloge 44,1 kHz, quantum 2 048 (46 ms). `pipewire` + `wireplumber` prêts (`wpctl status` montre « `30. SB16 v86` ») **12,4–14,7 s** après leur lancement ; premier son **2,7–3,0 s** après le lancement des deux `pw-play`. RMS = canal droit, moyenne des blocs sonores de la fenêtre (A seul à amplitude 0,5 donne 0,35).

| Run | Trous pendant la lecture | RMS deux flux | RMS B muet | RMS B rétabli | 1 s avant / après le muet | CPU lecture |
|---|---|---|---|---|---|---|
| worker 1 | 0 | 0,12 | 0,35 | 0,09 | 0,07 / 0,35 | 205 % |
| worker 2 | 0 | 0,35 | 0,35 | 0,35 | 0,35 / 0,35 | 219 % |
| main 1 | 2 (5 et 11 ms) | 0,12 | 0,35 | 0,10 | 0,07 / 0,35 | 218 % |
| main 2 | 0 | 0,12 | 0,35 | 0,09 | 0,07 / 0,35 | 217 % |
| run 9, worker, **config PipeWire par défaut** (48 kHz, quantum 1 024) | **12** (42–85 ms, 571 ms en tout, multiples du quantum) | — | 0,35 | — | — | 218 % |

Lecture : le muet par flux marche (`wpctl set-mute <id>`) : « B muet » donne exactement A seul (0,35) dans 4 runs sur 4, et le son continue. Mais le niveau « deux flux » n'est pas celui attendu : B devait être un 220 Hz (`pw-play --raw --rate=22050`, puissances qui s'ajoutent, RMS 0,5) ; on mesure 0,07–0,12, soit une **annulation quasi complète**, cohérente avec deux 440 Hz presque en opposition de phase (B lu à 44,1 kHz malgré `--rate`, les deux flux alignés par PipeWire sur ses quanta) ; et au run 2 worker B n'a rien changé (0,35 partout : B inaudible ou en phase nulle). Non élucidé : la preuve du muet est la différence nette avant/après la marque dans 3 runs sur 4, pas une mesure de niveau. L'escalier de niveaux n'a pas de sens sur un mélange (colonnes rejoues/sauts ignorées ici). Coût : PipeWire dans l'invité double le CPU par rapport à `aplay` (205–219 % contre 90–105 %), et avec sa configuration par défaut il saute des quanta (12 trous de 42–85 ms sur 10 s) ; à 44,1 kHz / 2 048 il n'en saute plus (0 trou dans 3 runs sur 4, 2 trous ≤ 11 ms dans le 4e).

## Ce qui a raté ou a été contourné

- **Ports** : 8095 puis 8097 étaient pris par la maquette E4 qui tournait en parallèle ; E5 a fini sur 8733. La première couche `apk add` de E1 n'a pas été reprise du cache Docker (recompilée, ~20 s, paquets retéléchargés).
- **`speaker_adapter` est créé après le chargement du wasm** : la page doit attendre `emulator-ready` avant de brancher sa prise sur `dac.node_output` (le premier essai échouait silencieusement).
- **Demande de données en course** : `startAudio()` est asynchrone ; `run.mjs` lisait `E5.gesture` trop tôt (corrigé par un `waitForFunction`).
- **« Sous-alimentation » hors lecture** : le DAC de la SB16 reste « activé » après la restauration de l'état et avant `aplay`, donc le compteur de trames vides comptait 3–6 s de silence légitime ; seules les sous-alimentations entre le premier et le dernier son sont retenues.
- **WirePlumber ne crée pas le puits ALSA** sans udev (l'invité n'a que mdev) : puits statique `api.alsa.pcm.sink` dans `pipewire.conf.d/sb16.conf`, écrit dans l'invité par la console série avant chaque scénario PipeWire (pour ne pas reconstruire l'image). Les sorties de `wpctl status` arrivent en UTF-8 mal décodé sur la console série (sans conséquence).
- **Flux B de PipeWire** : voulu à 220 Hz, mesuré comme un 440 Hz en opposition de phase (voir ci-dessus) ; un deuxième fichier à une autre fréquence aurait levé l'ambiguïté.
- **Avance (« lead ») sans effet** : voir Résultats ; la mécanique SB16 ↔ pilote n'a pas été creusée.
- **Sans garde, mon pont casse plus vite que v86 natif** (I/O error en 2 s contre rejoues) : mon worklet redemande à chaque quantum quand il est à sec (≈110 demandes par gel de 300 ms), le `DACProcessor` de v86 ne redemande qu'à chaque échange de tampon ; même classe de bogue, rafale plus grosse.
- **Pas de rejoue détectable au sens strict** (niveau qui recule d'un cran) : les anomalies de la voie native sous charge sont des sauts de plusieurs tranches dans les deux sens (la DMA court sur plusieurs périodes), que l'escalier à 8 niveaux ne sait pas orienter sans ambiguïté ; la preuve de la relecture est le surplus de trames (12,3–12,6 s reçues pour 10,03 s de fichier).

## Limites

- Une seule machine, un seul navigateur (Chrome 156), headless sauf le run 11 ; `localhost` ; deux runs par scénario (trois pour ceux rejoués en mise au point). Une autre maquette (E4) tournait en parallèle sur la même machine.
- **Rien n'a été écouté** : pas de carte son vue par le test, tout est mesuré dans le worklet (RMS, compteurs). Les trous < 5 ms (un bloc de 256 trames) ne sont pas vus ; la qualité du rééchantillonnage (sinc de v86 contre interpolation linéaire du pont) n'est pas évaluée.
- **Non mesurés** : Firefox, Edge, Safari (politiques d'autoplay et AudioWorklet différents) ; **le son d'un programme Wine** (`winealsa`/`winepulse`, non tenté : `wineboot` coûte 7 min par E1 et il n'y avait pas de programme Win32 sonore sous la main) ; le MIDI (MPU-401) et l'OPL3 ; un tampon partagé (`SharedArrayBuffer`) entre worker et worklet (seul le `MessagePort` a été fait) ; le pont écran du worker (`OffscreenCanvas`, E4) en même temps que le son ; un vrai blocage d'autoplay (voir Geste) ; une charge de Kazo autre qu'une boucle occupée.
- **Latence** : la part « pont » (55–65 ms) inclut le remplissage des premières périodes ALSA ; la latence de sortie du navigateur (`outputLatency` 40 ms) n'est pas dans les chiffres ; le temps de rendu réel chez l'usager (carte son, mixeur Windows) non plus.
- **CPU** : par processus, donc tous les fils du renderer (v86, worklet, JIT) ; le coût propre du son n'est pas séparé du coût de l'invité qui s'active.
- La configuration PipeWire a été réglée une fois (44,1 kHz, quantum 2 048) sans balayage ; `pw-play` n'est ni un programme Wine ni une application PipeWire typique.

## Chiffres bruts (fichiers)

Dans `essais/e5-son/` (ignorés par git) : `results-boot.json` (boot, `snd-ready`, état), `results-1.json` et `results-2.json` (les 12 scénarios, avec marques, rapport du worklet, analyse, CPU par type de processus, téléchargements), `results-11.json` (run headed de `main-gesture` et `worker-gesture` avec `resume_without_gesture`), `results-9.json` (mise au point : PipeWire par défaut, premier `main-mainload` en I/O error), `images/campaign.log`, `images/headed.log`, `images/smoke1.log`, `images/smoke2.log`, `images/docker-build.log`, `images/build-all.log`, `images/state-snd.bin` (78,3 Mo).

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **Le son dans v86 marche sans noyau spécial** : `snd-sb16` est dans `linux-virt` d'Alpine, `aplay` joue 10 s continues à 44,1 kHz stéréo 16 bits sur la SB16 émulée, +27 Mio dans l'image pour ALSA et PipeWire. Pas d'argument « son en V2 » venant du noyau ou de l'image. À valider.
2. **Le pont doit être worker → AudioWorklet direct (MessagePort), jamais par le fil principal** : avec v86 dans le worker et le `MessagePort` transféré au worklet, une page bloquée 60 % du temps ne crée ni trou ni rejoue ; la voie native de v86 (fil principal) sous la même charge relit l'anneau (2,2–2,6 s de trames en trop) ou tue `aplay`. C'est le critère « son continu sous charge » pour la part Kazo. À valider (et à refaire avec le pont écran de E4 en même temps).
3. **La garde « ignorer les demandes périmées » (PR #1661) est indispensable dans le pont** et n'est pas dans v86 0.5.473 (PR ouverte) : sans elle, un gel du worker de 300 ms tue la lecture (`I/O error`) ; avec elle, la lecture va au bout sans rejoue. Proposition : l'implémenter dans notre pont quel que soit le sort de la PR, et faire remonter nos mesures sur l'issue. À valider.
4. **Un gel de la VM reste un trou audible** (≈ 280 ms pour 300 ms de gel) : la carte émulée ne lit que ce que le pilote invité programme, et l'avance de 250 ms essayée n'a rien donné. Les gels côté VM (JIT, GC, sauvegarde d'état de E15/D56 : 88 ms pour 256 Mio ici) seront entendus ; à chiffrer avec la vraie fréquence des points de restauration, et à creuser (avance côté SB16 émulée, ou tampon ALSA plus grand dans l'invité). À valider.
5. **Latence** : 340–410 ms entre le lancement d'`aplay` et le premier son, dont ~300 ms dans l'invité ; 585–630 ms si l'invité est chargé. Suffisant pour un lecteur ou une notification, pas pour un instrument ; la latence « application → son » d'un programme Wine n'est pas mesurée (point 9). À valider.
6. **Budget CPU** : le son ne coûte rien côté page (service audio 0,5 %), mais un invité qui joue sort de l'arrêt : 90–105 % d'un cœur pour `aplay` seul, 205–219 % avec PipeWire. Proposition : pas de PipeWire par défaut dans l'invité V1 ; ALSA direct (ou `dmix`) et le mélange par programme côté Kazo si besoin. À valider.
7. **Muet par programme** : PipeWire tourne sur la SB16 (puits statique, WirePlumber sans udev), deux flux se mélangent, `wpctl set-mute` coupe un flux sans arrêter l'autre ; coût : 13–15 s de démarrage, 2,7–3,0 s avant le premier son, le double de CPU, et des sauts de quantum avec sa config par défaut. Si le muet par programme (133) est voulu en V1, PipeWire le donne mais cher ; sinon un muet global côté page (gain du worklet) est gratuit. À valider.
8. **Geste et autoplay** : la voie « bouton → `resume()` » marche ; le modèle tiré met la carte en attente tant que le contexte est suspendu (0,3–0,4 s ici sans xrun invité, mais une attente longue ferait des xruns dans l'invité : prévoir de couper la DMA ou d'avertir). Le blocage réel n'a pas été observé sous Playwright (headless et headed) : à vérifier à la main dans Chrome, Firefox et Safari avant de fixer l'UX (133). À valider.
9. **Wine** : non mesuré. Avant de conclure sur 133, il faut un programme Win32 sonore (`PlaySound`) dans l'état gabarit de E1 et la mesure `winealsa` → SB16 → pont ; à prévoir avec la reprise de E1 (question 150). À valider.
