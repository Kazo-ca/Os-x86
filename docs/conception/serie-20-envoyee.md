# Série 20 — code de l'ADR 0248 (270 à 272)

Posée le 2026-11-16. Les questions viennent du chantier Kazo `feature/18-dependency-resolution-code` (PR [Kazo-ca/Os#588](https://github.com/Kazo-ca/Os/pull/588) vers `feature/base`), qui met en œuvre l'ADR 0248 (résolution des dépendances par le Store et `kazopkg`). Le cas visé : BoxedWine dépend d'Os-x86. Chaque recommandation est déjà dans le code de la PR ; une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 270. Télécharger avant le consentement de l'administrateur ?

Contexte : pour installer une app et la Feature dont elle dépend, le plan doit être montré à un administrateur.

- A) ✨(Recommandé) Télécharger et vérifier d'abord (signature, API, plages de versions), avant de déranger l'administrateur ; rien n'est écrit avant son accord.
- B) Construire le plan depuis le `catalog.json` seul, sans télécharger, puis télécharger après l'accord : l'administrateur peut approuver un paquet refusé ensuite, et une source GitHub sans catalogue ne peut pas faire de plan.
- C) Autre : (Spécifiez)

## 271. Plan sans administrateur dans le Store

Contexte : un usager installe une app dont les dépendances sont personnelles et viennent d'une source de confiance ; aucune Feature n'est en jeu.

- A) ✨(Recommandé) Pas de fenêtre ; la fiche annonce les paquets « à installer avec cette app » et le message final les nomme.
- B) Toujours montrer une fenêtre de validation, sans authentification, dès que le plan a plus d'un paquet.
- C) Autre : (Spécifiez)

## 272. Ancien catalogue sans `dependencies`

Contexte : l'ADR 0248 §5 veut que la fiche du Store montre les dépendances ; un catalogue publié avant ce chantier ne les porte pas.

- A) ✨(Recommandé) La fiche n'affiche pas de dépendances ; l'installation les lit dans le paquet et montre le plan.
- B) Le Store télécharge le manifeste pour remplir la fiche : un téléchargement par fiche ouverte.
- C) Autre : (Spécifiez)
