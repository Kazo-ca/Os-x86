# Essai E2 — stockage des disques : OPFS contre IndexedDB

Date : 2026-11-10. Code jetable dans `essais/e2-stockage/` (question 101, ligne E2 des études prévues). Cet essai couvre la première partie de E2 : le banc de lectures et d'écritures de blocs sur une image de 1 Gio, le quota et `persist()`. La copie-sur-écriture (par fichiers ou par blocs), la base Linux/Wine partagée ou séparée, et « garder contre recréer » ne sont pas mesurés ici ; ils restent à faire. Les Fichiers de Kazo (IndexedDB, morceaux de 256 Kio, 16 Mio au plus par fichier) ne sont pas mesurés directement : le banc IndexedDB reproduit leur disposition (un enregistrement par morceau de 256 Kio) et donne donc ce qu'ils feraient au mieux si la limite de 16 Mio était levée.

## But

Savoir si un émulateur dans le navigateur (v86 dans un Web Worker) peut lire et écrire des blocs de 4 Kio et de 64 Kio dans une image de 1 Gio avec une latence acceptable, et à quel prix dans chacun des deux rangements possibles : un fichier OPFS ouvert avec `createSyncAccessHandle()`, ou une base IndexedDB de morceaux de 256 Kio. Vérifier aussi ce que les navigateurs annoncent comme quota, ce que répond `persist()`, et que tout est nettoyé après.

## Protocole

Une page statique (`bench.html`) servie par un petit serveur Node local (`server.mjs`, port libre, en-têtes COOP/COEP pour obtenir l'isolation d'origine croisée et donc une horloge plus fine). La page lance un Web Worker (`bench-worker.js`) qui fait, dans l'ordre :

1. **OPFS** : création d'un fichier, `createSyncAccessHandle()`, écriture séquentielle de 1 Gio par morceaux de 1 Mio, `flush()` ; puis 2000 lectures aléatoires de 4 Kio, 2000 écritures aléatoires de 4 Kio, `flush()`, même chose en 64 Kio ; puis 200 écritures de 4 Kio suivies chacune d'un `flush()` ; fermeture, réouverture et lecture séquentielle de 64 Mio ; suppression du fichier et vérification qu'il n'existe plus.
2. **IndexedDB** : base neuve, magasin sans chemin de clé, 4096 enregistrements de 256 Kio (`ArrayBuffer`, clé = index) écrits par transactions de 32 `put` ; 2000 `get` aléatoires (une transaction chacun) ; 2000 `put` aléatoires (une transaction chacun) ; 2000 `put` aléatoires groupés par 16 dans une transaction ; 2000 écritures d'un bloc de 4 Kio faites comme un vrai système de fichiers par morceaux doit les faire, c'est-à-dire `get` du morceau, modification, `put` du morceau (lecture-modification-écriture) ; suppression de la base et vérification par `indexedDB.databases()`.
3. **Stockage** : `navigator.storage.estimate()` avant, après l'écriture OPFS, après le remplissage IndexedDB et après le nettoyage ; `navigator.storage.persist()` (abandonné après 5 s si aucune réponse) ; présence de `createSyncAccessHandle` dans le worker.

Les données écrites sont un motif pseudo-aléatoire, pas des zéros. Les latences sont prises par opération avec `performance.now()` (médiane, p99) ; la moyenne vient du temps total divisé par le nombre d'opérations, ce qui la rend indépendante de la résolution de l'horloge. Chaque navigateur est lancé par Playwright dans un **profil tout neuf sur disque** (`launchPersistentContext` dans un dossier temporaire supprimé après la passe), trois passes par navigateur, et le tableau donne la **médiane des trois passes**. Les fichiers `results-<navigateur>-<n>.json` contiennent chaque passe.

Pourquoi un profil persistant : un contexte `newContext()` de Playwright est éphémère (type navigation privée). Dans Chrome, cet essai y a mesuré 235 µs par lecture de 4 Kio et 10 Gio de quota ; avec un profil sur disque, 10 µs. Les chiffres du contexte éphémère ne représentent pas ce qu'un usager aurait ; ils ne sont pas repris ici.

