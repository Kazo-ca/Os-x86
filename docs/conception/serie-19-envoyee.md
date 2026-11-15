# Série 19 — code de l'ADR 0246 (267 à 269)

Posée le 2026-11-15. Les questions viennent du chantier Kazo `feature/17-provider-declared-permissions-code` (PR [Kazo-ca/Os#570](https://github.com/Kazo-ca/Os/pull/570) vers `feature/base`), qui met en œuvre l'ADR 0246 (permissions déclarées par un fournisseur). Chaque recommandation est déjà dans le code de la PR ; une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 267. Langue du titre et de la phrase de risque d'une permission de Feature

Contexte : l'ADR 0246 §1 parle des « ressources localisées de la Feature (ADR 0222) ». Afficher la fenêtre d'approbation ne devrait pas obliger à charger le code de la Feature.

- A) ✨(Recommandé) Le texte vient du manifeste, dans une langue ; la traduction viendra par les fichiers `localization/` du manifeste de la Feature, comme ses autres textes, sans charger son code pour afficher une fenêtre.
- B) Les ressources satellites de la Feature (ADR 0222), lues par l'OS à l'affichage : l'assemblage de la Feature est chargé pour une fenêtre d'approbation.
- C) Autre : (Spécifiez)

## 268. Le Store montre-t-il les permissions des Features ?

Contexte : le Store lit les textes de son `catalog.json`, qui ne porte pas ceux des Features.

- A) ✨(Recommandé) Plus tard, avec la résolution des dépendances du Store (ADR 0248), qui saura de quelle Feature une app dépend.
- B) Dans ce chantier : ajouter les permissions de chaque Feature au `catalog.json` (dépôt Os-AppMarketPlace).
- C) Autre : (Spécifiez)

## 269. `kazopkg info`, que l'ADR cite, n'affiche aucune capacité aujourd'hui

- A) ✨(Recommandé) Le rapport de `kazopkg install` marque les capacités (« (!) » pour les dangereuses, celles d'une Feature comprises) ; `kazopkg info` reste tel quel.
- B) Ajouter les capacités et leur Feature d'origine à `kazopkg info`, par la lecture du paquet publié.
- C) Autre : (Spécifiez)
