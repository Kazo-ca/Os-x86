## Taille de morceau (Chrome, 800 Mio, localhost sans limite, COOP/COEP actifs)

| Mesure | 8 Mio | 32 Mio | 128 Mio | 512 Mio |
| --- | ---: | ---: | ---: | ---: |
| Passes | 3 | 3 | 3 | 3 |
| Temps total du téléchargement (s) | 5,4 | 4,0 | 3,2 | 3,6 |
| Débit (Mio/s) | 148 | 201 | 283 | 287 |
| Réseau (s) | 3,1 | 2,1 | 1,4 | 1,6 |
| SHA-256 des morceaux pendant le téléchargement (s) | 0,7 | 0,7 | 0,7 | 0,8 |
| Écriture OPFS (s) | 0,4 | 0,7 | 1,0 | 1,0 |
| État (fichier JSON après chaque morceau) (s) | 1,0 | 0,2 | 0,1 | 0,0 |
| `flush()` final (ms) | 179 | 156 | 63 | 22 |
| Mémoire privée du processus de rendu, pic (Mio) | 356 | 216 | 339 | 1 254 |
| Mémoire privée du processus de rendu, au repos (Mio) | 44 | 45 | 46 | 44 |
| Requêtes vues par le serveur | 101 | 26 | 8 | 3 |
| Vérification sur place (lecture OPFS + SHA-256) (s) | 1,1 | 1,1 | 1,0 | 1,1 |
| dont lecture OPFS (s) / SHA-256 (s) | 0,3 / 0,7 | 0,3 / 0,7 | 0,3 / 0,7 | 0,3 / 0,8 |
| Vérification (Mio/s) | 770 | 767 | 799 | 748 |

## Morceaux de 32 Mio par navigateur, avec et sans COOP/COEP

| Mesure | Chrome COI | Chrome sans COI | Edge COI | Firefox COI | Firefox sans COI |
| --- | ---: | ---: | ---: | ---: | ---: |
| Passes | 3 | 3 | 3 | 3 | 3 |
| Temps total du téléchargement (s) | 4,0 | 3,5 | 4,3 | 6,3 | 5,6 |
| Débit (Mio/s) | 201 | 230 | 186 | 127 | 144 |
| Réseau (s) | 2,1 | 1,8 | 2,2 | 4,3 | 3,7 |
| SHA-256 des morceaux pendant le téléchargement (s) | 0,7 | 0,6 | 0,8 | 0,7 | 0,7 |
| Écriture OPFS (s) | 0,7 | 0,6 | 0,7 | 0,5 | 0,5 |
| État (fichier JSON après chaque morceau) (s) | 0,2 | 0,2 | 0,3 | 0,4 | 0,4 |
| `flush()` final (ms) | 156 | 161 | 132 | 209 | 242 |
| Mémoire privée du processus de rendu, pic (Mio) | 216 | 168 | 186 | 299 | 320 |
| Mémoire privée du processus de rendu, au repos (Mio) | 45 | 45 | 20 | 83 | 84 |
| Requêtes vues par le serveur | 26 | 26 | 26 | 26 | 26 |
| Vérification sur place (lecture OPFS + SHA-256) (s) | 1,1 | 0,9 | 1,1 | 1,0 | 0,9 |
| dont lecture OPFS (s) / SHA-256 (s) | 0,3 / 0,7 | 0,2 / 0,6 | 0,3 / 0,8 | 0,3 / 0,7 | 0,2 / 0,7 |
| Vérification (Mio/s) | 767 | 922 | 766 | 828 | 889 |

## Sans COOP/COEP (Chrome) : toutes les tailles

