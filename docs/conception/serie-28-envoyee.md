# Série 28 — Prochain chantier après Kazo 3.5.2 (296)

Posée le 2026-11-18, après la publication de Kazo 3.5.2 et l'ouverture du chantier 8c (PR #20). Rien n'est accepté par défaut.

Ce qui reste du jalon 0 se partage en deux. Dans Kazo, le code des ADR 0254, 0251, 0252, 0253 et 0255, un chantier par ADR vers `feature/base`, puis une version publiée. Les chantiers 7, 10 (suite), 11 (suite) et 12 (suite) d'Os-x86 attendent cette version. Seul le chantier 13, BoxedWine minimal, dans le dépôt Kazo-ca/Os-App-BoxedWine, peut avancer sans elle : la Feature `ca.kazo.x86` et la résolution des dépendances (0248) sont déjà publiées. Sa démonstration par le terminal (`./setup.exe`) attend tout de même la route du shell (0251).

## 296. Quel chantier lancer maintenant ?

- A) ✨(Recommandé) Kazo 0254 (événements par usager) dans la session Kazo, et le chantier 13 BoxedWine dans une nouvelle session, en parallèle : dépôts différents, aucun fichier commun.
- B) Kazo 0254 seulement.
- C) Chantier 13 seulement.
- D) Autre : (Spécifiez)

## Réponses

Réponse du 2026-11-18. Tes mots : « 296. A ».

- 296 : **A**. Kazo 0254 part dans la session Kazo (PR vers `feature/base`) ; le chantier 13 part dans une nouvelle session, sur une branche `j0/base` créée dans Kazo-ca/Os-App-BoxedWine.
