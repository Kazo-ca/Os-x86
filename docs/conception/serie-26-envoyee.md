# Série 26 — chantier 8b, le service de la Feature ne démarre pas à la connexion (293)

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