| Mesure | 8 Mio | 32 Mio | 128 Mio | 512 Mio |
| --- | ---: | ---: | ---: | ---: |
| Passes | 3 | 3 | 3 | 3 |
| Temps total du téléchargement (s) | 5,9 | 3,5 | 2,8 | 3,1 |
| Débit (Mio/s) | 135 | 230 | 319 | 326 |
| Réseau (s) | 3,9 | 1,8 | 1,2 | 1,4 |
| SHA-256 des morceaux pendant le téléchargement (s) | 0,7 | 0,6 | 0,6 | 0,7 |
| Écriture OPFS (s) | 0,3 | 0,6 | 0,8 | 0,9 |
| État (fichier JSON après chaque morceau) (s) | 0,9 | 0,2 | 0,1 | 0,0 |
| `flush()` final (ms) | 255 | 161 | 46 | 11 |
| Mémoire privée du processus de rendu, pic (Mio) | 373 | 168 | 328 | 1 218 |
| Mémoire privée du processus de rendu, au repos (Mio) | 44 | 45 | 48 | 44 |
| Requêtes vues par le serveur | 101 | 26 | 8 | 3 |
| Vérification sur place (lecture OPFS + SHA-256) (s) | 0,9 | 0,9 | 0,9 | 1,1 |
| dont lecture OPFS (s) / SHA-256 (s) | 0,2 / 0,7 | 0,2 / 0,6 | 0,2 / 0,6 | 0,3 / 0,8 |
| Vérification (Mio/s) | 919 | 922 | 928 | 768 |

## Reprise après coupure (morceaux de 32 Mio)

| Mesure | Chrome | Edge | Firefox |
| --- | ---: | ---: | ---: |
| Passes | 3 | 1 | 1 |
| (a) worker qui réessaie : réponses coupées / 503 servies | 3 / 2 | 3 / 2 | 3 / 2 |
| (a) nouvelles tentatives | 5 | 5 | 5 |
| (a) octets reçus perdus (Mio) | 60 | 60 | 60 |
| (a) temps total (s) | 5,4 | 5,1 | 7,4 |
| (a) morceaux vérifiés à la fin | 25 / 25 | 25 / 25 | 25 / 25 |
| (b) rechargement : morceaux déjà vérifiés au départ des tentatives 1 à 4 | 0 → 5 → 10 → 15 | 0 → 5 → 10 → 15 | 0 → 5 → 10 → 15 |
| (b) morceaux téléchargés par tentative | 5 / 5 / 5 / 10 | 5 / 5 / 5 / 10 | 5 / 5 / 5 / 10 |
| (b) octets perdus par tentative (Mio) | 20 / 20 / 20 / 0 | 20 / 20 / 20 / 0 | 20 / 20 / 20 / 0 |
| (b) temps par tentative (s) | 0,9 / 0,9 / 0,9 / 1,5 | 0,8 / 0,9 / 0,9 / 1,5 | 1,3 / 1,5 / 1,3 / 2,6 |
| (b) vérification finale : morceaux bons | 25 | 25 | 25 |

## Débit limité à 100 Mbit/s (Chrome, morceaux de 32 Mio)

| Mesure | Valeur |
| --- | ---: |
| Passes | 3 |
| Temps total (s) | 68,5 |
| Débit (Mio/s) / (Mbit/s) | 11,7 / 97,9 |
| SHA-256 pendant le téléchargement (s) | 0,7 |
| Vérification sur place après (s) | 0,9 |
| Mémoire privée du processus de rendu, pic (Mio) | 192 |

## `fetch` d'un seul tenant (800 Mio en mémoire)

| Mesure | Chrome | Edge | Firefox |
| --- | ---: | ---: | ---: |
| Passes | 3 | 1 | 1 |
| Avec `integrity` : temps total (s) | 4,3 | 4,4 | 7,6 |
| Avec `integrity` : corps reçu et vérifié (s) | 3,3 | 3,4 | 6,7 |
| Avec `integrity` : écriture OPFS (s) | 1,0 | 1,0 | 0,8 |
| Avec `integrity` : mémoire privée du rendu, pic (Mio) | 2 445 | 2 224 | 1 063 |
| Sans `integrity` : temps total (s) | 2,0 | 2,2 | 5,0 |
| Sans `integrity` : mémoire privée du rendu, pic (Mio) | 848 | 849 | 1 068 |
| Erreur | aucune | aucune | aucune |

