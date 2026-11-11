# Série 7 — envoyée le 2026-11-10

Les vraies décisions qui restent avant le plan, après vérification de tes [consignes du 10 novembre](consignes-2026-11-10.md) (rapport : [verification-consignes-2026-11-10.md](verification-consignes-2026-11-10.md)). La série 8 traite la répartition des réglages, des profils et des opérations admin, et les détails « à préciser ». Les essais et mesures que je ferai moi-même sont dans [etudes-prevues.md](etudes-prevues.md) (E1 à E15) : je ne te les demande pas.

Numérotation à partir de 101, pour ne pas entrer en collision avec des numéros que ChatGPT aurait pu utiliser après 76. Même règle qu'avant : une ligne vide ou « ok » accepte toutes mes recommandations ; sinon, réponds par numéro. La règle de tes consignes vaut aussi : les questions que tu ne mentionnes pas sont acceptées avec ma recommandation. Quand une option dépend d'une autre question, elle le dit (« selon 104 »).

Pour lire les options : « bouteille » = un préfixe Wine, c'est-à-dire le faux disque C: d'un programme Windows ; « VM Programmes Windows » = la machine Linux gérée par Os-x86 où tournent Wine et les bouteilles ; « VM complète » = un système entier que tu apportes (vrai Windows, ReactOS, un Linux à toi) ; « agent » = le petit programme d'Os-x86 qui tourne dans l'invité ; « OPFS » = le stockage privé du navigateur, séparé des Fichiers de Kazo ; « ADR » = une décision d'architecture écrite dans le dépôt de Kazo avant le code ; « MZ » = l'en-tête d'un programme DOS, « MZ + PE » = celui d'un programme Windows, « ELF » = celui d'un programme Linux ; « binfmt » = le mécanisme par lequel le shell confie un fichier reconnu par son en-tête à une Feature ; « worker » = le fil d'exécution séparé du navigateur où tourne la VM.

---

### Méthode

**101. Puis-je écrire dès maintenant le code jetable des mesures?**
> Ton plan dépend de mesures (démarrage de Wine dans v86, stockage, fenêtre par fenêtre, son). D25 prévoit une maquette au jalon 0. Tes consignes demandent « de vrais essais exécutés et mesurés dans la phase autorisée », mais ne disent pas si cette phase commence maintenant. Les essais qui ne sont que de la lecture (E10, E12, E13) ne sont pas concernés : je les fais de toute façon.

- A) Oui, du code jetable seulement ✨(Recommandé)
  - Dans un dossier `essais/` du dépôt Os-x86, jamais réutilisé dans le produit, sans PR vers Kazo. Chaque essai a son protocole écrit avant, et ses résultats vont dans `docs/essais/`. Aucun code de production avant l'approbation du plan.
- B) Non : d'abord le plan complet, les mesures après ton approbation
  - Le plan contiendra alors des hypothèses non mesurées, et les points conditionnels (Wine graphique en V1, état démarré, stockage) resteront ouverts jusqu'à la phase suivante.
- C) Autre : (Spécifiez)

### Qui possède quoi

**102. À qui appartient une VM Programmes Windows, Linux ou FreeDOS?**
> Tes consignes (§7) disent « une même VM n'est utilisée que par un utilisateur à la fois », avec « des comptes distincts dans l'invité » et un risque de confidentialité « lors de l'utilisation suivante ». Le journal donne à chaque usager ses propres sous-systèmes (D11, D24, D61.11). Les deux ne sont vrais ensemble que si une même VM sert à plusieurs usagers Kazo, tour à tour. Les VM complètes sont à la question 103.

- A) À un seul usager Kazo ✨(Recommandé)
  - D11 et D24 tiennent. Seule l'image de base (lecture seule) est partagée par la machine ; chaque usager a ses VM, ses disques et ses bouteilles. Un seul usager Kazo par VM ; les comptes invités par bouteille de D22, qui séparent les bouteilles entre elles, tiennent. Ça change ta phrase de §7 sur les comptes distincts dans l'invité, pour ces VM seulement. Le prix : deux usagers qui installent le même programme le stockent deux fois.
