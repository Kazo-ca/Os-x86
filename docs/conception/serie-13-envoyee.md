# Série 13 — envoyée le 2026-11-11

Les propositions des deux essais du 2026-11-11 : l'essai Wine dédié ([../essais/e17-wine-dedie.md](../essais/e17-wine-dedie.md), 170 A) et E3 ([../essais/e3-distribution.md](../essais/e3-distribution.md)). Même protocole : une lettre par numéro, deux choix réels au moins, une recommandation ✨, « Autre » toujours possible ; rien n'est accepté sans ta réponse par numéro. Numéros 201 à 204.

Ce qui est déjà réglé par les règles que tu as validées, sans question : l'essai de 170 a réussi au jour 1, donc **Windows graphique reste en fin de V1 (J3)** et il n'y a pas de jour 2 ; le Wine de l'image est celui qui a réussi, **WineHQ 11.0 i386**, épinglé (187 A).

---

**201. La base de l'image Linux avec Wine (187 A disait « Alpine 3.24 ; sinon le Wine qui a réussi l'essai, chroot glibc »)**
> Mesuré : le Wine 11.0 d'Alpine garde ses fenêtres à 1x1 (bogue propre à cette build musl) ; le Wine 11.0 de WineHQ marche, mais il est compilé pour glibc. L'essai l'a fait tourner sur un invité **Debian trixie i386 entier** (rootfs Debian, noyau `linux-virt` d'Alpine, init minimal) : boot 49 s (62 s sur Alpine), image 380 Mio zstd (187 Mio pour Alpine), dégraissable vers 250 Mio ; bouteille préfabriquée dans Docker en 60 s. L'autre voie, un chroot glibc **dans** Alpine, n'a pas été testée (deux bibliothèques C dans la même image, plus petit en principe).

- A) **Debian trixie i386 + paquets WineHQ i386** (stable 11.0 épinglée), noyau `linux-virt` d'Alpine, init minimal, une passe de dégraissage (modules utiles seulement, `apt` et locales retirés) avec un objectif de 250 Mio zstd à vérifier ; règle de suivi d'E13 (vérifier à chaque Wine stable que `binary-i386` est encore publié ; repli : Wine recompilé par nous) ✨(Recommandé)
  - Ce qui a été mesuré ; un seul Wine à suivre ; Debian i386 reste maintenu comme « code hérité » sans noyau, ce que le noyau d'Alpine comble.
- B) Alpine 3.24 + chroot glibc (Debian i386) pour Wine seulement : image plus petite, mais deux libc, un essai de plus, et deux distributions à suivre
- C) Alpine 3.24 + Wine i386 recompilé par nous contre musl, avec le correctif à trouver (jour 2 de 170) : la plus petite image, le plus de travail et de risque
- D) Autre : (Spécifiez)

**202. Hébergement des images (E3)**
> Mesuré : GitHub Releases n'a pas de CORS ; jsDelivr est limité à 20 Mo avec un `Range` incohérent ; GitHub Pages et `raw.githubusercontent.com` font CORS, 206 et ETag mais plafonnent à 100 Mio par fichier et 1 Go par site ; R2 n'a pas pu être mesuré (aucun bucket public), sa documentation promet CORS par bucket, `Range` et sortie gratuite. 800 Mio se téléchargent en 68,5 s à 100 Mbit/s par morceaux de 32 Mio.

- A) **R2 derrière un domaine de Kazo** (CORS, `Range`, `Expose-Headers: Content-Range, ETag, Content-Length`, `Cache-Control: immutable`), un fichier par morceau de 32 Mio ; GitHub Pages seulement pour les petites images de test de la CI (Alpine sans Wine, FreeDOS) ; à demander à Kazo : l'origine d'hébergement ; à remesurer sur le vrai R2 avant le jalon 0 ✨(Recommandé)
  - Pas de serveur à exploiter ; sortie gratuite ; une mesure réelle manque encore.
- B) Un serveur Kazo (le serveur de synchronisation ou un serveur statique dédié) avec les mêmes en-têtes : tout chez Kazo, mais de la bande passante et de l'exploitation en plus
- C) GitHub Pages seulement, avec une image découpée en 25 fichiers de 32 Mio par site (1 Go par site) : gratuit, mais une image par site et 100 Mio par fichier
- D) Autre : (Spécifiez)

**203. Mécanisme de téléchargement et de vérification (E3)**
> Mesuré : `SubtleCrypto.digest` fait 1,1 à 1,25 Go/s dans un worker, .NET 10 interprété sous Blazor 35 Mo/s (24 s pour 800 Mio en bloquant l'interface) ; `fetch` d'un seul tenant avec `integrity` prend 2,4 Gio de mémoire sans reprise ; les trois navigateurs ne gardent jamais en cache HTTP une réponse de 64 Mio ou plus (c'est pourquoi v86 redemandait ses fichiers à chaque boot dans E1) ; la reprise après coupure marche dans Chrome, Edge et Firefox avec un état par image dans l'OPFS.

- A) Un **Web Worker JS livré par la Feature** télécharge par `Range` ou par fichiers de 32 Mio, vérifie chaque morceau avec `SubtleCrypto.digest` contre un manifeste SHA-256 par morceau (manifeste lui-même vérifié par `fetch` + `integrity` depuis l'origine de l'app), écrit directement dans l'OPFS, garde un état de reprise par image dans l'OPFS, réessaie avec délai croissant ; `cache: 'no-store'`, l'OPFS est le seul cache ; **v86 lit l'image depuis l'OPFS**, jamais par `Range` réseau à la demande ; le C# ne fait que piloter et afficher la progression (126) ✨(Recommandé)
  - 30 fois plus rapide que le C# interprété ; demande à Kazo l'accord pour un worker JS dans l'app Os-x86 et pour `cache: 'no-store'`.
- B) Tout en C# (téléchargement et SHA-256) pour rester dans le modèle Kazo : 24 s de vérification par image en bloquant l'interface, ou AOT à mesurer (installation du workload refusée faute d'élévation)
- C) Autre : (Spécifiez)

**204. Le coût du premier lancement Wine dans une VM démarrée à froid (152 A)**
> Mesuré : le premier processus Wine après le boot prend 62 à 79 s, parce que v86 lit à la demande 95 Mo de DLL de la bouteille par 9p (392 requêtes) ; les lancements suivants prennent 12 à 32 s (Notepad 23 à 32 s). En V1, la VM démarre à froid (152 A : 49 s), donc chaque démarrage de VM paie ce premier lancement. L'état gabarit (VM déjà démarrée, premier `wine` fait) est évalué pour V2 (152).

- A) En V1, réduire ce coût sans état gabarit : v86 lit l'image depuis l'OPFS (203) et la recette d'image précharge les fichiers chauds de la bouteille (liste mesurée) ; objectif mesuré au jalon J3 : premier programme utilisable moins de 60 s après l'invite ; si l'objectif n'est pas atteint, la question de l'état gabarit (152) est reposée pour V1 ✨(Recommandé)
  - Garde 152 A ; donne un chiffre à viser et une porte de sortie explicite.
- B) Accepter 62 à 79 s pour le premier programme en V1 (affiché à l'usager), état gabarit en V2 comme prévu
- C) Reposer dès maintenant 152 : état gabarit en V1 (2 à 4 s de reprise, mais un état de 300 Mo à distribuer et lié à la version de v86)
- D) Autre : (Spécifiez)

---

On retient quoi pour 201 à 204? Réponds par numéro ; une ligne vide n'accepte rien.
