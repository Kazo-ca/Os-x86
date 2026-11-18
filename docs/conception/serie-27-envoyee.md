# Série 27 — Kazo 3.5.2, portée du démarrage des services à la connexion (295)

Posée le 2026-11-18. Question de la session Kazo, qui écrit le correctif de l'issue Kazo-ca/Os#625 (293 A). Rien n'est accepté par défaut.

Le défaut n'est pas propre aux Features. `AppLifecycleOrchestrator.AutomaticServiceCandidates` ne regarde que les descripteurs déjà découverts ; dans le navigateur, toutes les apps sont chargées à la demande, donc aucun service automatique ne démarre à la connexion avant le premier lancement de son app. Cela touche aussi `kazowebd` (`org.kazoos.kazowebd`, `AutoStart=true`, ADR 0077). Le correctif écrit (branche locale `fix/625-feature-service-autostart`) prend les candidats dans le catalogue, lit le mode de démarrage avant tout chargement, puis charge seulement le code des services à démarrer, avec leur Feature (#621).

## 295. Jusqu'où va le correctif 3.5.2 ?

- A) ✨(Recommandé) Tout service automatique activé du catalogue, Feature ou non. `kazowebd` démarre à chaque connexion, comme son manifeste le demande (une DLL chargée et un processus de plus à la connexion). Corrige aussi les services hors Feature installés par kazopkg ou le Store.
- B) Seulement les enfants Service d'une Feature. `kazowebd` et les services hors Feature gardent le défaut.
- C) A, et `kazowebd` passe en démarrage manuel pour garder le comportement actuel du navigateur. Change le manifeste d'une app système ; relire l'ADR 0077, peut-être une ADR d'ajustement.
- D) Autre : (Spécifiez)

## Réponses

Réponse du 2026-11-18. Tes mots : « 295. C) avec une nuance : Chaque service déclare leur type de démarrage à l'installation et l'utilisateur peut les arrêter et changer le type de démarrage. Une feature peut Controller le type de démarrage des services qu'il fournis. On doit donc avoir une tab dans `System Monitor` avec les services. Dans le meilleur des mondes, une application devrais aussi pouvoir fournir son service et pouvoir le controler. Dans windows, on a la possibilité de démarrer une application à l'ouverture de windows, on devrais avoir la même expérience utilisateur. (on peut créer une TODO pour ce que on a pas de besoins pour déployer le dossier en cours) »

- 295 : **C, avec ta nuance**.
  - Dans 3.5.2 : tout service automatique du catalogue démarre à la connexion, Feature ou non ; `kazowebd` passe en démarrage manuel (ajustement de l'ADR 0077).
  - Principe : chaque service déclare son type de démarrage à l'installation ; l'usager peut l'arrêter et changer ce type ; une Feature peut régler le type de démarrage des services qu'elle fournit.
  - À faire plus tard (TODO, hors 3.5.2) : un onglet « Services » dans System Monitor ; une app qui fournit son propre service et le contrôle ; des apps lancées à l'ouverture de session, comme les applications de démarrage de Windows.