- B) Commune à plusieurs usagers, utilisée tour à tour, un compte invité chacun
  - Ça change D11, D24 et D61.11. Il faut vérifier que les programmes n'écrivent rien hors de leur compte (E7), et un admin pour créer ces VM.
- C) Autre : (Spécifiez)

**103. Une VM complète peut-elle être commune à plusieurs usagers, avec un compte invité chacun?**
> Un vrai Windows avec ta licence, ou un Linux apporté, est le cas naturel d'une machine partagée : une seule installation, un compte invité par usager Kazo, un usager à la fois (tes mots de §7).

- A) Oui, si un admin la crée ; un usager à la fois ; les dossiers personnels de l'invité restent séparés ✨(Recommandé)
  - C'est ta phrase de §7, limitée aux VM complètes. La séparation entre comptes invités est celle du système invité, pas de Kazo : à dire dans la spécification. Ça précise D11 (sous-systèmes par usager) pour ce cas. D61.11 et ta phrase de §7 (pas d'installation Windows « pour tous ») restent vraies pour la VM Programmes Windows ; une VM complète commune est l'exception explicite que tu décides ici.
- B) Non : une VM complète appartient aussi à un seul usager
- C) Autre : (Spécifiez)

**104. BoxedWine gère-t-il aussi les VM complètes?**
> Tes consignes étendent BoxedWine aux environnements Linux (§2) et retirent la restriction « Linux/DOS par `x86` seulement » (§19.2) : Linux et DOS sont donc déjà couverts. 63 A parlait de « Linux, DOS et complètes » ; seules les VM complètes restent à dire.

- A) Oui : BoxedWine est l'interface de tous tes environnements ✨(Recommandé)
  - Une seule app pour voir, démarrer, arrêter et régler tes VM, quel que soit leur genre ; `x86` fait pareil en ligne de commande. Ça change 63 A (puce 4 : réglages des VM Linux, DOS et complètes par `x86` seulement) pour les VM complètes.
- B) Non : les VM complètes restent à `x86`
- C) Autre : (Spécifiez)

**105. Avec quelle permission BoxedWine gère-t-il tes VM?**
> D32 et D42 exigent « gérer les sous-systèmes » (dangereuse, approuvée par un admin pour toute la machine) pour toucher à une VM que l'app n'a pas créée. 63 A donnait à BoxedWine une troisième permission, limitée aux VM Programmes Windows. Avec Linux, DOS et peut-être les VM complètes (104), cette permission doit s'élargir.

- A) Une permission dangereuse « gérer les environnements de l'usager en session » ✨(Recommandé)
  - Elle élargit la troisième permission de 63 A : BoxedWine règle, démarre et arrête les VM de l'usager connecté, pour les genres retenus en 104, rien d'autre. Jamais les VM d'un autre usager ni les réglages de l'admin. Ça précise D32 (troisième permission) et D42. Conséquence : « gérer les sous-systèmes » reste aux outils d'admin.
- B) « Gérer les sous-systèmes », telle quelle
  - Plus simple, mais BoxedWine pourrait agir sur les VM de tous les usagers, comme un outil d'admin.
- C) Autre : (Spécifiez)

**106. Sans BoxedWine, Os-x86 montre-t-il encore un écran?**
> 63 A donnait à Os-x86 « des fenêtres simples d'affichage » (écran d'une VM, ouvert avec `x86`). Tes consignes (§12) confient les applications Linux graphiques à BoxedWine, même lancées au terminal, et §2 ne liste plus ces fenêtres. Or BoxedWine est facultatif (51 D), et D13 envoie toujours un ELF tapé au terminal à Os-x86.

- A) Oui, les fenêtres simples de 63 A restent, comme repli ✨(Recommandé)
  - Quand BoxedWine est installé, il affiche (§12). Sinon, `x86 start --ecran` et un ELF graphique tapé au terminal ouvrent une fenêtre simple d'Os-x86 : l'écran brut de la VM, sans bibliothèque ni réglages. Ta phrase de 51 (« tout faire avec Os-x86 seul ») tient.
