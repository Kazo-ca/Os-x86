# Série 23 — suite de 277 dans le code de l'ADR 0247 (281 et 282)

Posée le 2026-11-17. Questions de suivi du chantier Kazo `feature/20-feature-declared-roles-code` (PR [Kazo-ca/Os#604](https://github.com/Kazo-ca/Os/pull/604)), après l'application de ta réponse à 277 : objet `KazoUser` avec `IsInRole("admin")` et `IsInRole("<feature>:<rôle>")`, administrateur = tous les rôles (233 B), ADR d'ajustement 0283. Chaque recommandation est déjà dans le code ; une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 281. Quel nom de la Feature sert de préfixe à ses rôles ?

Contexte : tu as écrit `x86:manager`, alors que l'identifiant d'Os-x86 est `ca.kazo.x86`. Il faut un nom court, stable et unique parmi les Features installées.

- A) ✨(Recommandé) Le `CapabilityPrefix` de la Feature (ADR 0246) : déjà court et validé, refusé à l'installation s'il est déjà pris, donc unique. Pour Os-x86 il vaut `x86`, comme dans ses permissions `x86.vm.use` ; son rôle s'écrit `x86:vm-manager`.
- B) Un nouveau champ `ShortName` dans le manifeste.
- C) L'identifiant complet de la Feature : `ca.kazo.x86:vm-manager`.
- D) Autre : (Spécifiez)

## 282. Une sous-commande ouverte à tous peut-elle agir au nom d'un rôle ?

Contexte : la relecture avait fait refuser `RoleHolder` (la portée qui agit au nom d'un rôle) hors d'un point d'entrée marqué `RequiresRole`, ce qui bloquait `x86 admin`. La session l'a assoupli.

- A) ✨(Recommandé) Accepté si l'usager détient au moins un rôle de la Feature ; la Feature vérifie ensuite le rôle exact par `IsInRole`.
- B) Refusé : `x86 admin` devient une commande à part avec `RequiresRole`.
- C) Un paramètre `rôle` dans `AuthenticateAsync`, vérifié par l'OS à l'authentification.
- D) Autre : (Spécifiez)

## Réponses, le 2026-11-17

Tes mots : « Oui, A et A ».

| # | Réponse | Effet |
|---|---|---|
| 281 | **A** | Préfixe des rôles = `CapabilityPrefix` de la Feature ; pour Os-x86, `x86:vm-manager`. Déjà codé dans #604. |
| 282 | **A** | Une sous-commande ouverte à tous agit au nom d'un rôle si l'usager détient au moins un rôle de la Feature ; rôle exact vérifié par `IsInRole`. Déjà codé. |

