# Série 18 — code de l'ADR 0262 et épinglage de Kazo (263 à 266)

Posée le 2026-11-15. Les questions 263 à 265 viennent du chantier Kazo `feature/16-feature-service-registry-code` (PR [Kazo-ca/Os#541](https://github.com/Kazo-ca/Os/pull/541), déjà fusionnée par toi, recommandations codées). La question 266 vient de l'état de Kazo : `feature/base` est entièrement dans `main`, et le code des ADR 0260 à 0262 est publié dans `v3.4.0-preview.2` à `.4`. Rien n'est accepté par défaut.

Rappel des numéros : en fusionnant #450, la PR Kazo #478 a renuméroté les trois premières ADR de la série : 0243 devient **0260** (type d'app Feature), 0244 devient **0261** (bibliothèques d'app), 0245 devient **0262** (registre de services). Les ADR 0246 à 0255 gardent leur numéro ; 0256 est l'ajustement de 0261 (série 17).

## 263. Sous quel compte tourne le processus des services d'une Feature ?

Contexte : une seule instance d'un service sert toutes les apps de la page, donc tous les usagers d'une session partagée.

- A) ✨(Recommandé) `kazoos-svc`, en portée système, comme un démon : la Feature est installée pour toute la machine et vérifie l'usager appelant à chaque opération (`Caller`, ADR 0246).
- B) L'usager de la première app qui le demande : plus lisible dans `ps`, mais l'instance garderait l'identité de cet usager pour les autres.
- C) Un processus et une instance par usager de session : isolé, mais plus « une fois par page ».
- D) Autre : (Spécifiez)

## 264. Feature désactivée ou retirée pendant que la page tourne : que devient le processus de ses services ?

- A) ✨(Recommandé) L'instance est abandonnée (libérée si elle est `IDisposable`), `Changed` est levé, le processus reste jusqu'à l'arrêt de la page, comme le code d'une app chargée (ADR 0214 §1).
- B) Le processus est arrêté tout de suite, au risque de couper une opération en cours d'une app qui tenait encore l'instance.
- C) Autre : (Spécifiez)

## 265. Fixture `sdk-compat` et version mineure du SDK citées par l'ADR 0262

- A) ✨(Recommandé) À la publication : les paquets de la fixture sont figés par version de SDK publiée, et la version change par la PR de montée de version.
- B) Dans un petit chantier à part, maintenant.
- C) Autre : (Spécifiez)

## 266. Sur quelle version de Kazo épingler Os-x86 ?

Contexte : Os-x86 est épinglé sur `v3.2.1` (220 A : « 3.3.0 à sa publication »). Kazo est maintenant en `v3.3.4` stable et `v3.4.0-preview.4`. Le type Feature, les bibliothèques partagées et `GetServiceAsync<T>()` n'existent que dans les 3.4.0 préliminaires. Le chantier 8 (passage au vrai type Feature) en a besoin, ainsi que le code des ADR 0246, 0248 et 0250 à venir.

- A) ✨(Recommandé) Épingler maintenant sur `v3.4.0-preview.4` par un petit chantier à part (sous-module, build, tests), puis suivre chaque préliminaire qui apporte une ADR de la série, et passer à `v3.4.0` à sa publication.
- B) Épingler sur `v3.3.4` stable maintenant, et attendre `v3.4.0` stable pour le chantier 8.
- C) Rester sur `v3.2.1` jusqu'à `v3.4.0` stable.
- D) Autre : (Spécifiez)

## Réponses, le 2026-11-15

Tes mots : « Oui, A partout ».

| # | Réponse | Effet |
|---|---|---|
| 263 | **A** | Processus des services d'une Feature sous `kazoos-svc`, en portée système ; la Feature vérifie l'usager appelant. Déjà codé dans #541. |
| 264 | **A** | Instance abandonnée, `Changed` levé, processus gardé jusqu'à l'arrêt de la page. Déjà codé. |
| 265 | **A** | Fixture `sdk-compat` et version mineure du SDK à la publication. |
| 266 | **A** | Os-x86 épinglé sur `v3.4.0-preview.4` par le petit chantier `j0/1c-kazo-pin`, lancé le même jour ; suivi de chaque préliminaire qui apporte une ADR de la série ; `v3.4.0` à sa publication. Remplace la seconde moitié de 220 A. |