- B) Non : sans BoxedWine, aucun affichage graphique, console seulement
  - Plus simple à construire ; un ELF graphique tapé au terminal échoue avec un message « installe BoxedWine ». Ça change 63 A (fenêtres simples d'affichage).
- C) Autre : (Spécifiez)

### Fichiers et permissions des programmes

**107. Je lis §11 comme remplaçant D22 (« un `.exe` voit son dossier et ses arguments ») par « ce que la VM voit » : confirmes-tu?**
> Tes consignes (§11) veulent que les programmes invités accèdent à ton dossier personnel, aux dossiers locaux autorisés au navigateur et aux montages, « en conservant les droits déjà accordés ». D22 disait « fichiers par sous-système, plafonnés par l'appelant ; un `.exe` voit son dossier et ses arguments ». Faits vérifiés : Kazo n'a aucun montage distant ; le seul montage est un dossier de ton vrai ordinateur (`hostfs`, Chrome et Edge). « Dossiers distants » ne désigne donc rien en V1.

- A) Oui : accès par VM, avec les droits déjà accordés par Kazo (lecture seule reste lecture seule) ✨(Recommandé)
  - Tous les programmes d'une VM voient ce que la VM voit, sans question par programme. Limite connue (61.9) : un programme hostile de cette VM lit tout ce que la VM voit. Ça remplace D22 pour les fichiers ; le plafond par l'appelant (D21, et les permissions que BoxedWine déclare, D43) tient. La présentation (lettre de lecteur, erreur quand un montage disparaît) vient dans le plan.
- B) Non : une permission « fichiers » par programme, comme le réseau (D14), demandée au premier accès hors du dossier du programme
  - Plus sûr, mais une fenêtre de plus par programme. Ça change ton §11 (« doivent pouvoir accéder ») et ton §6 (« éviter une avalanche de confirmations »).
- C) Autre : (Spécifiez)

**108. Une autre app lance un programme que tu as déjà autorisé « toujours » : on redemande?**
> D52 (acceptée) dit : un « toujours » pris pour toi ne couvre pas une autre app ; la fenêtre redemande et nomme les deux apps. Tes consignes (§6) disent : l'origine du lancement ne doit pas, à elle seule, changer la bouteille ni les permissions du programme. La bouteille est déjà tranchée par §6 (même programme, même bouteille, ça change D50) ; ici, seulement la permission.

- A) Pas de nouvelle question : la décision reste au programme, l'app lanceuse ne fait que plafonner ✨(Recommandé)
  - Le droit effectif = ta décision ∩ les permissions de l'app lanceuse (D21 tient). Si l'app a moins de droits, le programme perd ce qui dépasse ; jamais plus. Ça change D52 (plus de « redemande et nomme les deux apps ») ; le reste de D52 (l'OS marque l'expéditeur) tient.
- B) On garde D52 : redemander quand une autre app lance, en nommant les deux apps
  - Plus de questions ; une app ne profite jamais d'un « toujours » pris pour toi.
- C) Autre : (Spécifiez)

### Terminal

**109. Un programme console lancé par `./x.exe` : où va sa sortie en V1?**
> Tes précisions de 53 et 55 (« le terminal s'occupe aussi d'afficher le rendu ») sont enregistrées comme valables après 62 B. Tes consignes (§5) disent qu'une fenêtre séparée est acceptable en V1 et que « le terminal courant n'a pas été imposé ». Faits vérifiés : Kazo donne les flux du shell à une fenêtre lancée par son nom, ligne par ligne ; une app à instance unique déjà ouverte rend 0 tout de suite, sans flux ; Ctrl+C tue la fenêtre et ses enfants sans préavis ; Kazo n'a pas de fenêtre invisible (états normal, réduite, agrandie seulement).

- A) Dans les flux du terminal, ligne par ligne ; la fenêtre BoxedWine s'ouvre réduite ✨(Recommandé)
  - BoxedWine relaie l'entrée et la sortie du programme dans les flux que le shell lui donne : tuyaux (`|`) et redirections (`>`) marchent. Pas de mode brut pour les fenêtres lancées du shell en V1 (pas de touches une par une, pas de couleurs) ; le mode brut de D27 ne reste en V1 que pour les commandes d'Os-x86 (`x86 shell`, et le programme DOS de 111), selon 119. Conditions : une instance BoxedWine par lancement au terminal (ça tranche 72.5) ; comme Ctrl+C tue l'instance sans préavis, Os-x86 détecte la mort de BoxedWine pour arrêter le programme (D61.4), et transmettre Ctrl+C au programme attend un ADR (V2, avec D27). Ça suit 53 et 55 ; ça reporte D27 en V2 pour les fenêtres.
