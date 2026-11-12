# Série 16 — questions du code de l'ADR 0243 (256 à 258), posées le 2026-11-11

Trois questions que le chantier `feature/14-feature-app-type-code` de Kazo-ca/Os ([#449](https://github.com/Kazo-ca/Os/pull/449), code de l'ADR 0243) pose au mainteneur. La recommandation de chacune est appliquée dans le code en attendant ; une réponse différente est un correctif sur la même PR.

**256. « Installer pour moi » dans le Store pour une Feature.** Le `catalog.json` du Store ne dit pas le genre d'un paquet : le bouton reste visible et le clic est refusé avec le message de l'ADR 0243.
- A) Garder ce comportement et ajouter `kind: feature` au catalogue avec l'ADR 0248 (dépendances et catalogue) ✨(Recommandé)
- B) Ajouter `kind` au `catalog.json` dès maintenant (changement du dépôt Os-AppMarketPlace) et cacher le bouton
- C) Autre : (Spécifiez)

**257. Lignes `allow` machine au retrait d'une Feature.**
- A) Les effacer ; une réinstallation redemande le consentement ✨(Recommandé)
- B) Les garder, comme les données (ADR 0048 §2)
- C) Autre : (Spécifiez)

**258. Enfant de genre `Web` dans une Feature.**
- A) Refusé (`manifest.feature.child.kind`) : un site n'est pas un point d'entrée d'une fonctionnalité ✨(Recommandé)
- B) Permis
- C) Autre : (Spécifiez)