## Machine et navigateurs

| Élément | Valeur |
|---|---|
| Machine | AMD Ryzen 7 3700X (8 cœurs, 16 fils), 64 Go de RAM, Windows 11 Pro 10.0.26200, disque C: avec 14 Go libres sur 786 Go au moment de l'essai |
| Node / Playwright | Node 24.15.0, Playwright 1.65.0 installé localement dans `essais/e2-stockage/` |
| Chrome | 156.0.8078.80 installé sur la machine (`channel: 'chrome'`) |
| Edge | 157.0.4341.42 installé sur la machine (`channel: 'msedge'`) |
| Chromium | Chrome for Testing 158.0.8127.18, build téléchargé par Playwright (en mode sans tête, Playwright utilise sa « coquille sans tête ») |
| Firefox | 159.0, build téléchargé par Playwright (Firefox n'est pas installé sur la machine) |
| Safari | non mesuré (pas de Mac) |
| Mode | sans tête pour les trois passes ; une passe « avec fenêtre » en plus pour Chrome (voir plus bas) |
| État du stockage | froid : profil neuf à chaque passe ; mais la machine a 64 Go de RAM, donc le cache de fichiers de Windows garde le 1 Gio entier (voir les limites) |

| Élément | Chrome | Edge | Chromium (Playwright) | Firefox (Playwright) |
|---|---:|---:|---:|---:|
| `createSyncAccessHandle` dans le worker | oui | oui | oui | oui |
| `createSyncAccessHandle` dans la page | non | non | non | non |
| Résolution de `performance.now()` dans le worker (µs) | 5 | 5 | 5 | 20 |
| Quota annoncé par `estimate()` (Gio) | 10,0 | 10,0 | 10,0 | 10,0 |
| `persist()` | `false` | `false` | `false` | pas de réponse en 5 s (invite à l'usager) |
| Usage annoncé après l'écriture OPFS (Mio) | 1 024 | 1 024 | 1 024 | 1 024 |
| Usage annoncé après le remplissage IndexedDB (Mio) | 1 026 | 1 026 | 1 026 | 1 028 |
| Usage annoncé après le nettoyage (Mio) | 0,2 | 0,2 | 0,2 | 0,5 |
| Taille du profil après la passe, avant sa suppression (Mio) | 9,8 | 14,4 | 2,5 | 32,5 |
| Durée d'une passe (s) | 40 | 40 | 41 | 118 |

## Résultats : OPFS (`createSyncAccessHandle`, fichier de 1 Gio)

Médiane des trois passes. Mio = 1 048 576 octets. Pour les latences de quelques microsecondes, la médiane est arrondie par l'horloge (pas de 5 µs dans Chrome, de 20 µs dans Firefox) : la moyenne est plus fiable.

| Mesure | Chrome | Edge | Chromium (Playwright) | Firefox (Playwright) |
|---|---:|---:|---:|---:|
| Écriture séquentielle par 1 Mio (Mio/s) | 1 723 | 1 399 | 1 765 | 1 656 |
| `flush()` après l'écriture du 1 Gio (ms) | 413 | 1 276 | 880 | 355 |
| Lecture aléatoire 4 Kio — moyenne (µs) | 10,1 | 11,7 | 8,1 | 59,6 |
| Lecture aléatoire 4 Kio — médiane / p99 (µs) | 10 / 30 | 10 / 40 | 5 / 30 | 60 / 100 |
| Lecture aléatoire 4 Kio — opérations/s | 98 600 | 85 300 | 123 100 | 16 800 |
| Écriture aléatoire 4 Kio — moyenne (µs) | 29,6 | 30,1 | 25,9 | 181,5 |
| Écriture aléatoire 4 Kio — médiane / p99 (µs) | 20 / 130 | 25 / 110 | 20 / 70 | 180 / 260 |
| Écriture aléatoire 4 Kio — opérations/s | 33 800 | 33 300 | 38 600 | 5 500 |
| `flush()` après 2000 écritures de 4 Kio (ms) | 110 | 112 | 94 | 85 |
| Lecture aléatoire 64 Kio — moyenne (µs) | 32,3 | 34,2 | 31,1 | 62,7 |
| Lecture aléatoire 64 Kio — médiane / p99 (µs) | 25 / 120 | 25 / 135 | 20 / 120 | 60 / 120 |
| Lecture aléatoire 64 Kio — opérations/s | 30 900 | 29 300 | 32 100 | 15 900 |
| Écriture aléatoire 64 Kio — moyenne (µs) | 64,3 | 70,8 | 76,9 | 195,2 |
| Écriture aléatoire 64 Kio — médiane / p99 (µs) | 50 / 210 | 55 / 250 | 50 / 600 | 200 / 280 |
| Écriture aléatoire 64 Kio — opérations/s | 15 500 | 14 100 | 13 000 | 5 100 |
| `flush()` après 2000 écritures de 64 Kio (ms) | 149 | 188 | 159 | 124 |
| Écriture 4 Kio + `flush()` à chaque fois — médiane / p99 (µs) | 500 / 965 | 545 / 4 805 | 530 / 760 | 660 / 1 340 |
| Réouverture puis lecture séquentielle de 64 Mio (Mio/s) | 2 905 | 3 579 | 4 172 | 4 372 |
| Fichier supprimé et absent ensuite | oui | oui | oui | oui |

## Résultats : IndexedDB (4096 enregistrements de 256 Kio, clé = index)

| Mesure | Chrome | Edge | Chromium (Playwright) | Firefox (Playwright) |
|---|---:|---:|---:|---:|
| Remplissage séquentiel, 32 `put` par transaction (Mio/s) | 163 | 174 | 143 | 40 |
| Temps pour écrire le 1 Gio (s) | 6,3 | 5,9 | 7,2 | 25,6 |
| `get` aléatoire d'un morceau — médiane / p99 (µs) | 1 335 / 6 800 | 1 220 / 2 455 | 1 200 / 2 660 | 3 300 / 4 440 |
| `get` aléatoire — opérations/s | 607 | 703 | 763 | 302 |
| `put` aléatoire, une transaction par `put` — médiane / p99 (µs) | 4 385 / 9 250 | 4 280 / 12 000 | 4 090 / 10 165 | 10 480 / 76 740 |
| `put` aléatoire, une transaction par `put` — opérations/s | 216 | 215 | 223 | 86 |
| `put` aléatoire, 16 par transaction — coût moyen par `put` (µs) | 1 917 | 1 829 | 2 297 | 9 891 |
| `put` aléatoire, 16 par transaction — `put`/s | 522 | 547 | 435 | 101 |
| **Écriture d'un bloc de 4 Kio** (lecture-modification-écriture du morceau) — médiane / p99 (µs) | **5 555** / 11 070 | **5 435** / 15 640 | **5 565** / 19 580 | **12 680** / 70 100 |
| Écriture d'un bloc de 4 Kio — opérations/s | 171 | 168 | 151 | 74 |
| Base supprimée et absente ensuite | oui | oui | oui | oui |

Le coût effectif d'une écriture aléatoire de 4 Kio dans IndexedDB est donc celui de la ligne en gras : il faut relire le morceau de 256 Kio, le modifier et le réécrire, dans une transaction. Grouper les écritures par 16 divise le coût par deux dans Chrome, pas dans Firefox.

## Mise en regard

| Opération | OPFS (Chrome / Firefox) | IndexedDB (Chrome / Firefox) | Rapport |
|---|---:|---:|---:|
| Lecture aléatoire d'un bloc de 4 Kio | 10 µs / 60 µs | 1 335 µs / 3 300 µs (lecture du morceau entier) | ≈ 130× / 55× |
| Écriture aléatoire d'un bloc de 4 Kio | 30 µs / 180 µs | 5 555 µs / 12 680 µs | ≈ 185× / 70× |
| Écriture aléatoire d'un bloc de 64 Kio | 64 µs / 195 µs | 5 555 µs / 12 680 µs (même coût, un morceau) | ≈ 85× / 65× |
| Écrire une image de 1 Gio d'un coup | 0,6 s + `flush()` 0,4 s / 0,6 s + 0,4 s | 6,3 s / 25,6 s | ≈ 6× / 26× |

## Chrome avec fenêtre contre sans tête

Une passe de Chrome 156 avec fenêtre (même profil neuf, même machine) contre la médiane des trois passes sans tête :

| Mesure | Avec fenêtre (1 passe) | Sans tête (médiane de 3) |
|---|---:|---:|
| Écriture séquentielle (Mio/s) | 2 217 | 1 723 |
| Lecture aléatoire 4 Kio — moyenne (µs) | 6,9 | 10,1 |
| Écriture aléatoire 4 Kio — moyenne (µs) | 20,3 | 29,6 |
| Écriture 4 Kio + `flush()` — médiane (µs) | 430 | 500 |
| IndexedDB `get` — médiane (µs) | 1 055 | 1 335 |
| IndexedDB écriture d'un bloc de 4 Kio — médiane (µs) | 4 450 | 5 555 |

Avec fenêtre, Chrome est 20 à 40 % plus rapide sur tout, OPFS comme IndexedDB. Le mode sans tête est donc un peu pessimiste, mais il ne change ni l'ordre de grandeur ni le classement. Les chiffres sans tête sont gardés comme référence parce qu'ils viennent de trois passes.

## Limites de la mesure

- **Cache de fichiers.** Avec 64 Go de RAM, Windows garde le 1 Gio entier en mémoire : les lectures OPFS ne touchent pas le disque, et les écritures y vont par `flush()` (0,3 à 1,3 s pour 1 Gio, soit un SSD rapide). Sur une machine de 8 Go avec un disque lent, les lectures aléatoires OPFS seront plus lentes la première fois ; la relation entre OPFS et IndexedDB, elle, tient parce qu'IndexedDB passe aussi par ce cache.
- **Disque presque plein.** Les quatre navigateurs annoncent exactement 10 Gio de quota : pour Firefox c'est sa limite connue, mais pour Chrome, Edge et Chromium c'est vraisemblablement une conséquence des 14 Go libres sur le disque au moment de l'essai (le quota suit l'espace libre). À remesurer sur un disque avec de la place.
- **Profils neufs.** `persist()` répond `false` dans un profil neuf de Chrome, Edge et Chromium (aucune interaction, aucun signal d'engagement) ; Firefox pose la question à l'usager, et sans fenêtre personne ne répond. Un profil réel d'usager peut répondre autrement ; ce qui est acquis, c'est qu'on ne peut pas compter dessus sans geste de l'usager.
- **Builds Playwright.** Chromium et Firefox sont les builds téléchargés par Playwright, pas des navigateurs installés ; Chrome et Edge sont ceux de la machine. Les quatre donnent le même tableau pour OPFS côté Chromium ; Firefox n'a pas de point de comparaison installé.
- **Sans tête.** Voir la section précédente : 20 à 40 % pessimiste pour Chrome.
- **Variance.** D'une passe à l'autre, les latences aléatoires OPFS varient de 0 à 30 % (souvent un seul pas d'horloge), les débits séquentiels de 10 à 35 % (surtout la lecture après réouverture et le `flush()` du 1 Gio, qui dépendent de ce que le système a déjà écrit en arrière-plan), IndexedDB de 1 à 16 %. Trois passes suffisent pour l'ordre de grandeur, pas pour départager des écarts de moins de 30 %.
- **Horloge.** `performance.now()` a un pas de 5 µs dans Chrome et de 20 µs dans Firefox même avec l'isolation d'origine croisée ; les médianes sous 50 µs sont donc grossières. Les moyennes et les opérations/s n'ont pas ce défaut.
- **Une seule machine, un seul disque, pas de Safari, pas de mobile.**
- **Non couvert** : copie-sur-écriture, image de base partagée, coût de conservation, lecture vraiment froide (après redémarrage), plusieurs onglets en même temps, Fichiers de Kazo eux-mêmes.

## Nettoyage

Chaque passe supprime son fichier OPFS (vérifié : `getFileHandle` répond `NotFoundError`), sa base IndexedDB (vérifié : absente de `indexedDB.databases()`), puis le profil temporaire entier (vérifié : dossier absent). `estimate()` retombe à 0,2 à 0,5 Mio après le nettoyage. Aucun dossier `e2-*` ne reste dans le dossier temporaire. Ce qui reste sur la machine : les navigateurs téléchargés par Playwright (Chromium 450 Mo, coquille sans tête 286 Mo, Firefox 353 Mo, dans le dossier de cache de Playwright de l'usager) et `essais/e2-stockage/node_modules` (18 Mo).

## Où sont les chiffres bruts

- `essais/e2-stockage/resultats/results-<navigateur>-<n>.json` : une passe complète par fichier (toutes les statistiques, `estimate()` à chaque étape, agent utilisateur, erreurs de console).
- `essais/e2-stockage/resultats/summary.json` : médiane feuille par feuille des trois passes.
- `essais/e2-stockage/resultats/results-chrome-headed-1.json` et `summary-headed.json` : la passe avec fenêtre.
- `node tableau.mjs` imprime les tableaux ci-dessus depuis `summary.json`.

## Ce que ça veut dire pour Os-x86

1. **OPFS avec `createSyncAccessHandle()` est le seul rangement de blocs utilisable** pour une VM : 10 à 30 µs par bloc de 4 Kio dans Chrome et Edge, 60 à 180 µs dans Firefox, soit de 5 000 à 100 000 opérations par seconde. v86 fait ses accès disque depuis son worker, et c'est exactement là que l'API existe (elle est absente de la page).
2. **IndexedDB par morceaux de 256 Kio coûte 5 à 13 ms par écriture de bloc**, 70 à 185 fois plus qu'OPFS, et 1 à 3 ms par lecture. Un système de fichiers de ce type (c'est la disposition des Fichiers de Kazo) ne peut pas porter l'image disque d'une VM, même si la limite de 16 Mio était levée. Il reste bon pour ce qu'il fait déjà : des fichiers de l'usager, lus et écrits en entier. Un cache de morceaux en mémoire cacherait les lectures, pas les écritures.
3. **`flush()` se paie par lot, pas par bloc** : 0,5 ms par écriture si on le fait à chaque fois, 100 ms après 2000 écritures. Un `flush()` périodique (par exemple toutes les secondes, et avant une sauvegarde d'état) coûte presque rien ; un `flush()` par écriture plafonnerait à 2000 écritures par seconde.
4. **Firefox est 5 à 6 fois plus lent qu'OPFS sous Chrome pour les accès aléatoires**, mais 5 000 écritures de 4 Kio par seconde (20 Mio/s en petites écritures) restent très au-dessus de ce qu'un invité Wine demande. Firefox n'a pas besoin d'un chemin différent, seulement d'être mesuré dans E1.
5. **Le quota et `persist()` ne sont pas acquis** : aucun navigateur n'accorde la persistance sans geste de l'usager, et le quota suit l'espace libre du disque (10 Gio ici, sur un disque presque plein). Les disques invités peuvent donc être évincés par le navigateur, et une image de 1 Gio plus une base Linux/Wine tiennent dans 10 Gio mais pas plusieurs fois.
6. **Importer une image** se fait à plus de 1 Gio/s dans OPFS (avant `flush()`), contre 6 s dans IndexedDB sous Chrome et 26 s sous Firefox : un téléchargement écrit directement dans OPFS n'est jamais limité par le stockage.

**Recommandation à valider** : ranger les images disque des VM (base et différences) dans l'OPFS, ouvertes par `createSyncAccessHandle()` depuis le worker de v86, avec un `flush()` périodique et avant toute sauvegarde d'état ; garder IndexedDB et les Fichiers de Kazo pour les métadonnées, les états et les fichiers de l'usager, pas pour les blocs ; demander `persist()` après un geste de l'usager, afficher si elle est accordée, et traiter l'éviction comme un risque réel (d'où l'intérêt d'un export ou d'une synchro, §9 et §10 des consignes) ; refaire la mesure du quota sur un disque avec de la place, et remesurer les accès OPFS à froid sur une machine de 8 Go avant de fixer un chiffre pour E1. Les parties de E2 non couvertes ici (copie-sur-écriture, base partagée, garder contre recréer) restent à faire et demandent leur propre banc.
