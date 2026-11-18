# Série 26 — chantier 8b, le service de la Feature ne démarre pas à la connexion (293), et unkernel (294)

Posée le 2026-11-18. Question du chantier 8b d'Os-x86 (PR Kazo-ca/Os-x86#19 vers `j0/base`). Rien n'est accepté par défaut.

Ce que révèle Kazo 3.5.1, sans le contournement de l'issue #621 :

- Kazo ne démarre à la connexion que les services dont le code est déjà chargé.
- Le code d'une Feature n'est chargé qu'au premier lancement d'un de ses enfants.
- Le service `ca.kazo.x86.host` (288 A) reste donc arrêté (`inactive (dead)`). Au chantier 8, c'est le contournement qui chargeait ce code avant la connexion.
- La commande `x86` installe quand même le moteur. Mais une app qui appelle `Ix86` avant toute commande `x86` reçoit `engine-unavailable`.
- Le défaut touche aussi une Feature installée par kazopkg ou par le Store, pas seulement l'hôte de dev.

## 293. Que faire pour que `ca.kazo.x86.host` démarre à la connexion ?

- A) ✨(Recommandé) C'est un défaut de Kazo (ADR 0262) : ouvrir une issue Kazo, puis une PR de correctif vers `main` et sa copie vers `feature/base`, comme pour #621 (version 3.5.2). D'ici là, la PR #19 est fusionnée telle quelle, avec la limite documentée.
- B) Garder dans l'hôte de dev d'Os-x86 un préchargement limité au service, sans rien changer chez Kazo. Une Feature installée par le Store garde le défaut.
- C) Accepter la limite : `Ix86` reste indisponible jusqu'à la première commande `x86`, et on documente. Pas de travail chez Kazo.
- D) Autre : (Spécifiez)

## 294. Que faire d'unkernel (pithings/unkernel) ?

Ajoutée le 2026-11-18, après ton évaluation demandée. unkernel est un vrai noyau Linux 7.3 compilé en wasm avec une MMU réelle ; les programmes sont des binaires riscv64 exécutés par un JIT (kwemu). Alpine démarre en 0,3 s, contre 16 à 18 s pour notre premier `x86 shell`. Il n'exécute pas de x86 : leur démo FreeDOS dans qemu-system-i386 met environ 70 s jusqu'à l'invite (3,2 s sur v86), et ni `wine` ni `box64` n'existent pour riscv64 chez Alpine. JSPI obligatoire ; noyau sur le fil principal par défaut, mode worker expérimental ; un noyau par fil ; réseau TCP et DNS seulement. Dépôt créé le 2026-11-12, un seul contributeur, versions nightly ; noyau GPL-2.0-only, SDK MIT.

- A) ✨(Recommandé) Veille, rien de changé au jalon 0 ; un essai « unkernel en worker dans Kazo » (démarrage, mémoire, disque OPFS, fichiers Kazo, relais) est ajouté aux études prévues, à lancer quand leur mode worker n'est plus expérimental.
- B) Essai maintenant, en parallèle du jalon 0, dans l'hôte de dev.
- C) Planifier dès maintenant une Feature Linux distincte basée sur unkernel.
- D) L'écarter.
- E) Autre : (Spécifiez)

## Réponses

Réponses du 2026-11-18. Tes mots : « 294. A » et « 293. A ».

- 293 : **A**. Défaut de Kazo (ADR 0262) : issue Kazo, PR de correctif vers `main` avec la version 3.5.2, et sa copie vers `feature/base`, comme pour #621. La PR Kazo-ca/Os-x86#19 est fusionnée telle quelle, avec la limite documentée ; Os-x86 s'épinglera ensuite sur 3.5.2.
- 294 : **A**. Veille sur unkernel ; essai E18 ajouté aux études prévues, à lancer quand leur mode worker n'est plus expérimental. Rien ne change au jalon 0.
