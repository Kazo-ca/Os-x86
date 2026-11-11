> [English version](README.md)

# Os-x86

Os-x86 est la fonctionnalité d'émulation x86 de [Kazo OS](https://github.com/Kazo-ca/Os) : elle fait tourner des machines virtuelles x86 (Linux, FreeDOS, et des programmes Windows par Wine) dans le navigateur, avec [v86](https://github.com/copy/v86), et permet aux apps de Kazo OS d'y lancer des programmes par le paquet de contrats `Os-x86.Interface`. L'app Windows destinée à l'usager, [BoxedWine](https://github.com/Kazo-ca/Os-App-BoxedWine), est un dépôt à part qui dépend de celui-ci.

La conception a été débattue et consignée avant tout code : [`docs/conception/`](docs/conception/) (le plan approuvé est [`plan-2026-11-11.md`](docs/conception/plan-2026-11-11.md), le registre des décisions [`registre-reconcilie.md`](docs/conception/registre-reconcilie.md)). Les prototypes de mesure jetables sont dans [`essais/`](essais/), leurs rapports dans [`docs/essais/`](docs/essais/).

## Disposition

| Chemin | Contenu |
| --- | --- |
| `src/OsX86.Interface/` | le paquet de contrats `Os-x86.Interface` (`Ix86` et ses types), embarqué par les apps clientes |
| `src/OsX86/` | x86-Lib : l'implémentation (moteur, stockage, réseau, partage, agent, identité, VM) |
| `src/OsX86.Feature/` | le paquet Kazo OS : la commande `x86`, la fenêtre de permission, la page des règles, les pages admin |
| `src/OsX86.Os/` | un hôte Kazo OS avec la fonctionnalité, pour le développement et les démonstrations |
| `tests/` | tests xUnit |
| `docs/fr/`, `docs/en/` | documentation produit (fonctionnalités, changelog), français d'abord |
| `external/KazoOs` | Kazo OS en sous-module, épinglé sur un tag de version |

## Compiler

Prérequis : le SDK .NET 10 (`global.json`) et le sous-module (`git submodule update --init`).

```bash
dotnet build OsX86.sln -c Release      # paquets Kazo OS de nuget.org, à la version du sous-module
dotnet test OsX86.sln -c Release
dotnet run --project src/OsX86.Os      # Debug : Kazo OS depuis les sources du sous-module
```

Le profil `Debug` compile Kazo OS depuis le sous-module (premier build long) ; `Release` prend les paquets publiés, ce que la CI compile. Voir [`app-repository.md`](https://github.com/Kazo-ca/Os/blob/main/doc/fr/wasm/framework/app-repository.md) de Kazo OS.

## État

Le jalon 0 (programmes console) est en cours ; voir [`docs/fr/features.md`](docs/fr/features.md) et [`docs/conception/jalon-0-execution.md`](docs/conception/jalon-0-execution.md).

## Licence

MIT. v86 est sous BSD 2 clauses ; les images invitées portent leurs propres licences et leurs sources, publiées à côté d'elles.
