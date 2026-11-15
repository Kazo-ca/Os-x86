# Chantier 5 (j0/5-agent) : note de passation

PR : https://github.com/Kazo-ca/Os-x86/pull/7 vers `j0/base`, ouverte le 2026-11-12, CI verte
(build, guest, agent, alpine-test, freedos, linux-base, image-tool-tests), `CLEAN`, non fusionnée.
Documentation de référence : `docs/fr/agent.md` (miroir `docs/en/agent.md`).

## Ce dont le chantier 6 a besoin

- Client : `new AgentClient(EngineAgentChannel.For(engine))`, puis `ConnectAsync` après
  `StartAsync`. `not-ready` = image sans agent (212 A). `ConnectAsync` peut être rappelé après un échec.
- `LaunchAsync` rend un `LaunchResult` (`Id` = `<préfixe>/<seq>`, `Exited`) : à enregistrer comme
  processus Kazo enfant, à retirer à `Exited` (207 A). `KillAsync(id)` sur un lancement déjà fini
  lève `AgentException` de code `unknown-launch`.
- `x86 shell` = `OpenConsoleAsync` : `ReadOutputAsync`, `WriteAsync`, `SendInterruptAsync`,
  `ResizeAsync`, `CloseAsync(grace, keep)`, `Exited`.
- Un événement `ready` après la connexion = agent redémarré : les lancements et consoles suivis
  échouent avec `closed`.
- L'usager invité du préfixe `p` est `osx86-p` et doit exister dans `/etc/passwd` de l'image ;
  l'agent n'a pas d'opération pour le créer (chantiers 9 et 10). L'image Alpine de test a
  `osx86-test` et `osx86-other`.
- Ports : `hvc0` protocole, `hvc1` consoles multiplexées, `hvc2` et `hvc3` libres.

## Pièges rencontrés

- Un programme lancé en tâche de fond par un init non interactif hérite de SIGINT et SIGQUIT
  ignorés, et les transmet à ses enfants : l'agent remet tous les signaux par défaut avant
  `exec`. À garder en tête pour tout autre démon lancé par `init.sh`.
- La racine de l'initramfs Alpine est en 0700 : un usager ne peut rien atteindre sans `chmod 755 /`.
- `<math.h>` déclare `jn()` (Bessel) : collision avec le type `jn` de l'agent.
- GateGuard prend un fichier nommé `agent.md` pour un fichier d'instructions de harnais : il
  suffit d'énoncer que c'est la documentation de l'agent invité.
- `mcp__ccd_pr__set_monitor` est refusé par le mode auto : surveillance par `gh pr checks --watch`.
- `src/OsX86.Feature/manifest.json` apparaît modifié après un build : aucune différence de
  contenu, seulement les fins de ligne régénérées. Ne pas le commiter.

## Non vérifié

X et `list-windows` (squelette), Wine, démarrage réel de linux-base (pas de `hda` dans le worker :
l'agent y est installé et vérifié dans l'export, jamais exécuté), les 22 contrôles R134.5 à
l'exécution (`verify-console.sh`), Firefox et Safari.

## Points de revue laissés ouverts (bas)

- `kill` par préfixe peut retenir la boucle de l'agent jusqu'à 0,5 s ; un `exec` qui traîne jusqu'à 5 s.
- Les journaux de lancement optionnels (`log: true`) dans `/run/osx86` ne sont jamais supprimés.
- Sans `cgroup.kill` (noyau antérieur à 5.14) un processus qui ignore SIGTERM survivrait ; les
  noyaux des images sont en 6.12 et 6.18.
- `LaunchResult` est un record qui porte une `Task` : son égalité n'a pas de sens.
