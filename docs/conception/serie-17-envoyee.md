# Série 17 — questions du code de l'ADR 0244 (259 à 262)

Posée le 2026-11-12. Elle vient du chantier Kazo `feature/15-app-libraries-code` (PR [Kazo-ca/Os#450](https://github.com/Kazo-ca/Os/pull/450) vers `feature/base`), qui met en œuvre l'ADR 0244 (bibliothèques d'app et versions partagées). Chaque recommandation est déjà dans le code de la PR ; une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 259. Comment l'auteur marque-t-il sa bibliothèque comme partagée ?

Contexte : l'ADR 0244 §2 parle d'un `buildTransitive` qui pose `KazoOsSharedLibrary` sur la référence. Une référence par DLL n'a pas de `buildTransitive`, et un élément MSBuild côté app laisserait une app marquer une bibliothèque qui ne l'est pas. Ta réponse à 225 : c'est l'auteur de la bibliothèque qui marque, par MSBuild, et l'app n'a rien à déclarer.

- A) ✨(Recommandé) La propriété `KazoOsSharedLibrary` du projet de la bibliothèque écrit la marque dans l'assemblage ; elle suit la bibliothèque par projet, par DLL ou par NuGet. Une ADR d'ajustement corrige la parenthèse du §2.
- B) `buildTransitive` du paquet NuGet seulement : les références par projet et par DLL ne sont pas couvertes.
- C) Les deux : la marque dans l'assemblage, plus un élément `buildTransitive` accepté ; l'app peut alors marquer.
- D) Autre : (Spécifiez)

## 260. Deux majeures d'une bibliothèque partagée, sans Feature : ensemble dans la même page ?

Contexte : l'ADR §2 dit « chacune chargée pour ses apps, séparément ». Or toutes les apps installées partagent le contexte de chargement par défaut, qui ne tient qu'un assemblage par nom simple.

- A) ✨(Recommandé) Les deux s'installent ; une seule majeure par page. L'app de l'autre majeure est servie par la copie chargée, une ligne de journal le dit, et « Redémarrer pour terminer » est proposé.
- B) Un contexte de chargement à part pour les apps de l'autre majeure : vraie coexistence, mais le chargement unifié de l'ADR 0182 est à revoir et à prouver dans l'OS rogné (chantier à part).
- C) Refuser la seconde installation (contredit ta réponse sur les deux majeures installables).
- D) Autre : (Spécifiez)

## 261. Deux apps avec une bibliothèque non partagée de même nom et de versions différentes

Contexte : deux apps embarquent chacune sa version d'un même paquet NuGet ordinaire, non marqué partagé.

- A) ✨(Recommandé) La première chargée sert les deux ; la seconde est notée dans `/var/log/syslog`.
- B) Refuser à l'installation la seconde app.
- C) Un contexte de chargement par app (option écartée par l'ADR).
- D) Autre : (Spécifiez)

## 262. Qui est « fournisseur » d'une bibliothèque partagée avant le code de l'ADR 0245 ?

Contexte : la règle « la copie d'une Feature passe d'abord » demande de savoir quelle Feature fournit la bibliothèque. Le registre d'interfaces de l'ADR 0245 n'est pas encore codé.

- A) ✨(Recommandé) Toute Feature installée qui embarque la bibliothèque ; l'ADR 0245 restreindra à celles qui déclarent fournir une de ses interfaces.
- B) Aucune avant l'ADR 0245 : seule la règle « plus récente compatible » s'applique.
- C) Autre : (Spécifiez)

## À savoir, sans question

- Le commit `dcc249c0` (256 B : `kind` dans `catalog.json`, Store sans « Installer pour moi » pour une Feature) n'est **pas** dans `feature/base` : #449 a été fusionnée à `7dc1340e`, avant son arrivée sur la branche. Il est repris dans #450 (commit de fusion `17b5a1f9`). Os-AppMarketPlace#20, déjà fusionnée, documente ce champ.
- À faire dans Os-x86 quand une version de Kazo contiendra ce chantier : `Os-x86.Interface` pose la marque partagée dans son projet.
- Hors de la PR, non faits : retrait du contournement de #419 dans Os-App-IRC, régénération d'Os-AppTemplate.