- B) Dans une fenêtre console séparée de BoxedWine ; rien dans le terminal
  - Le terminal attend seulement la fermeture. Tuyaux et redirections ne marchent pas. Ça change tes précisions de 53 et 55.
- C) Les deux : flux du terminal et fenêtre console ouverte en même temps
- D) Autre : (Spécifiez)

**110. Le vrai code de sortie : par quel chemin?**
> 62 B (acceptée) : `./setup.exe` va à BoxedWine, une fenêtre, qui n'a pas de code de sortie ; le terminal reçoit 0, 1 ou 130. Tes consignes veulent le vrai code « souhaité en V1 si possible, obligatoire en V2 ». Le journal dit qu'un ADR qui donne un code de sortie aux fenêtres change 62 B et doit être proposé comme tel. Autre voie écartée : faire passer `./x.exe` par la commande `x86`, ce qui contredirait 62 B et 64.

- A) ADR Kazo « code de sortie d'une fenêtre lancée du shell », tenté en V1, obligatoire en V2 ✨(Recommandé)
  - Petit ADR (le shell sait déjà attendre le code que les commandes rendent) : une fenêtre déclare son code avant de se fermer. BoxedWine y met le code du programme (tronqué à 8 bits par Wine). Ça change 62 B (« une fenêtre n'a pas de code de sortie ») : dès que l'ADR est livré, elle en a un.
- B) V2 seulement : en V1, 0, 1 ou 130
- C) Autre : (Spécifiez)

**111. Un `.exe` DOS tapé au terminal : par Os-x86 ou par BoxedWine?**
> D28 (acceptée) : FreeDOS géré par Os-x86, mode texte dans le terminal. 62 B envoie tout `.exe` à BoxedWine par l'association, et 64 interdit toute association à Os-x86. Tes consignes préfèrent le contenu détecté à l'extension et veulent DOS au terminal « si sa complexité reste raisonnable ». Faits vérifiés : v86 sait lire l'écran texte d'une VM (`get_text_screen`, par interrogation), ce qui règle l'affichage ; l'entrée du terminal Kazo reste en mode ligne pour une fenêtre, ou un jeu de touches réduit (sans F1 à F12) pour une commande. Un `.com` n'a pas d'en-tête.

- A) Par Os-x86 : son binfmt par en-tête reconnaît un `.exe` MZ pur (DOS) avant l'association ✨(Recommandé)
  - Un `.exe` DOS tapé au terminal va à FreeDOS, texte dans le terminal, sans BoxedWine (D28 et 53 A puce 1 tiennent) : en V1, programmes texte à entrée simple ; les touches de fonction attendent le mode brut de 119. Un `.exe` Windows (MZ + PE) passe par l'association, donc BoxedWine (62 B tient). Un `.com`, sans en-tête, passe par l'association `.com` (BoxedWine, qui le confie à Os-x86) : au double-clic par tes consignes, et au terminal par la route du shell vers l'association (journal, ligne 252), ce que je tranche ici avec le `.exe` DOS. Un programme DOS graphique lancé au terminal s'affiche selon 106. Ce binfmt n'est pas une association d'extension (64 tient). Seul ce cas est tranché ici ; la règle générale contenu/extension (`.bat`, `.cmd`, `.msi`, sans extension, erreurs de format) vient dans le plan avec ses exceptions, à valider.
- B) Par BoxedWine : tout `.exe` passe par l'association, BoxedWine confie DOS à Os-x86 et affiche
  - Une seule route pour le shell. Mais DOS au terminal exige BoxedWine, et la sortie passe par 109. Ça change D28 et 53 A puce 1.
- C) Autre : (Spécifiez)

### Fenêtres et démarrage