## Lectures `Range` à la demande (comme v86), 500 lectures aléatoires

| Mesure | Chrome | Firefox |
| --- | ---: | ---: |
| 64 Kio, sans limite : médiane / p99 (ms) | 3,5 / 8,7 | 4,0 / 13,1 |
| 64 Kio, sans limite : débit (Mio/s) | 17,3 | 13,4 |
| 4 Kio, sans limite : médiane / p99 (ms) | 2,7 / 7,4 | 3,4 / 5,6 |
| 64 Kio à 100 Mbit/s : médiane / p99 (ms) | 4,2 / 14,2 | 4,9 / 19,9 |
| 64 Kio à 100 Mbit/s : débit (Mio/s) | 8,1 | 8,2 |

## Cache HTTP et Cache API

### 200 Mio, `Cache-Control: public, max-age=3600`

| Étape | Chrome : requêtes serveur (statuts), temps | Edge | Firefox |
| --- | --- | --- | --- |
| fetch 1 (défaut) | 1 (200), 0,4 s | 1 (200), 0,5 s | 1 (200), 1,0 s |
| fetch 2 (défaut, juste après) | 1 (200), 0,4 s | 1 (200), 0,5 s | 1 (200), 0,9 s |
| fetch 3 (cache: force-cache) | 1 (200), 0,4 s | 1 (200), 0,4 s | 1 (200), 1,0 s |
| fetch 4 (cache: no-cache = revalidation) | 1 (200), 0,5 s | 1 (200), 0,4 s | 1 (200), 0,9 s |
| Cache API : cache.add(url) | 1 (200), 0,8 s | 1 (200), 0,9 s | 1 (200), 1,1 s |
| Cache API : cache.match(url) + arrayBuffer | 0, 0,2 s | 0, 0,3 s | 0, 0,7 s |
| Cache API : cache.match une 2e fois | 0, 0,3 s | 0, 0,3 s | 0, 0,7 s |
| Usage annoncé avec la Cache API (Mio) | 200 | 200 | 200 |

### 200 Mio, `Cache-Control: no-store`

| Étape | Chrome : requêtes serveur (statuts), temps | Edge | Firefox |
| --- | --- | --- | --- |
| fetch 1 (défaut) | 1 (200), 0,3 s | 1 (200), 0,3 s | 1 (200), 0,9 s |
| fetch 2 (défaut, juste après) | 1 (200), 0,3 s | 1 (200), 0,3 s | 1 (200), 0,8 s |
| fetch 3 (cache: force-cache) | 1 (200), 0,3 s | 1 (200), 0,3 s | 1 (200), 0,8 s |
| fetch 4 (cache: no-cache = revalidation) | 1 (200), 0,4 s | 1 (200), 0,4 s | 1 (200), 0,8 s |
| Cache API : cache.add(url) | 1 (200), 0,9 s | 1 (200), 0,9 s | 1 (200), 1,0 s |
| Cache API : cache.match(url) + arrayBuffer | 0, 0,3 s | 0, 0,3 s | 0, 0,7 s |
| Cache API : cache.match une 2e fois | 0, 0,3 s | 0, 0,3 s | 0, 0,6 s |
| Usage annoncé avec la Cache API (Mio) | 200 | 200 | 200 |

### 800 Mio, `Cache-Control: public, max-age=3600`

