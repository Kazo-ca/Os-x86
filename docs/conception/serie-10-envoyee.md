# Série 10 : ce qui n'est pas clair après tes réponses du 2026-11-11

Tes réponses aux séries 7, 8 et 9 sont enregistrées dans le [journal](journal-decisions.md) (section « Réponses du 2026-11-11 »), avec leurs réserves. Huit points restent ambigus ou se contredisent ; je ne les tranche pas à ta place. Même protocole : une lettre, rien pour la recommandation ✨, ou « Autre ».

**152 — Démarrage à froid en V1 (113) contre l'état gabarit de 151 A**
Contexte : 113 dit « toujours démarrer à froid en V1, état modèle évalué pour V2 ». 151 A, telle que je l'avais écrite, met la bouteille gabarit « dans l'état gabarit » (une VM déjà démarrée, sauvegardée chez nous). Les deux ne tiennent pas ensemble. Chiffres de E1 : démarrage à froid de Linux 62 s, puis Xorg 12 à 16 s ; depuis un état sauvegardé, 2 à 4 s ; `wineboot` 7 minutes dans les deux cas s'il n'est pas déjà fait.
- A ✨(Recommandé) : 113 tient. En V1 la VM démarre à froid (62 s, à afficher) ; la bouteille gabarit est livrée **en fichiers** dans l'image, préfabriquée chez nous, copiée pour chaque nouvelle bouteille ; aucun `wineboot` chez l'usager. L'état gabarit (VM déjà démarrée) est évalué pour V2. 151 A est reformulée ainsi.
- B : 151 A telle quelle : l'état gabarit remplace le démarrage à froid dès V1 ; 113 est révisée.
- C : démarrage à froid par défaut, état gabarit offert comme option dans les réglages de la VM dès V1.
- Autre : (Spécifiez)

**153 — 150 B contre « Wine graphique en fin de V1 »**
Contexte : tes consignes et D25 visaient Windows graphique à la fin de V1. 150 B redéfinit le jalon 0 sur la console et repousse Wine graphique « à un jalon ultérieur sans date ». Je dois savoir si V1 peut se clore sans lui.
- A ✨(Recommandé) : Wine graphique reste un objectif de V1 sans jalon daté ; V1 n'est pas close tant qu'il n'est pas livré, sauf décision contraire de ta part après un essai dédié (autre Wine, voir E13 : paquets i386 de WineHQ).
- B : Wine graphique sort de V1 ; V1 se clôt sur console Wine, DOS et Linux ; Wine graphique devient V1.x.
- C : décision reportée au résultat de l'essai dédié (une journée), que je lance maintenant.
- Autre : (Spécifiez)

**154 — Qui affiche la fenêtre console de 109 et l'écran DOS de 111 ?**
Contexte : 106 dit qu'Os-x86 n'ouvre aucune fenêtre de VM propre ; 111 envoie un programme DOS du terminal directement à Os-x86 sans BoxedWine ; 109 accepte une fenêtre console séparée. Si Os-x86 n'affiche rien et que BoxedWine n'est pas dans la boucle, personne n'affiche le programme DOS. 63 A gardait à Os-x86 « des fenêtres d'affichage simples ».
- A ✨(Recommandé) : Os-x86 garde ses fenêtres d'affichage simples de 63 A (console texte, écran DOS plein), sans réglages ni bibliothèque ; elles servent à 109 et 111 ; BoxedWine affiche tout le reste. 106 se lit « pas de fenêtre de VM gérée », pas « aucune fenêtre ».
- B : le terminal de Kazo affiche lui-même le texte (E14) ; DOS graphique passe par BoxedWine comme Windows.
- C : tout passe par BoxedWine, y compris DOS ; 111 est révisée.
- Autre : (Spécifiez)

**155 — Attribution d'une VM à un usager (124.1) : V1 ou V2 ?**
Contexte : 102 dit « V1 environnements partagés à tour de rôle, V2 différentiel personnel » ; le sujet « conteneurs toujours actifs » dit « attribution personnelle en V2 » ; 124.1 demande de pouvoir attribuer une VM à un usager précis et réserver certaines VM aux admins.
- A ✨(Recommandé) : en V1, restrictions par VM (qui peut démarrer, qui peut administrer) et attribution d'une VM à un usager (elle n'apparaît qu'à lui et aux admins) ; le différentiel personnel sur une base commune reste V2.
- B : tout cela en V2 ; en V1 les VM communes sont visibles de tous les usagers admis.
- Autre : (Spécifiez)

**156 — Le « rôle qui permet de gérer les VM » de 140**
Contexte : Kazo n'a que trois autorités (Utilisateur, Administrateur, Système) ; ajouter un rôle est un ADR lourd dans Kazo. Une permission par usager accordée par l'admin existe déjà comme mécanisme (décisions de capacités).
- A ✨(Recommandé) : pas de nouveau rôle Kazo ; dans la page Os-x86 des Paramètres, section Utilisateurs, l'admin coche par usager « peut gérer les VM communes » (et par VM, 124.1). C'est une permission d'Os-x86, vérifiée à chaque appel.
- B : un vrai rôle dans Kazo (ADR), réutilisable par d'autres Features.
- Autre : (Spécifiez)

**157 — Les trois issues publiques dans Kazo-ca/Os (135)**
Contexte : ton accord est consigné dans le document de ChatGPT. Ouvrir une issue publique est irréversible ; je veux ta confirmation ici, et savoir si tu veux relire les textes. Les trois points : `StartChild` sans vérification de permission ; permissions « sujet » accordées en silence à la connexion (ADR 0040) ; fichier de refus écrasable par `filesystem.user-home.write`.
- A ✨(Recommandé) : je rédige les trois textes (en anglais, sans donnée personnelle), je te les donne à relire, puis je les ouvre quand tu dis « ouvre ».
- B : ouvre-les tout de suite.
- C : pas maintenant.
- Autre : (Spécifiez)

**158 — « Os-X86.Interface implémente Ix86 »**
Contexte : jusqu'ici (D10) `Os-x86.Interface` était le paquet NuGet de **contrats** (interfaces, sans code), référencé par les apps. Ton schéma dit qu'il « implémente Ix86 », ce qui en ferait une implémentation.
- A ✨(Recommandé) : `Os-x86.Interface` = paquet de contrats, qui **contient** `Ix86` (et les types associés) ; `x86-Lib` = implémentation réutilisable du moteur, qui implémente `Ix86` ; la Feature Os-x86 enregistre une instance de `Ix86` dans Kazo ; BwLib dépend de `Ix86` seulement ; BoxedWine dépend de `IBwLib`.
- B : `Os-x86.Interface` est bien une implémentation (classe) de `Ix86`, et les contrats vivent dans un autre paquet à nommer.
- Autre : (Spécifiez)

**159 — Ton prénom dans le document des réponses**
Contexte : le document cite ton prénom 90 fois ; le dépôt est public. Rien n'est poussé tant que tu n'as pas répondu.
- A ✨(Recommandé) : je remplace le prénom par « toi » / « l'usager » dans la copie du dépôt avant de pousser.
- B : garder tel quel.
- C : ne pas mettre ce document dans le dépôt (je garde seulement le journal).
- Autre : (Spécifiez)

Propositions qui ne demandent pas de question (je les mets dans le registre réconcilié, à valider là) : noms des permissions de 141 (`x86.vm.manage` globale ; `x86.vm.use` courant ; `x86.vm.destroy` suppression et export) ; seuil de 149 : images de moins de 64 Mio admises dans IndexedDB.