**112. La fenêtre par fenêtre : avant ou après la fin de V1?**
> Objectif confirmé par tes consignes : une fenêtre Kazo par vraie fenêtre Windows. D26 prévoyait une fenêtre par programme en v1, le mode intégré plus tard. Rien n'est mesuré ; les trois façons de sortir chaque fenêtre de l'écran de la VM (un agent qui lit les fenêtres X11, un serveur d'affichage distant comme Xpra, ou un pilote d'affichage à nous) coûtent toutes plusieurs mois. Tu demandes seulement le calendrier.

- A) V1 = une fenêtre Kazo par programme (D26) ; fenêtre par fenêtre dans un jalon suivant, après l'essai E4, sans bloquer la fin de V1 ✨(Recommandé)
  - Le titre de la fenêtre Kazo suit déjà la fenêtre principale du programme ; l'icône suivra avec l'ADR de 57. La fenêtre par fenêtre arrive en V1.x ou V2 selon E4, avec un plan à te faire valider.
- B) La fenêtre par fenêtre conditionne la fin de V1
  - La fin de V1 recule d'au moins l'essai E4 plus sa mise en œuvre, et d'un ADR Kazo « fenêtres sans propriétaire ».
- C) Autre : (Spécifiez)

**113. Si E1 montre un démarrage à froid au-dessus de 10 s, un état « déjà démarré » livré avec l'image est-il acceptable en V1?**
> Tes consignes arrêtent une VM d'applications dès que ses programmes se ferment, reportent la veille/reprise usager en V2, et visent un démarrage sous 10 s. Le journal juge un état « déjà démarré » indispensable pour Wine dans v86 (ligne 77 : un Puppy avec Wine a mis 15 minutes). Ce n'est pas ta veille personnelle : c'est un état gabarit, créé une fois par version d'image, restauré à chaque démarrage. Contraintes de v86 (lues ce jour) : l'état se construit avec des disques chargés paresseusement et sans écriture ; les périphériques sont figés, donc le disque de l'usager est soit déjà présent (même emplacement, même taille) quand le gabarit est pris, soit un partage remonté par l'agent après la restauration, qui réinitialise aussi le réseau. E1 mesure les deux voies.

- A) Oui : un état gabarit par image, restauré à chaque démarrage, jeté à chaque mise à jour ✨(Recommandé)
  - Ça ajoute un troisième genre d'état à D34 et D48 (points de restauration durables, états de veille jetables) : l'état gabarit, lié à la version de v86 et de l'image. La veille/reprise personnelle reste en V2.
- B) Non : démarrage à froid toujours ; on accepte le temps mesuré par E1
- C) Autre : (Spécifiez)

### Stockage et sauvegarde

**114. La synchronisation des disques de VM entre appareils : en V1 ou en V2?**
> Faits vérifiés : les Fichiers de Kazo refusent tout fichier de plus de 16 Mio ; la synchronisation de Kazo ne couvre que le dossier personnel, lisible par toute app avec une permission normale ; l'OPFS n'est jamais synchronisé. Ta règle « toujours demander en cas de conflit » marche pour des fichiers ; pour un disque entier ou une couche de blocs, choisir un côté jette tout l'autre côté. Où ranger les disques (OPFS, fichiers Kazo en morceaux, couches) est l'objet de E2, puis d'une question.

- A) En V2 ; en V1, un disque ne s'ouvre qu'une fois et ne se synchronise pas ✨(Recommandé)
  - Ce qui se synchronise en V1 : tes fichiers dans ton dossier personnel (vus par la VM, 107). Tes décisions et ta bibliothèque restent par appareil en V1 ; leur synchronisation (domaine de synchro propre à Os-x86, ADR Kazo) vient avec les disques en V2. Une bouteille exportée (115) est un fichier téléchargé sur ton ordinateur, pas un fichier de Kazo. Ça reporte en V2 la synchronisation des disques demandée en §9, avec un conflit qui demande à l'usager par VM entière (E11 prépare le mécanisme).
- B) Dès V1
  - Domaine de synchro Os-x86 dans Kazo (ADR), transfert de plusieurs Go, et dialogue de conflit par VM entière : choisir l'ancienne version perd tout le travail de l'autre appareil, pour toutes les bouteilles.
