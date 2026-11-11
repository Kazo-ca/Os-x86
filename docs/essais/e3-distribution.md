# Essai E3 — distribution des images

Date : 2026-11-11. Code jetable dans `essais/e3-distribution/` (question 101, ligne E3 des études prévues, section 4.2 du plan du 2026-11-11). Critère de l'essai : 0,8 Go téléchargé et vérifié, reprise après coupure ; moins de 10 min sur 100 Mbit/s ; vérification en moins de 30 s. Conséquence prévue d'un échec : hébergement sur serveur Kazo obligatoire.

**Résultat en une ligne : critère atteint.** 800 Mio téléchargés par morceaux de 32 Mio, vérifiés morceau par morceau (SHA-256) et écrits dans l'OPFS en 68,5 s à 100 Mbit/s (4 s sur localhost), vérification complète sur place en 1 s, reprise après trois coupures de connexion et deux 503 sans rien perdre d'autre que le morceau en cours, dans Chrome, Edge et Firefox. Le SHA-256 de .NET 10 interprété fait 35 Mo/s contre 1 100 à 1 250 Mo/s pour `SubtleCrypto` : la vérification doit se faire en JS. Le cache HTTP des trois navigateurs ne garde aucun fichier de 64 Mio ou plus (ce qui explique E1). GitHub Releases est inutilisable depuis le navigateur (pas de CORS) ; GitHub Pages et `raw.githubusercontent.com` envoient CORS et répondent 206 mais sont bornés en taille ; R2 n'a pas pu être mesuré (documentation seulement).

## But

Décider où héberger les images (0,2 à 0,8 Go) pour qu'un navigateur puisse les télécharger par morceaux avec reprise, comment les vérifier (où faire le SHA-256 : C# sous Kazo ou JS), quelle taille de morceau, et si le cache HTTP peut servir à quelque chose vu qu'E1 a vu v86 redemander ses `.zst` à chaque démarrage. Avec et sans isolation d'origine croisée (COOP/COEP), parce que Kazo peut l'activer.

## Ce qui a été construit

