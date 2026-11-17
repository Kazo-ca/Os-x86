# Série 24 — code de l'ADR Kazo 0249, section « Fonctionnalités » des Paramètres (283 à 286)

Posée le 2026-11-17. Questions du chantier Kazo `feature/21-settings-features-section-code` (PR [Kazo-ca/Os#617](https://github.com/Kazo-ca/Os/pull/617), vers `feature/base`). Chaque recommandation est déjà dans le code, et les écarts avec l'ADR 0249 sont consignés dans l'ADR d'ajustement 0285. Une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 283. Que veut dire « activer » et « désactiver » une Feature dans les Paramètres ?

Contexte : l'ADR 0249 prévoit d'activer ou de désactiver une Feature depuis la section « Fonctionnalités ». Une Feature s'installe pour toute la machine (ADR 0260). La session a relié ce bouton à l'installation qui existe déjà, plutôt que de créer un nouvel état.

- A) ✨(Recommandé) Désactiver, c'est désinstaller la Feature pour tous les usagers (ADR 0048), et activer, c'est la réinstaller par la même ligne d'installation, reprise dans la page. Rien de nouveau à stocker.
- B) Un état « désactivée » à part : la Feature reste installée, mais ses points d'entrée sont arrêtés et cachés. Il faut alors un nouveau document système pour garder cet état.
- C) Autre : (Spécifiez)

## 284. Faut-il afficher l'état « redémarrage requis » de l'ADR 0249 §1 ?

Contexte : l'ADR 0249 prévoit un état « redémarrage requis » sur la fiche d'une Feature. Or une Feature arrive et se met à jour à chaud (ADR 0214 §1), sans redémarrage de l'OS.

- A) ✨(Recommandé) Ne pas l'afficher, puisque rien ne l'exige aujourd'hui.
- B) L'afficher quand une mise à jour attend un rechargement de la page (ADR 0239), une fois que le SDK expose cet état.
- C) Autre : (Spécifiez)

## 285. Où vit le contrat `IFeatureSettingsPages`, qui donne aux Paramètres les pages d'une Feature ?

Contexte : la règle d'analyse KOSAPP0031 empêche une app de dépendre des assemblages internes de l'OS. Les Paramètres sont eux-mêmes une app.

- A) ✨(Recommandé) Public, dans `Kazo.Os.AppSdk.Blazor`, et injecté dans les Paramètres. L'OS ne répond qu'à l'app Paramètres, donc une autre app ne peut pas s'en servir.
- B) Dans un assemblage interne à l'OS, avec une exception à KOSAPP0031 pour les Paramètres seulement.
- C) Autre : (Spécifiez)

## 286. Comment tester de bout en bout la section « Fonctionnalités » ?

Contexte : Kazo n'a pas encore de vraie Feature installable. Os-x86 sera la première, avec son chantier 8.

- A) ✨(Recommandé) Tests unitaires et de rendu dans cette PR. Le parcours complet sera testé avec la première Feature réelle, Os-x86.
- B) Ajouter dès maintenant une Feature d'essai dans Kazo et un test Playwright du parcours complet.
- C) Autre : (Spécifiez)
