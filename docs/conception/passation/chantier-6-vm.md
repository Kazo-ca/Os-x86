# Chantier 6 (j0/6-vm-and-command) : note de passation

PR : https://github.com/Kazo-ca/Os-x86/pull/9 vers `j0/base`, fusionnée le 2026-11-15 (`adfafa8`).
Documentation de référence : `docs/fr/vm.md` (miroir `docs/en/vm.md`).

## Ce qu'il faut savoir pour la suite

- `VmManager` (un par hôte) : registre, budget, instances rangées par (usager, VM). `VmInstance` :
  `AcquireAsync` (application), `HoldAsync` (démarrage à la main), `StopAsync(force)`,
  `OpenConsoleAsync`, `LaunchAsync`, `PingAgentAsync`.
- Fabriques injectables : `IEngineFactory` (la commande du squelette a `UnavailableEngineFactory`,
  code 6), `IBootImageSource` (`StaticBootImageSource` aujourd'hui).
- **`linux-base` par défaut (après 6a)** : écrire un `IBootImageSource` sur `ImageStore.Locate` qui
  rend aussi le disque, étendre `BootImage` au disque, et changer `X86Command.DefaultProfile` en
  `linux-base`. Le profil livré `linux-base` n'a pas de préfixe par défaut : `x86 shell` y demande
  `--prefix` tant que les usagers invités des préfixes n'existent pas (chantiers 9 et 10).
- **Chantier 7** : écrire `VmManagerOptions.MemoryLimitMiB` et `StopGrace` (lus aujourd'hui, jamais
  écrits), gérer les VM des autres usagers (aujourd'hui `vm-not-found`), la fenêtre de choix de
  165 A à partir de `VmMemoryBudgetExceededException`.
- **Chantier 8** : les services de la commande viennent de `X86Runtime.Services` (statique). La
  Feature appellera `X86Runtime.Configure` avec sa fabrique de moteurs (elle a l'`IJSRuntime`), lira
  `navigator.deviceMemory` pour `IHostMemory`, enregistrera `new X86Service(manager, userId)` par
  usager, et fera de chaque VM un processus Kazo et de chaque enfant (`VmInstance.Children`) un
  processus enfant (207 A).
- **Chantier 10** : `x86 run` et `./hello.exe` passent par `VmInstance.LaunchAsync`, qui suit le
  lancement comme enfant et tient la VM jusqu'à son `exit`.
- Identifiant d'usager : `IAppExecutionContext.UserId` = 32 chiffres hexadécimaux du `LocalUserId`.

## Pièges rencontrés

- **Écho du terminal de l'invité** : avant que l'agent n'ouvre `hvc0` en mode brut, le premier
  `ping` revient tel quel et `AgentClient.ConnectAsync` échoue en `protocol`. `VmInstance` réessaie
  (`ConnectThroughEchoAsync`). Le test du chantier 5 ne le voyait pas parce qu'il se connectait après
  l'invite série. À corriger peut-être dans le client lui-même (ignorer une ligne sans `ok` égale à
  la requête), hors de ce chantier.
- `TextReader.ReadLineAsync(token)` de l'entrée standard peut ignorer l'annulation : le relais de
  `x86 shell` ne l'attend plus après la fin du shell.

## Non vérifié

La commande dans Kazo lui-même (fichiers privés réels, Ctrl+C du terminal de Kazo), Firefox,
Safari, linux-base.