- `gen.mjs` : génère `images/data-<N>.bin` (motif xorshift32, déterministe) et un manifeste JSON avec le SHA-256 du fichier entier et le SHA-256 de chaque morceau pour 8, 32, 128 et 512 Mio. Fichiers de 800 Mio (banc principal), 200 Mio (cache et fumée), 8/32/64/128 Mio (seuil du cache).
- `server.mjs` : serveur Node sur 127.0.0.1:8098 qui sert `/images/` avec `Access-Control-Allow-Origin: *`, `Access-Control-Expose-Headers`, `Accept-Ranges`, 206 sur `Range`, `ETag`, 304 sur `If-None-Match`, `Cache-Control` réglable ; `/ctl` pour simuler des pannes (fermer la connexion après N Mio sur les n réponses suivantes, répondre 503 aux n prochains GET, limiter le débit à N Mbit/s, activer ou non COOP/COEP) ; `/stats` donne le journal des requêtes (chemin, Range, statut, octets envoyés) ; sert aussi le projet Blazor publié sous `/dotnet/interp/`.
- `dl.html` + `dl-worker.js` : un Web Worker qui télécharge par morceaux (`fetch` avec `Range`, corps lu en flux dans un tampon préalloué de la taille du morceau), hache chaque morceau avec `crypto.subtle.digest`, le compare au manifeste, l'écrit à sa place dans un fichier OPFS ouvert par `createSyncAccessHandle()`, et note les morceaux vérifiés dans un petit fichier d'état OPFS (avec l'ETag et la taille du serveur) ; au démarrage, si l'état correspond, les morceaux vérifiés sont sautés. Deux comportements sur erreur : réessayer (délai 100 ms × tentative, 2 s au plus) ou abandonner (et laisser la page être rechargée). Autres modes : vérification sur place d'un fichier OPFS (relecture + SHA-256 par morceau), `fetch` d'un seul tenant avec et sans `integrity`, lectures `Range` aléatoires de 64 Kio et 4 Kio (ce que fait v86), banc SHA-256 (`SubtleCrypto` contre une implémentation en JS pur), test du cache HTTP et de la Cache API (comptage des requêtes vues par le serveur), `Range` inter-origine vers des hébergeurs publics (1 Kio par hôte), nettoyage.
- `run.mjs` : pilote Playwright. Chaque passe lance le navigateur dans un profil neuf sur disque (`launchPersistentContext`, comme E2), exécute le scénario, supprime le fichier OPFS puis le profil. Mesure la mémoire des processus du navigateur lancé (jeu de travail et pages privées via `Win32_Process`, échantillonnage toutes les 100 à 300 ms, processus de rendu isolés par `--type=renderer` ou `-contentproc`).
- `dotnet/` : projet Blazor WebAssembly autonome (.NET 10.0.13, `dotnet new blazorwasm`) dont la page hache un tampon de 64 Mio avec `SHA256.HashData` et `IncrementalHash` (blocs de 1 Mio), trois passes chacune, plus une boucle C# témoin, et publie les Mo/s dans `window.__results`. Publié en Release, interprété (jiterpreter par défaut), sans le workload `wasm-tools`.
- `probe-hosts.sh`, `probe-hosts2.sh` : sondes `curl` (HEAD, GET `Range: bytes=0-1023`, OPTIONS de prévol) sur des hébergeurs publics, User-Agent générique, en-tête `Origin` factice.
- `tableau.mjs` : médiane des passes et tableaux ci-dessous depuis `resultats/results-*.json`.

## Protocole

1. **Hébergeurs réels** (lecture seule, aucun compte, aucun envoi) : `curl` HEAD, GET partiel et OPTIONS sur un site GitHub Pages public (`microsoft.github.io/monaco-editor`), `raw.githubusercontent.com`, jsDelivr (npm et gh), un asset de GitHub Releases, `developers.cloudflare.com` et un sous-domaine `pages.dev` ; puis les mêmes `Range` depuis le navigateur (Chrome et Firefox, page servie en `http://127.0.0.1`, avec et sans COOP/COEP) pour voir ce que le JS obtient vraiment (prévol, en-têtes lisibles). Pour R2 : documentation seulement (aucun bucket public connu accessible, voir ratés).
2. **Téléchargement reprenable vers l'OPFS** : 800 Mio, morceaux de 8, 32, 128 et 512 Mio (Chrome, 3 passes chacun), 32 Mio dans Edge et Firefox (3 passes) ; après chaque téléchargement, vérification sur place (relecture OPFS + SHA-256 par morceau). Reprise : (a) le serveur ferme la connexion après 20 Mio sur les réponses 4, 5 et 6 et répond 503 aux deux premiers GET, le worker réessaie ; (b) le serveur coupe la 6e réponse après 20 Mio, le worker abandonne, la page est rechargée (nouveau worker, même profil, même OPFS), trois fois, puis une quatrième tentative sans coupure. Débit limité à 100 Mbit/s côté serveur (Chrome, 32 Mio, 3 passes). Comparaisons : `fetch` d'un seul tenant avec `integrity` (800 Mio en mémoire) puis sans ; 500 lectures `Range` aléatoires de 64 Kio et de 4 Kio, sans limite puis à 100 Mbit/s.
3. **SHA-256 .NET contre `SubtleCrypto`** : 64 Mio, 3 passes par mesure, dans les trois navigateurs ; `SubtleCrypto.digest` et SHA-256 en JS pur dans le worker ; .NET 10 interprété dans la page Blazor. AOT : voir ratés.
4. **COOP/COEP** : passe 2 refaite sans les en-têtes dans Chrome (toutes tailles) et Firefox (32 Mio), 3 passes.
5. **Cache HTTP** : pour 200 et 800 Mio avec `Cache-Control: public, max-age=3600` et ETag (et 200 Mio en `no-store`) : quatre `fetch` successifs (défaut, défaut, `force-cache`, `no-cache`), puis `caches.open` + `cache.add` + deux `cache.match`, en comptant les requêtes vues par le serveur ; témoin avec 8, 32, 64 et 128 Mio pour trouver le seuil.

Trois passes par mesure principale, médiane dans les tableaux ; 1 passe pour Edge et Firefox sur les scénarios secondaires. Mio = 1 048 576 octets ; Mo = 10⁶ octets (SHA-256 seulement).

## Machine, navigateurs, cache

| Élément | Valeur |
|---|---|
| Machine | AMD Ryzen 7 3700X (8 cœurs, 16 fils), 64 Go de RAM, Windows 11 Pro 10.0.26200 ; disque C: avec 198 à 207 Go libres pendant l'essai |
| Node / Playwright | Node 24.15.0, Playwright 1.65.0 installé dans `essais/e3-distribution/` |
| Chrome | 156.0.8078.80 installé sur la machine (`channel: 'chrome'`), sans tête |
| Edge | 157.0.4341.42 installé sur la machine (`channel: 'msedge'`), sans tête |
| Firefox | 159.0, build Playwright (`firefox-1572`), sans tête |
| Safari, mobile | non mesurés |
| .NET | SDK 10.0.402, runtime WebAssembly .NET 10.0.13, Blazor WebAssembly autonome, Release, interprété ; workload `wasm-tools` absent (AOT non mesuré) |
| Réseau | tout sur 127.0.0.1 ; le « 100 Mbit/s » est une limite du serveur Node par réponse ; l'essai d'hébergeurs touche Internet (quelques Kio) |
| Cache | profil de navigateur neuf à chaque passe ; fichier source dans le cache de fichiers de Windows (64 Go de RAM) ; serveur en `Cache-Control: public, max-age=3600` sauf dans les scénarios de cache ; le worker télécharge en `cache: 'no-store'` |
| Isolation d'origine croisée | `crossOriginIsolated === true` dans la page et `SharedArrayBuffer` présent quand COOP/COEP sont envoyés ; `false` sinon |
| Quota annoncé par `estimate()` (profil neuf) | 10,8 Gio dans Chrome et Edge (E2 avait vu 10,0 Gio sur un disque presque plein) |

## Résultats

### Hébergeurs réels (sondes `curl` du 2026-11-11 et `fetch` depuis le navigateur)

| Hébergeur (URL sondée) | `Access-Control-Allow-Origin` | `Accept-Ranges` / `Range: bytes=0-1023` | `ETag` | `Cache-Control` | OPTIONS | `fetch` + `Range` depuis Chrome 156 et Firefox 159 | Limites (documentation) |
|---|---|---|---|---|---|---|---|
| GitHub Pages (`microsoft.github.io/monaco-editor/…/editor.main.js`, 79 Kio) | `*` (mesuré) | `bytes` ; **206**, `Content-Range: bytes 0-1023/79052`, `Content-Length: 1024` (mesuré) | `"6af3a50e-134cc"` | `max-age=600` | 405 | **206, 1 024 octets reçus, sans prévol** ; `Content-Length` lisible, `Content-Range`, `ETag` et `Accept-Ranges` **non lisibles** par le JS (pas d'`Expose-Headers`) | 1 Go par site, fichier git de 100 Mio au plus (vérification des consignes) |
| `raw.githubusercontent.com` (`microsoft/monaco-editor/main/package-lock.json`, 573 Kio) | `*` + `Cross-Origin-Resource-Policy: cross-origin` (mesuré) | `bytes` ; **206** (mesuré) | SHA-256 du contenu | `max-age=300`, `Vary: Authorization` | 403 (mais `ACAO: *`) | **206, 1 024 octets, sans prévol** ; mêmes en-têtes non lisibles que Pages | fichier git de 100 Mio au plus |
| jsDelivr npm (`monaco-editor@0.52.0/…/editor.main.js`, 900 Kio) et gh | `*` + `Access-Control-Expose-Headers: *` + CORP | `bytes` ; **206 mais corps incohérent à l'offset 0** : 0 octet (monaco) ou 1 381 octets (jquery, 27 Kio) pour 1 024 demandés ; 1 024 octets corrects pour `bytes=1000-2023` (mesuré, curl et navigateur) | faible `W/"…"` | `public, max-age=31536000, immutable` (npm) ; 7 jours (gh) | 200 | **206 mais 0 octet reçu** à l'offset 0 (Chrome et Firefox) ; `Content-Range` et `ETag` lisibles | 20 Mo par fichier, interdit d'héberger des fichiers (vérification des consignes) |
| GitHub Releases (`github.com/cli/cli/releases/download/…`) | **aucun** sur la 302 de `github.com` ; l'objet final (`release-assets.githubusercontent.com`, blob Azure) a `Accept-Ranges: bytes` et répond 206 mais **sans CORS** (mesuré) | — | `"0x8DD…"` | `no-cache` | 404 | **échec** dans les deux navigateurs, avec ou sans `Range` (« No 'Access-Control-Allow-Origin' header ») | 2 Gio par asset |
| Cloudflare Pages (`developers.cloudflare.com`, `*.pages.dev`) | `*` (mesuré, y compris sur une 404 de `pages.dev`) | page HTML : `Range` **ignoré** (200, corps entier) ; asset statique non sondé | — | `public, max-age=0, must-revalidate` | 405 | non mesuré | 25 Mio par fichier (vérification des consignes) |
| Cloudflare R2 | **non mesuré** (aucun bucket public accessible ; un sous-domaine `r2.dev` pris au hasard répond 401) | non mesuré | | | | non mesuré | Documentation lue le 2026-11-11 : politique CORS par bucket (`AllowedOrigins`, `AllowedHeaders`…) ; le sous-domaine `r2.dev` est limité en débit et « non destiné à la production », il faut un domaine personnalisé ; sortie (egress) gratuite ; objet de 5 Tio au plus ; `Range` pris en charge (API S3) |

Deux faits mesurés qui comptent : (1) `Range: bytes=N-M` ne déclenche **pas de prévol** dans Chrome 156 ni Firefox 159 (GitHub Pages répond 405 à OPTIONS et la requête passe quand même) ; (2) sans `Access-Control-Expose-Headers`, le JS ne lit ni `Content-Range`, ni `ETag`, ni `Accept-Ranges` : la taille totale et la version doivent venir du manifeste ou d'un `HEAD` sur la même origine, et la reprise ne peut pas s'appuyer sur l'ETag de l'hébergeur s'il ne l'expose pas.

### Taille de morceau (Chrome, 800 Mio, localhost sans limite, COOP/COEP actifs, médiane de 3)

| Mesure | 8 Mio | 32 Mio | 128 Mio | 512 Mio |
| --- | ---: | ---: | ---: | ---: |
| Temps total du téléchargement (s) | 5,4 | 4,0 | 3,2 | 3,6 |
| Débit (Mio/s) | 148 | 201 | 283 | 287 |
| dont réseau (s) | 3,1 | 2,1 | 1,4 | 1,6 |
| dont SHA-256 des morceaux (s) | 0,7 | 0,7 | 0,7 | 0,8 |
| dont écriture OPFS (s) | 0,4 | 0,7 | 1,0 | 1,0 |
| dont fichier d'état après chaque morceau (s) | 1,0 | 0,2 | 0,1 | 0,0 |
| `flush()` final (ms) | 179 | 156 | 63 | 22 |
| Mémoire privée du processus de rendu, pic (Mio) | 356 | 216 | 339 | 1 254 |
| Mémoire privée du processus de rendu, au repos (Mio) | 44 | 45 | 46 | 44 |
| Requêtes vues par le serveur (HEAD + GET) | 101 | 26 | 8 | 3 |
| Vérification sur place : lecture OPFS + SHA-256 (s) | 1,1 | 1,1 | 1,0 | 1,1 |
| dont lecture OPFS (s) / SHA-256 (s) | 0,3 / 0,7 | 0,3 / 0,7 | 0,3 / 0,7 | 0,3 / 0,8 |
| Vérification (Mio/s) | 770 | 767 | 799 | 748 |

Le débit sur localhost (150 à 290 Mio/s) est celui du serveur Node de l'essai, pas une limite du navigateur : le réseau prend 1,4 à 3,1 s sur les 3 à 5 s, le SHA-256 0,7 s (≈ 1,2 Gio/s), l'écriture OPFS 0,4 à 1 s. Le fichier d'état (réécrit après chaque morceau, par un `createSyncAccessHandle` ouvert et fermé à chaque fois) coûte 10 ms par morceau : 1 s avec 100 morceaux de 8 Mio, négligeable au-delà. Le pic mémoire ne suit pas la taille du morceau en dessous de 128 Mio (356 Mio avec 8 Mio contre 216 avec 32 : ce sont les tampons du flux `fetch` en attente de ramasse-miettes) ; à 512 Mio il monte à 1,2 Gio.

### Reprise après coupure (morceaux de 32 Mio, 25 morceaux)

| Mesure | Chrome (3 passes) | Edge (1) | Firefox (1) |
| --- | ---: | ---: | ---: |
| (a) réponses coupées par le serveur / 503 servis | 3 / 2 | 3 / 2 | 3 / 2 |
| (a) nouvelles tentatives du worker | 5 | 5 | 5 |
| (a) octets reçus puis perdus (Mio) | 60 | 60 | 60 |
| (a) temps total avec les pannes (s) (sans panne : 4,0 / 4,3 / 6,3) | 5,4 | 5,1 | 7,4 |
| (a) morceaux vérifiés à la fin | 25 / 25 | 25 / 25 | 25 / 25 |
| (b) rechargement de page : morceaux déjà vérifiés au départ des tentatives 1 → 4 | 0 → 5 → 10 → 15 | 0 → 5 → 10 → 15 | 0 → 5 → 10 → 15 |
| (b) morceaux téléchargés par tentative | 5 / 5 / 5 / 10 | 5 / 5 / 5 / 10 | 5 / 5 / 5 / 10 |
| (b) octets perdus par tentative (Mio) | 20 / 20 / 20 / 0 | 20 / 20 / 20 / 0 | 20 / 20 / 20 / 0 |
| (b) temps par tentative (s) | 0,9 / 0,9 / 0,9 / 1,5 | 0,8 / 0,9 / 0,9 / 1,5 | 1,3 / 1,5 / 1,3 / 2,6 |
| (b) vérification finale : morceaux bons | 25 / 25 | 25 / 25 | 25 / 25 |

Une coupure coûte au plus le morceau en cours (ici 20 Mio reçus sur 32) ; un 503 ne coûte rien. Après rechargement de la page (ou du navigateur : l'état est dans l'OPFS), le worker repart au premier morceau non vérifié. Les trois navigateurs se comportent pareil ; la fermeture de connexion arrive au JS comme `TypeError: network error` sur `reader.read()`.

### Débit limité à 100 Mbit/s (Chrome, 32 Mio, 3 passes)

| Mesure | Valeur |
| --- | ---: |
| Temps total pour 800 Mio (s) | 68,5 (68,3 à 68,7) |
| Débit obtenu (Mio/s) / (Mbit/s) | 11,7 / 97,9 |
| SHA-256 pendant le téléchargement (s, recouvert par l'attente réseau) | 0,7 |
| Vérification sur place après (s) | 0,9 |
| Mémoire privée du rendu, pic (Mio) | 192 |

Critère « < 10 min sur 100 Mbit/s » : **68,5 s**, soit 0,8 Go à 98 % de la limite ; le calcul (SHA-256, écriture) est invisible derrière le réseau. Extrapolation à 350 Mio (E1, Alpine + Wine + état) : 30 s ; à 25 Mbit/s : 4,5 min pour 800 Mio.

### `fetch` d'un seul tenant (800 Mio en mémoire) et lectures `Range` à la demande

| Mesure | Chrome (3) | Edge (1) | Firefox (1) |
| --- | ---: | ---: | ---: |
| Avec `integrity` : temps total, corps compris (s) | 4,3 | 4,4 | 7,6 |
| Avec `integrity` : corps reçu et vérifié avant que `arrayBuffer()` résolve (s) | 3,3 | 3,4 | 6,7 |
| Avec `integrity` : écriture OPFS du tampon de 800 Mio (s) | 1,0 | 1,0 | 0,8 |
| Avec `integrity` : mémoire privée du rendu, pic (Mio) | **2 445** | 2 224 | 1 063 |
| Sans `integrity` : temps total (s) | 2,0 | 2,2 | 5,0 |
| Sans `integrity` : mémoire privée du rendu, pic (Mio) | 848 | 849 | 1 068 |

`integrity` double le temps et triple la mémoire dans Chrome et Edge (2,4 Gio : le corps est gardé une fois pour le hachage, une fois pour l'`ArrayBuffer`) ; aucune reprise possible, aucun progrès visible. À proscrire pour 800 Mio.

| Lectures `Range` aléatoires, 500 par série | Chrome | Firefox |
| --- | ---: | ---: |
| 64 Kio, sans limite : médiane / p99 (ms) ; débit (Mio/s) | 3,5 / 8,7 ; 17 | 4,0 / 13,1 ; 13 |
| 4 Kio, sans limite : médiane / p99 (ms) | 2,7 / 7,4 | 3,4 / 5,6 |
| 64 Kio à 100 Mbit/s : médiane / p99 (ms) ; débit (Mio/s) | 4,2 / 14,2 ; 8 | 4,9 / 19,9 ; 8 |

Une lecture `Range` coûte 3 à 5 ms même sur localhost (contre 10 à 30 µs dans l'OPFS, E2) : lire une image à la demande comme v86 le fait avec `-partial` donne 13 à 17 Mio/s au mieux, 100 à 400 fois moins qu'une lecture OPFS, et dépend du réseau à chaque bloc. C'est acceptable pour une première exploration mais pas pour une VM qui tourne.

### COOP/COEP : avec et sans (morceaux de 32 Mio, médiane de 3)

| Mesure | Chrome COI | Chrome sans COI | Edge COI | Firefox COI | Firefox sans COI |
| --- | ---: | ---: | ---: | ---: | ---: |
| `crossOriginIsolated` / `SharedArrayBuffer` | oui / oui | non / non | oui / oui | oui / oui | non / non |
| Temps total du téléchargement (s) | 4,0 | 3,5 | 4,3 | 6,3 | 5,6 |
| Débit (Mio/s) | 201 | 230 | 186 | 127 | 144 |
| dont réseau / SHA-256 / écriture OPFS (s) | 2,1 / 0,7 / 0,7 | 1,8 / 0,6 / 0,6 | 2,2 / 0,8 / 0,7 | 4,3 / 0,7 / 0,5 | 3,7 / 0,7 / 0,5 |
| Mémoire privée du rendu, pic / repos (Mio) | 216 / 45 | 168 / 45 | 186 / 20 | 299 / 83 | 320 / 84 |
| Vérification sur place (s) | 1,1 | 0,9 | 1,1 | 1,0 | 0,9 |
| `Range` inter-origine vers Pages, raw, jsDelivr | identique | identique | — | identique | identique |
| `fetch` vers GitHub Releases | échec CORS | échec CORS | — | échec CORS | échec CORS |

Sans COOP/COEP, Chrome (toutes tailles : 5,9 / 3,5 / 2,8 / 3,1 s pour 8 / 32 / 128 / 512 Mio) et Firefox sont 10 à 15 % plus rapides, ce qui est dans la variance des passes (10 à 25 %). Rien ne change fonctionnellement : OPFS, `SubtleCrypto`, `Range`, Cache API, CORS vers les hébergeurs se comportent pareil. Seule différence réelle : `performance.measureUserAgentSpecificMemory()` et `SharedArrayBuffer` n'existent qu'avec l'isolation. Les `fetch` en mode `cors` vers des hébergeurs qui envoient `ACAO: *` passent sous `COEP: require-corp` (Pages et jsDelivr n'envoient pas `Cross-Origin-Resource-Policy`, raw si ; CORS suffit).

### Cache HTTP et Cache API

Requêtes vues par le serveur à chaque étape (statut entre parenthèses), 1 à 3 passes :

| Étape | 200 Mio, `max-age=3600` (Chrome / Edge / Firefox) | 800 Mio, `max-age=3600` | 200 Mio, `no-store` |
| --- | --- | --- | --- |
| `fetch` 1 (défaut) | 1 (200) / 1 (200) / 1 (200) | 1 (200) / 1 (200) / 1 (200) | 1 (200) ×3 |
| `fetch` 2 (défaut, juste après) | **1 (200)** / **1 (200)** / **1 (200)** | **1 (200)** ×3 | 1 (200) ×3 |
| `fetch` 3 (`cache: 'force-cache'`) | 1 (200) ×3 | 1 (200) ×3 | 1 (200) ×3 |
| `fetch` 4 (`cache: 'no-cache'`, revalidation) | 1 (200) ×3 (pas de 304 : rien en cache) | 1 (200) ×3 | 1 (200) ×3 |
| Cache API : `cache.add(url)` | 1 (200) ; 0,9 / 1,0 / 1,0 s | 1 (200) ; 3,9 / 3,7 / 4,4 s | 1 (200) |
| Cache API : `cache.match(url)` + `arrayBuffer()` | **0** ; 0,3 / 0,3 / 0,6 s | **0** ; 1,2 / 1,2 / 2,9 s | 0 |
| Cache API : second `cache.match` | 0 | 0 | 0 |
| Usage `estimate()` avec l'entrée en Cache API (Mio) | 200 | 800 | 200 |

Témoin pour trouver le seuil (même `Cache-Control`, `fetch` 1 / 2 / 3 / 4) :

| Taille | Chrome | Edge | Firefox |
| --- | --- | --- | --- |
| 8 Mio | 1 (200) / **0** / **0** / 1 (**304**) | idem | idem |
| 32 Mio | 1 (200) / **0** / **0** / 1 (**304**) | idem | idem |
| 64 Mio | 1 (200) / 1 (200) / 1 (200) / 1 (200) | idem | idem |
| 128 Mio | 1 (200) / 1 (200) / 1 (200) / 1 (200) | idem | idem |

Dans un profil neuf, les trois navigateurs gardent en cache HTTP les réponses de 32 Mio (second `fetch` servi sans réseau, `no-cache` donne un 304) et **ne gardent jamais** celles de 64 Mio et plus, quel que soit `Cache-Control`, `ETag` ou `cache: 'force-cache'` : chaque `fetch` refait le téléchargement complet, et il n'y a même pas de revalidation. C'est exactement ce qu'E1 a vu avec les `.zst` de 187 Mio de v86. La Cache API, elle, garde 800 Mio sans problème et les ressert sans réseau en 1,2 s (Chrome, Edge) ou 2,9 s (Firefox), mais compte 800 Mio dans le quota (`estimate()` reste à 800 Mio juste après `caches.delete`, la suppression est asynchrone).

### SHA-256 : .NET WebAssembly contre `SubtleCrypto` et JS pur (64 Mio, meilleure de 3 passes, médiane de 3 chargements)

| Mesure | Chrome | Edge | Firefox |
| --- | ---: | ---: | ---: |
| `SubtleCrypto.digest` dans le worker (Mo/s) | **1 253** | 1 101 | 1 107 |
| SHA-256 en JS pur (Mo/s) | 112 | 97 | 50 |
| .NET 10 interprété (jiterpreter) : `SHA256.HashData` (Mo/s) | **35** | 35 | 35 |
| .NET 10 interprété : `IncrementalHash` par blocs de 1 Mio (Mo/s) | 35 | 35 | 35 |
| .NET 10 interprété : boucle C# témoin (somme d'octets, Mo/s) | 184 | 173 | 144 |
| .NET 10 interprété : chargement de la page Blazor + remplissage + 6 hachages (s) | 13,5 | 13,2 | 14,2 |
| .NET 10 AOT | non mesuré | non mesuré | non mesuré |
| Temps pour 800 Mio, extrapolé (s) : `SubtleCrypto` / .NET interprété | 0,7 / **24** | 0,8 / 24 | 0,8 / 24 |

`SubtleCrypto` est 32 à 36 fois plus rapide que le SHA-256 managé de .NET 10 interprété (sur le navigateur, `System.Security.Cryptography` n'appelle pas WebCrypto : c'est du C# interprété), et 10 à 25 fois plus rapide qu'un SHA-256 en JS. Les 800 Mio prendraient 24 s en C# interprété, sur le fil principal de Blazor, contre 0,7 s en `SubtleCrypto` dans un worker. La boucle témoin à 184 Mo/s montre que le jiterpreter est actif ; sans lui ce serait pire.

## Ce qui a raté ou a été contourné

- **AOT .NET non mesuré** : `dotnet workload install wasm-tools` (nécessaire pour l'AOT et pour le relink natif) demande une élévation ; l'installation a été annulée. `dotnet publish` avertit « Publishing without optimizations ». La mesure interprétée reste la bonne référence pour Kazo aujourd'hui (Kazo tourne en interprété ; vérification des consignes), mais l'AOT pourrait rapprocher .NET du JS pur (≈ 100 Mo/s), pas de `SubtleCrypto`.
- **R2 non mesuré** : aucun bucket public connu ; les faits viennent de la documentation Cloudflare lue ce jour (`r2/buckets/cors`, `public-buckets`, `platform/limits`, `pricing`), pas d'une mesure.
- **Pas de vrai `.zst` d'E1** : `essais/e1-wine-v86/images/` contient un rootfs à plat (3 818 petits `.bin.zst`, 212 Mo), pas une image unique ; le banc a tourné sur des données aléatoires de 800 Mio. Pour le débit et la vérification cela ne change rien (rien n'est compressé en transit) ; le comportement du cache avec un vrai nom de fichier non plus.
- **jsDelivr et `Range` à l'offset 0** : 206 avec un corps vide ou trop long, en curl comme dans les deux navigateurs ; non expliqué (représentation compressée à la périphérie ?). jsDelivr est de toute façon hors jeu (20 Mo).
- **`performance.measureUserAgentSpecificMemory()`** n'a jamais résolu pendant une passe (elle attend un ramasse-miettes ; appelée seule, elle répond) ; la mémoire est donc mesurée au niveau des processus Windows (`Win32_Process`, 3 à 7 échantillons par passe de 3 à 10 s) : les pics peuvent être sous-estimés de quelques dizaines de Mio, et les 2,4 Gio du `fetch` avec `integrity` sont un minimum.
- **Page Blazor sous un sous-chemin** : le premier test de fumée a bloqué 3 min parce que `index.html` publié a `<base href="/">` ; corrigé en réécrivant `<base href="/dotnet/interp/">` après la publication.
- **`/ctl?reset=1&cut=…`** remettait les pannes à zéro après les avoir réglées (ordre des paramètres) : corrigé avant les passes.
- **Débit localhost** : borné par le serveur Node de l'essai (flux de 256 Kio, `drain` à chaque écriture) à 150 à 320 Mio/s ; ce n'est pas une mesure du navigateur. Le scénario à 100 Mbit/s, lui, est fidèle.
- Rien laissé en marche : port 8098 libre à la fin, aucun profil `e3-*` dans le dossier temporaire, fichiers OPFS supprimés (vérifié à chaque passe), fichiers de 800 et 200 Mio supprimés (voir README).

## Limites

- Une seule machine (16 fils, 64 Go, SSD), Windows seulement, sans tête ; E2 a montré que le sans-tête est 20 à 40 % pessimiste pour les accès OPFS dans Chrome.
- Les pannes sont simulées par le serveur local (fermeture de socket, 503, limite de débit par réponse) : pas de perte de paquets, pas de latence, pas de changement d'adresse IP, pas de mise en veille. Un vrai réseau mobile ajoutera des erreurs d'autres formes (délais, DNS) que le worker traite par le même chemin (réessai), mais non mesurées.
- 100 Mbit/s est appliqué par connexion : un téléchargement parallèle (2 à 6 morceaux à la fois) n'a pas été mesuré ; sur un vrai réseau il peut aider, sur une limite partagée non.
- Variance : 10 à 25 % d'une passe à l'autre sur les temps totaux (Chrome 32 Mio : 3,6 / 4,0 / 4,0 s ; Firefox 32 Mio : 8,2 / 6,3 / 6,1 s) ; la mémoire varie de 20 % ; les écarts COI / sans COI sont dans cette marge.
- Les hébergeurs ont été sondés avec de petits fichiers publics existants (79 Kio à 900 Kio) : le comportement en 206 sur un fichier de 800 Mio et sous charge n'est pas mesuré ; les limites de taille viennent de leur documentation.
- Le cache HTTP a été mesuré dans un profil neuf : Chrome dimensionne son cache disque selon l'espace libre et refuse les entrées au-delà d'une fraction de cette taille ; sur un disque presque plein le seuil peut être plus bas que 32 Mio, pas plus haut.
- Non couvert : Safari, mobile, téléchargement en arrière-plan (Background Fetch), compression en transit, plusieurs images à la fois, cohabitation avec l'éviction du stockage (E2), mise à jour d'une image (téléchargement différentiel).

## Chiffres bruts

- `essais/e3-distribution/resultats/results-<navigateur>-<scénario>-<paramètres>-<n>.json` : une passe par fichier (temps par phase et par morceau, erreurs, journal du serveur résumé, mémoire des processus, agent utilisateur, erreurs de console, profil supprimé).
- `resultats/summary-<horodatage>.json` : toutes les passes de la passe complète ; `resultats/tableaux.md` : sortie de `node tableau.mjs` (médianes).
- `resultats/hosts-probe.txt`, `hosts-probe2.txt`, `hosts-probe3-jsdelivr.txt` : en-têtes bruts des sondes `curl` ; `resultats/cf-r2-*.html` : pages de documentation Cloudflare lues.
- `resultats/dotnet-publish-interp.log`, `workload-install.log` : publication Blazor et échec de l'installation du workload.
- `resultats/run-full.log`, `run-cache-small.log` : durée de chaque passe.

## Ce que ça veut dire pour Os-x86

1. **Le critère d'E3 est atteint ; l'hébergement sur serveur Kazo n'est pas « obligatoire » au sens de la conséquence d'échec, mais aucun hébergeur gratuit testé ne convient tel quel.** GitHub Releases est exclu (pas de CORS, mesuré dans Chrome et Firefox). jsDelivr est exclu (20 Mo, `Range` incohérent). GitHub Pages et `raw.githubusercontent.com` font ce qu'il faut (CORS, 206 sans prévol, ETag, cache 5 à 10 min) mais plafonnent à 100 Mio par fichier et 1 Go par site : ils peuvent porter **une** image découpée en fichiers de 32 Mio (25 fichiers pour 800 Mio) ou les petites images de test de la CI (Alpine sans Wine, FreeDOS), pas le catalogue. Pour les images complètes : **R2 avec un domaine personnalisé** (CORS par bucket, `Range`, sortie gratuite, `r2.dev` exclu pour la production) ou un **serveur Kazo** qui envoie `Access-Control-Allow-Origin`, `Accept-Ranges`, `ETag`, `Access-Control-Expose-Headers: Content-Range, ETag, Content-Length` et `Cache-Control: public, max-age=31536000, immutable` sur des noms versionnés. **À valider** : R2 n'a pas été mesuré ; il faut un compte Cloudflare et un domaine, donc une décision de Kazo.
2. **Morceaux de 32 Mio, un fichier par morceau si l'hébergeur le permet, sinon `Range` sur un fichier unique.** 32 Mio : 26 requêtes pour 800 Mio, 170 à 220 Mio de mémoire privée dans le processus de rendu (300 dans Firefox), au plus 32 Mio perdus par coupure, et c'est la plus grande taille que les trois navigateurs gardent encore en cache HTTP. 8 Mio coûte 100 requêtes et 1 s de fichier d'état (à corriger en gardant l'état en mémoire et en l'écrivant toutes les n secondes si on choisit 8) ; 128 Mio et plus montent en mémoire (340 Mio à 1,2 Gio) sans rien gagner. **À valider** sur mobile (mémoire) et sur un vrai réseau (parallélisme à 2 ou 3 morceaux).
3. **Le SHA-256 se fait en JS, avec `SubtleCrypto.digest` dans le worker de téléchargement, jamais en C# sous Kazo** : 1,1 à 1,25 Go/s contre 35 Mo/s interprété (24 s pour 800 Mio, en bloquant l'interface). Le manifeste porte un SHA-256 par morceau (et le SHA-256 du manifeste lui-même, signé ou servi depuis l'origine de l'app) ; la vérification complète d'une image déjà présente coûte 1 s (critère : 30 s). `fetch(..., { integrity })` est à proscrire pour les images (2,4 Gio de mémoire, pas de reprise) ; il reste bon pour le manifeste. **À valider** : AOT .NET non mesuré, mais même à 5 fois mieux il resterait 6 fois sous `SubtleCrypto`.
4. **Stratégie de cache : aucune confiance dans le cache HTTP pour les images ; l'OPFS est le cache.** Les trois navigateurs ne gardent pas les réponses de 64 Mio et plus (c'est le problème d'E1 : v86 redemande ses `.zst` à chaque démarrage parce qu'ils font 187 Mio). Os-x86 télécharge une fois en `cache: 'no-store'` dans l'OPFS (décision 149 A), et v86 doit lire l'image depuis l'OPFS (chargeur maison ou `-partial` sur une URL servie par un Service Worker depuis l'OPFS), pas par `Range` réseau à la demande (3 à 5 ms par bloc, 13 à 17 Mio/s, contre 10 à 30 µs dans l'OPFS). La Cache API marche pour 800 Mio mais doublerait le stockage compté dans le quota : inutile si l'OPFS a l'image. Les fichiers du runtime (v86 wasm, agent) restent sous 32 Mio et peuvent compter sur le cache HTTP avec des noms versionnés. **À valider** : comportement avec un disque presque plein (seuil plus bas).
5. **Reprise** : garder un fichier d'état par image dans l'OPFS (ETag ou version, taille, taille de morceau, liste des morceaux vérifiés) ; repartir au premier morceau non vérifié après rechargement ou redémarrage ; sur erreur réseau, réessayer avec délai croissant sans rien jeter ; comparer la version du manifeste, pas l'ETag de l'hébergeur (souvent illisible depuis le JS). Afficher la progression par morceau (décision 126 : consentement puis téléchargement visible). Mesuré dans les trois navigateurs.
6. **COOP/COEP ne change rien à la distribution** : ni les débits (écart dans la variance), ni les API utilisées, ni les CORS vers les hébergeurs qui envoient `ACAO: *`. Si Kazo active l'isolation, les images doivent venir d'une origine qui envoie CORS (ou `Cross-Origin-Resource-Policy: cross-origin`) : c'est déjà requis pour `Range`.
7. **À demander à Kazo** : (a) une origine d'hébergement des images (bucket R2 + domaine, ou serveur) avec les en-têtes du point 1 ; (b) l'accord pour que le téléchargement et le SHA-256 vivent dans un Web Worker JS livré par l'app Os-x86 (pas en C#), et pour `cache: 'no-store'` ; (c) la confirmation que l'OPFS (149 A) et `persist()` après un geste (177 A) s'appliquent aux 0,8 Go d'images, sachant que le quota annoncé dans un profil neuf est de 10,8 Gio ; (d) rien côté COOP/COEP.

**Recommandation à valider** : images hébergées sur R2 derrière un domaine Kazo (ou un serveur Kazo) avec CORS, `Range`, `Expose-Headers` et `immutable` ; découpage en morceaux de 32 Mio avec manifeste SHA-256 par morceau ; téléchargement dans un Web Worker avec `Range`, `SubtleCrypto.digest`, écriture directe dans l'OPFS et état de reprise dans l'OPFS ; aucune dépendance au cache HTTP pour les fichiers de plus de 32 Mio ; v86 lit l'image depuis l'OPFS. À remesurer : R2 réel (débit, 206 sur 800 Mio), un vrai réseau mobile avec pertes, la mémoire sur un appareil de 4 Go, et l'AOT .NET si Kazo l'envisage.