| Étape | Chrome : requêtes serveur (statuts), temps | Edge | Firefox |
| --- | --- | --- | --- |
| fetch 1 (défaut) | 1 (200), 1,7 s | 1 (200), 1,5 s | 1 (200), 3,7 s |
| fetch 2 (défaut, juste après) | 1 (200), 1,5 s | 1 (200), 1,4 s | 1 (200), 4,5 s |
| fetch 3 (cache: force-cache) | 1 (200), 1,7 s | 1 (200), 1,3 s | 1 (200), 3,5 s |
| fetch 4 (cache: no-cache = revalidation) | 1 (200), 2,0 s | 1 (200), 1,4 s | 1 (200), 3,5 s |
| Cache API : cache.add(url) | 1 (200), 4,0 s | 1 (200), 3,7 s | 1 (200), 4,2 s |
| Cache API : cache.match(url) + arrayBuffer | 0, 1,2 s | 0, 1,2 s | 0, 2,6 s |
| Cache API : cache.match une 2e fois | 0, 1,4 s | 0, 1,1 s | 0, 2,7 s |
| Usage annoncé avec la Cache API (Mio) | 800 | 800 | 800 |

### Témoin : seuil du cache HTTP (même `Cache-Control: public, max-age=3600`, 1 passe)

| Taille | Chrome : requêtes vues par le serveur aux fetch 1 / 2 / 3 (force-cache) / 4 (no-cache) | Edge | Firefox |
| --- | --- | --- | --- |
| 8 Mio | 1 (200) / 0 / 0 / 1 (304) | 1 (200) / 0 / 0 / 1 (304) | 1 (200) / 0 / 0 / 1 (304) |
| 32 Mio | 1 (200) / 0 / 0 / 1 (304) | 1 (200) / 0 / 0 / 1 (304) | 1 (200) / 0 / 0 / 1 (304) |
| 64 Mio | 1 (200) / 1 (200) / 1 (200) / 1 (200) | 1 (200) / 1 (200) / 1 (200) / 1 (200) | 1 (200) / 1 (200) / 1 (200) / 1 (200) |
| 128 Mio | 1 (200) / 1 (200) / 1 (200) / 1 (200) | 1 (200) / 1 (200) / 1 (200) / 1 (200) | 1 (200) / 1 (200) / 1 (200) / 1 (200) |

## Range inter-origine depuis le navigateur (1 Kio par hôte)

| Hôte | Chrome COI | Chrome sans COI | Firefox COI | Firefox sans COI |
| --- | --- | --- | --- | --- |
| GitHub Pages (microsoft.github.io) | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent |
| raw.githubusercontent.com | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent | 206, 1024 o, Content-Range absent |
| jsDelivr (npm) | 206, 0 o, Content-Range lu | 206, 0 o, Content-Range lu | 206, 0 o, Content-Range lu | 206, 0 o, Content-Range lu |
| GitHub Releases (redirection github.com) | échec : Failed to fetch | échec : Failed to fetch | échec : NetworkError when attempting to fetch resource. | échec : NetworkError when attempting to fetch resource. |
| GitHub Releases sans Range | échec : Failed to fetch | échec : Failed to fetch | échec : NetworkError when attempting to fetch resource. | échec : NetworkError when attempting to fetch resource. |

## SHA-256 : .NET WebAssembly contre SubtleCrypto et JS pur (64 Mio)

| Mesure | Chrome | Edge | Firefox |
| --- | ---: | ---: | ---: |
| `SubtleCrypto.digest` (Mo/s, meilleure de 3) | 1 253 | 1 101 | 1 107 |
| SHA-256 en JS pur (Mo/s, meilleure de 3) | 112 | 97 | 50 |
| .NET 10 interprété : `SHA256.HashData` (Mo/s) | 35 | 35 | 35 |
| .NET 10 interprété : `IncrementalHash` par 1 Mio (Mo/s) | 35 | 35 | 35 |
| .NET 10 interprété : boucle C# de somme d'octets (Mo/s) | 184 | 173 | 144 |
| .NET 10 interprété : temps de chargement + hachage, page entière (s) | 13,5 | 13,2 | 14,2 |
| .NET 10 AOT : `SHA256.HashData` (Mo/s) | — | — | — |
| Temps pour 800 Mio en .NET interprété, extrapolé (s) | 24 | 24 | 24 |

## Versions

- chrome : 156.0.8078.80
- msedge : 157.0.4341.42
- firefox : 159.0
