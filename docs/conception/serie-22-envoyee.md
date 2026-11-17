# Série 22 — code de l'ADR 0247 (277 à 280)

Posée le 2026-11-17. Les questions viennent du chantier Kazo `feature/20-feature-declared-roles-code` (PR [Kazo-ca/Os#604](https://github.com/Kazo-ca/Os/pull/604) vers `feature/base`), qui met en œuvre l'ADR 0247 (rôles déclarés par une Feature, avec 233 B : l'administrateur détient tous ces rôles). La session a codé A partout ; pour 277, ma recommandation diffère, à cause du plan d'Os-x86 : une réponse autre que A demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 277. Exiger un rôle pour une sous-commande d'une commande ouverte à tous

Contexte : dans Kazo, un rôle s'exige par manifeste (`RequiresRole` sur un point d'entrée). Os-x86 a une seule commande `x86`, ouverte à tous, et le plan prévoit `sudo x86 admin` comme sous-commande pour le rôle « gestionnaire des VM » (140 B, 156, 160 A). Ce rôle sert justement à donner l'administration des VM à un usager qui n'est pas administrateur de Kazo.

- A) Refus : la Feature déclare un point d'entrée séparé avec `RequiresRole` (par exemple une commande `x86-admin`), ou exige `Administrator`. Codé par la session. Os-x86 devrait renommer `x86 admin` en commande à part.
- B) ✨(Recommandé) Ajouter un paramètre `role` à `AuthenticateAsync` : la sous-commande nomme le rôle qu'elle exige, et l'OS vérifie le rôle et authentifie l'usager. `x86 admin` reste une sous-commande ; la vérification reste faite par l'OS, à chaque opération.
- C) Accepter si l'usager détient au moins un rôle de la Feature, quel qu'il soit.
- D) Autre : (Spécifiez)

## 278. Synchronisation de `roles.config`

- A) ✨(Recommandé) Pas de synchronisation dans cette PR ; une issue `TODO` pour une liste des documents système synchronisés.
- B) La construire dans cette PR.
- C) Autre : (Spécifiez)

## 279. Forme de l'attribution des rôles dans Paramètres ▸ Utilisateurs

Contexte : l'ADR dit « colonne Rôles par usager ».

- A) ✨(Recommandé) Un panneau « Rôles des Features » sous la liste des comptes : un tableau comptes × rôles avec des cases à cocher.
- B) Une colonne de badges dans la liste des comptes, avec une boîte d'édition par compte.
- C) Autre : (Spécifiez)

## 280. `kazorole list` sans `sudo`

- A) ✨(Recommandé) Tout usager liste les rôles et leurs détenteurs, comme `getent group`.
- B) `sudo` exigé pour voir les détenteurs.
- C) Autre : (Spécifiez)

## Réponses, le 2026-11-17

Tes mots pour 277 : « Je crois que pour simplifier le développement, la feature doit pouvoir interroger l'OS pour savoir si le user a les permissions... On va devoir avoir un objet `KazoUser` et pouvoir l'interroger comme `_kazoUser.IsInRole("admin")` ou `_kazoUser.IsInRole("x86:manager")` (le préfixe doit rester le nom de la feature qui déclare les rôles). » Pour 278 à 280 : « A ».

| # | Réponse | Effet |
|---|---|---|
| 277 | **D, ta réponse** | Objet `KazoUser` interrogeable par `IsInRole` : rôles de l'OS par leur nom (`admin`), rôles d'une Feature sous la forme `<nom de la Feature>:<rôle>` (`x86:manager`), le préfixe étant le nom de la Feature qui déclare le rôle. Avec 233 B, un administrateur détient tous ces rôles. `x86 admin` reste une sous-commande qui vérifie le rôle par `IsInRole` ; l'authentification par opération (sudo) reste à part. Correctif et ADR d'ajustement demandés dans #604 ; le champ du manifeste qui sert de préfixe est à fixer par la session. |
| 278 | **A** | Pas de synchronisation de `roles.config` dans cette PR ; une issue pour la liste des documents système synchronisés. |
| 279 | **A** | Panneau « Rôles des Features » : tableau comptes × rôles avec cases à cocher. |
| 280 | **A** | `kazorole list` sans `sudo`. |

Appliqué le 2026-11-17 dans #604 (commits `3c0c79e3`, `4f80b2b5`) : `KazoUser.Current.IsInRole`, rôle `admin` de l'OS, rôles `<CapabilityPrefix>:<rôle>` (pour Os-x86 : `x86:vm-manager`), ADR d'ajustement 0283. L'issue de suivi de 278 est Kazo-ca/Os#607, ouverte par moi. Questions de suivi : [série 23](serie-23-envoyee.md).