- C) Dès V1, par un ADR Kazo qui lève la limite de 16 Mio : disques rangés dans les Fichiers de Kazo, synchronisés par morceaux
  - Le stockage de Kazo est déjà découpé en morceaux de 256 Kio. Même problème de conflit par disque entier ; et toute app avec une permission normale verrait les disques (contre D61.12).
- D) Autre : (Spécifiez)

**115. « Sauvegarde demandée par l'utilisateur » : laquelle?**
> Faits vérifiés : la sauvegarde de Kazo est réservée à l'admin, couvre toute la machine (un seul fichier JSON de la base), exclut l'OPFS et n'a aucune fenêtre avec des cases à cocher ; Kazo ne sait télécharger un fichier qu'en le tenant entier en mémoire. Tes consignes (§10) supposent une fenêtre d'exportation où les VM sont cochées par défaut.

- A) Une nouvelle exportation personnelle d'Os-x86 : tes VM et bouteilles, par BoxedWine ou `x86 export` ✨(Recommandé)
  - Fenêtre dans BoxedWine (ou commande `x86`), VM cochées par défaut, export d'une seule bouteille possible. Téléchargement en flux par la fenêtre « Enregistrer sous » du navigateur (`showSaveFilePicker`, Chrome et Edge) ; ailleurs, en plusieurs fichiers téléchargés de taille bornée, ou vers un lecteur monté (hostfs) ; jamais dans les Fichiers de Kazo (114). Ça change 63 A (export du disque par `x86` ou « gérer les sous-systèmes ») : BoxedWine exporte avec la permission de 105. Ça précise ton §10 et §19.13 : la « sauvegarde demandée par l'utilisateur » est cette exportation ; la sauvegarde système de Kazo (admin) reste sans VM en V1. Conséquence : un ADR Kazo « contributeurs de sauvegarde » est proposé pour V2. La cohérence d'une sauvegarde pendant que la VM tourne et l'inclusion de l'image de base : E15, puis une question.
- B) Étendre la sauvegarde système de Kazo aux VM dès V1 (ADR Kazo, admin seulement, format en flux à créer)
- C) Autre : (Spécifiez)

### Réseau et confiance

**116. Le réseau simulé des VM : relié au réseau simulé de Kazo, ou séparé?**
> Tes consignes (§14) veulent que chaque VM soit un hôte et que Kazo ait sa propre adresse sur ce réseau. Faits vérifiés : le réseau simulé de Kazo travaille au niveau HTTP seulement (noms `kazodns`, écouteurs HTTP des apps) ; il ne transporte aucun flux TCP brut ; aucune app ne peut y enregistrer un hôte aujourd'hui. D20 donne déjà à chaque bouteille son propre espace réseau isolé. La comparaison « plusieurs programmes dans une VM » contre « VM séparées » que demande ton §6 se fera à partir de E6 et te sera soumise après.

- A) Un réseau propre à Os-x86 entre VM, relié à celui de Kazo par une passerelle HTTP ✨(Recommandé)
  - Entre VM : un réseau Ethernet d'Os-x86, chaque VM avec une adresse. Vers Kazo : HTTP seulement (le réseau de Kazo ne transporte pas d'autres flux). Sans ADR, une VM n'est joignable que par un port qu'Os-x86 écoute sur l'adresse de Kazo ; « chaque VM un hôte nommé » demande un ADR Kazo « hôtes enregistrés par une Feature ». Les VM joignent les apps de Kazo qui écoutent en HTTP. Ça restreint §14 : les services non HTTP d'une VM ne sont visibles que des autres VM. La politique par défaut (qui voit qui) vient avec E6.
- B) Un réseau propre à Os-x86 seulement, sans lien avec le réseau simulé de Kazo
  - Plus simple ; le navigateur de Kazo ne joint pas un serveur web qui tourne dans une VM. Ça change §14.
- C) Autre : (Spécifiez)

**117. Comment empêcher un programme invité d'imiter une fenêtre de confiance?**
> Tes consignes refusent 70 A (marque dessinée par Kazo sur les raccourcis), demandent une API d'icône de fenêtre, et interdisent le consentement tapé au terminal parce qu'un programme pourrait l'imiter. Faits vérifiés : Kazo compte sur « l'icône de la barre des tâches reste celle de l'app » contre les faux titres (ADR 0117) ; mais depuis l'ADR 0234, une app peut cacher cette icône, son bouton de barre des tâches ou tout son cadre. Dès qu'une app peut changer son icône, un programme Windows peut ressembler à la fenêtre de permission d'Os-x86.

- A) Kazo marque les fenêtres de programmes invités ✨(Recommandé)
  - C'est 70 C, réduit aux fenêtres : toute fenêtre dont l'icône n'est pas celle de son app porte, dans la barre de titre et le bouton de la barre des tâches, une petite marque de l'app qui l'affiche (BoxedWine ou Os-x86), dessinée par Kazo, quels que soient le style et les options de cadre (ou ces options sont refusées aux fenêtres de programmes). Les fenêtres de confiance d'Os-x86 n'en ont pas. Ça fait partie de l'ADR d'icône de 57 ; la marque sur les raccourcis reste à BoxedWine (57, tes mots).
- B) Aucune marque ; les fenêtres de confiance d'Os-x86 passent par les écrans sécurisés de l'OS (ADR Kazo « fenêtre de confiance d'une Feature »)
  - Protège les fenêtres de confiance, pas une app de Kazo imitée par un programme.
- C) Autre : (Spécifiez)

### Périmètre

**118. V1 : une seule livraison, ou des jalons intermédiaires démontrables?**
> Tes consignes fixent quatre résultats pour la fin de V1 (Feature + API, Linux console et graphique, FreeDOS, Windows graphique par Wine) et acceptent un développement progressif. Compté dans l'inventaire provisoire de [etudes-prevues.md](etudes-prevues.md) : une dizaine d'ADR Kazo avant le premier jalon, une vingtaine en tout.

- A) Des jalons intermédiaires démontrables, chacun avec ses tests ; « fin de V1 » = les quatre résultats de §4 livrés ✨(Recommandé)
  - Le contenu de chaque jalon vient dans le plan, à valider là. Rien n'est publié sans ton accord (§18).
- B) Une seule livraison V1 complète
- C) Autre : (Spécifiez)

**119. « Linux utilisable en ligne de commande » en V1, ça veut dire quoi?**
> Tes consignes veulent démarrer, arrêter et « ouvrir une session de commandes Linux depuis le terminal Kazo », avec `docker exec` comme inspiration, mais classent « l'intégration avancée du terminal » en V2. Fait vérifié : une commande de type Terminal reçoit déjà de Kazo un écran plein et un jeu de touches réduit (sans F1 à F12 ; Ctrl+C ferme la commande). 73 A proposait une syntaxe (non acceptée) ; je la reprends ici, à valider.

- A) Un shell interactif dans le terminal Kazo, par l'agent, avec le jeu de touches actuel ; mode brut au jalon suivant ✨(Recommandé)
  - `x86 shell <vm>` ouvre un terminal dans l'onglet courant (éditeurs, couleurs) ; `x86 run <vm> -- <commande>` lance une commande et rend son code. Au premier jalon, Ctrl+C ferme la session et les touches de fonction manquent ; l'ADR « mode brut » (D27) les ajoute au jalon suivant, pour les commandes d'Os-x86 seulement (`x86 shell`, et un programme DOS lancé par le binfmt de 111), jamais pour les fenêtres lancées du shell (109). Une session `x86 shell` compte comme une application qui garde sa VM en marche (§7). Ça change ton §4 (intégration avancée en V2) pour `x86 shell` seulement. Conséquence : sans agent (image apportée), console série, une session, plus lente.
- B) Commandes ligne par ligne seulement (`x86 run`), le shell interactif en V2
- C) La console de la VM dans une fenêtre d'Os-x86 seulement ; rien dans le terminal Kazo
- D) Autre : (Spécifiez)

---

On retient mes recommandations pour les questions 101 à 119? Une ligne vide ou « ok » veut dire oui ; sinon, réponds par numéro (par exemple « 105 B »).
