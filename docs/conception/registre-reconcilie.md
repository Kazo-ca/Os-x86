# Registre réconcilié des décisions — Os-x86 et BoxedWine

Écrit le 2026-11-11, à la suite de ta réponse « Registre d'abord ». C'est le point 2 de tes consignes §20 : une seule liste, par thème, de ce qui est décidé, avec les numéros d'origine, ce que les réponses récentes ont changé, les conditions, et ce qui reste ouvert. Le [journal](journal-decisions.md) reste la source chronologique ; ce registre ne le remplace pas, il le lit.

Nouveau fichier, qui ne double aucun autre : le journal est chronologique (série par série), les vérifications sont des rapports datés, ce registre est thématique et dit l'état final de chaque point. Il sera cité par le plan (§20, point 1 à 10) et mis à jour à chaque nouvelle série.

## Règles de lecture

**Ordre de priorité des sources** (la plus récente prime quand deux se contredisent) :

1. Série 10 (152 à 159), répondue le 2026-11-11.
2. Réponses du 2026-11-11 aux séries 7, 8 et 9 (102 à 149), consignées dans [reponses-2026-11-11.md](reponses-2026-11-11.md) ; leurs réserves « à préciser » restent applicables ; un « oui » ne valide que ce qui a été présenté à l'oral.
3. Réponses du 2026-11-10 (101, 105, 134, 150, 151) et les [consignes du 2026-11-10](consignes-2026-11-10.md), qui corrigent le journal là où elles le disent (§19).
4. Le journal, séries 1 à 6 (1 à 64).
5. La carte C1 à C45 de [decisions.md](../recherche-2026-11-08/decisions.md), seulement où rien d'autre n'a tranché.

**Ce qui n'est pas une décision** : les recommandations de la série 6 après 64 (65 à 76) ; mes recommandations des séries 7, 8 et 9 que tu n'as pas retenues (ta règle du 2026-11-10 : pas d'acceptation par défaut) ; les propositions des rapports d'essais (toutes marquées « à valider ») ; les recommandations R105.x et R134.x de [verification-105-134.md](verification-105-134.md).

**Statuts** :

| Statut | Sens |
|---|---|
| **Décidé** | Validé par toi, explicitement. |
| **Décidé, réserve** | Validé, mais avec un « à préciser » enregistré avec la réponse ; la réserve est citée. |
| **Conditionnel** | Décidé dans son principe ; le classement V1/V2 ou le mécanisme dépend d'une mesure ou d'une étude nommée. |
| **À valider** | Ma proposition ou le résultat d'un essai ; rien n'est retenu tant que tu n'as pas dit oui. |
| **Ouvert** | Ta réponse est requise ; la question sera posée dans une prochaine série (numéros à partir de 160 ; 77 à 100 restent à ChatGPT). |
| **Remplacé** | Ancienne décision, gardée pour la traçabilité, avec ce qui la remplace. |

Faits de Kazo : lus au commit `cd89ee0` (2026-11-09 et 10), revérifiés sur `main` `6aef3e0` le 2026-11-11 pour les points des issues #431 à #433 (inchangés). Les autres faits « cd89ee0 » sont des lectures datées, pas des mesures ; ils seront revérifiés au moment de chaque ADR.

## A. Vision, rôles et intégration dans Kazo

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D1, D5 | Deux niveaux : VM complète (systèmes invités) et exécution intégrée de programmes, notamment Windows par Wine dans une VM Linux. Le bac à sable serveur est un autre projet ; pas de repli serveur. | S1 ; consignes §2 | Décidé |
| D2 | De MsDos.Clone on reprend l'organisation et la méthode (cœur séparé des adaptateurs, tests, étapes, hôte de démo), pas le CPU. | S1 ; consignes §2 | Décidé |
| D7, D6 | Os-x86 est une **fonctionnalité facultative** de Kazo, « comme WSL » : livrée par le Marketplace, installée au niveau système par un admin, avec de petits points d'extension génériques dans le noyau (ADR). Elle n'a pas de réputation ou de droits supérieurs aux autres extensions du seul fait d'être système. | S1 ; consignes §2 | Décidé |
| D7 (puce 2) | « Grâce à elle, l'OS exécute des .exe dans le terminal » : vrai avec BoxedWine installé. **Os-x86 seul ne lance aucun programme Windows** (58 A puce 2, 62 B, 64) ; il lance les ELF et, par 111, les programmes DOS. | S6, 111 | Décidé (précisé par 62 B et 111) |
| D9, D29 | Pas de moteur Boxedwine (GPL). Os-x86 fait tourner Wine **dans** sa VM Linux. `Os-x86.Interface` reste générique (aucun type Wine) ; BoxedWine pilote Wine par des commandes dans l'invité. | S2, S3 | Décidé |
| D33, D37, D38, D39 | Nouveau type d'app Kazo **« Feature »** : une identité (installation, mise à jour, retrait, Store, permissions), un identifiant enfant par point d'entrée (commande, fenêtre, service, page web), permissions déclarées en bloc puis par point d'entrée. Seules les Features fournissent une interface, déclarent des permissions de fournisseur, enregistrent un binfmt, et peuvent dépendre d'une autre Feature. Section « Fonctionnalités » dans les Paramètres. Utilisable tout de suite après l'installation, sauf copie plus ancienne des contrats déjà chargée (redémarrage). | S3, S4 ; consignes §3 | Décidé |
| D10, D30, D31, D36 | **`Os-x86.Interface`** = paquet NuGet de **contrats** (sans code), référencé à la compilation par les apps clientes, embarqué dans chacune (#419 à terminer côté Kazo, règle « une copie par nom, la plus récente compatible »). Résolution : `await GetServiceAsync<T>()` → null si aucun fournisseur utilisable, jamais mis en cache ; `IsAvailable<T>()` synchrone ; `GetStatus` ; permission manquante = exception ; dépendance facultative hors plage = absente, pas retirée du catalogue. « Référencer la fonctionnalité » = dépendance de manifeste (facultative ou obligatoire) sur la Feature. | S2, S3, S4 ; consignes §3 | Décidé |
| 158 | **Architecture des bibliothèques** (tes noms) : BoxedWine (app, BW) ; Os-x86 (Feature, x86) ; **BwLib** (Boxed-WineLib, réutilisable) ; **x86-Lib** (réutilisable) ; contrats **IBwLib** et **Ix86**. `Os-x86.Interface` contient `Ix86` et les types associés ; `x86-Lib` implémente `Ix86` ; la Feature Os-x86 enregistre une instance d'`Ix86` dans Kazo ; BwLib dépend d'`Ix86` seulement ; BoxedWine dépend d'`IBwLib`. Aucun héritage entre IBwLib et Ix86 ; nombre de projets et mécanisme d'injection non déduits. | Sujet sans numéro du 2026-11-11 ; 158 A | Décidé |
| D35 (nommage) | Identifiants d'app, de points d'entrée et espaces de noms C# fixés avant la première publication ; « Os-x86 » (avec tiret) n'est pas un espace de noms ; nom NuGet réservé tôt. | S3, S4 ; consignes §3 | Décidé (valeurs : Ouvert, dans le plan) |
| D11 (révisé), D12 | Un paquet Feature, installé au niveau système par un admin ; V1 : environnements Programmes partagés à tour de rôle (102) ; bouteilles personnelles (D20, D22) ; VM complète privée ou partagée (103) ; voir la section E. Store et kazopkg résolvent les dépendances (ADR) ; installer BoxedWine propose Os-x86 (72.10) ; refus = installation de BoxedWine arrêtée avec message ; retirer Os-x86 avertit et désactive les apps dépendantes ; désinstaller garde les données. | S2 ; consignes §3 | Décidé |
| D51, 49, 104 | **BoxedWine = app ordinaire**, « une façon simple d'utiliser Os-x86 », pas une Feature, pas l'émulateur Boxedwine. Il reçoit les lancements Windows par les associations, porte l'interface de gestion, la bibliothèque, les fichiers récents, les profils ; il gère aussi **les environnements Linux (consignes §2) et les VM complètes (104)**. « Le rôle d'interface ne confère pas tous les droits » (104). | S5, S6 ; consignes §2 ; 104 | Décidé |
| 63 A (révisé) | **Os-x86 n'a aucune app** : la commande `x86` (configuration comprise), ses fenêtres de sécurité (permission réseau, page des règles, sélecteur de bouteilles) ; propriétaire des confirmations de 124.2 et 124.3 à préciser ; 124.10 est une confirmation de Kazo. **Plus d'app Sous-systèmes** (change D11, 49 A). Les « fenêtres simples d'affichage » de 63 A sont retirées par 106 et 154 : Os-x86 n'ouvre aucune fenêtre de VM ; l'affichage va à BoxedWine (graphique) ou au terminal de Kazo (console, DOS texte). | S6 ; 106 ; 154 B | Décidé |
| D49 (exception de sécurité) | Os-x86 applique les permissions et garde ses protections et confirmations **quel que soit l'appelant** (BoxedWine, terminal, autre app). Aucune opération d'API ne permet à un client de s'accorder une permission ; aucun consentement tapé dans un terminal (seule exception : l'option explicite de 124.3 qui saute une confirmation de suppression, droits vérifiés) ; les fenêtres de confiance sont celles d'Os-x86 ou de l'OS. | S5 ; consignes §2, §6 ; 124.3, 124.6 | Décidé |
| 156 | **Un vrai rôle Kazo** pour gérer les VM, et « la fonctionnalité devra avoir la possibilité de déclarer des rôles » → ADR Kazo « rôles déclarés par une Feature », avant le premier jalon. Articulation avec les permissions des apps : à préciser dans l'ADR. | 140 B, 156 B | Décidé, réserve |

## B. Moteurs, paliers et périmètre V1 / V2

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D3, C6 | v86 officiel dans un Web Worker ; le C# intègre, derrière une abstraction de moteur. Pas de fork annoncé ; les écarts nécessaires sont identifiés d'abord (E5 : correctif #1661 absent de 0.5.473 ; E8 : serveur 9p à remplacer par le nôtre ; E1 : `.zst` rechargés à chaque boot). | S1 ; consignes §4 | Décidé ; liste des écarts : À valider |
| D4, D16, D17 | Paliers : 32 bits avec v86 → Linux x86-64 sur un moteur choisi après banc d'essai (les `.exe` 64 bits arrivent là, par le nouveau WoW64) → Windows x64 expérimental, sans date, sans bloquer le 32 bits. E13 : QEMU Wasm progresse mais exige SharedArrayBuffer et COOP/COEP ; aucun fork 64 bits de v86. | S1, S2 ; consignes §4 ; E13 | Décidé |
| D35, D61.6 | **Wine 11.0 épinglé** ; Wine Mono et Wine Gecko dans l'image. Base retenue par les recherches et les essais, non validée comme décision : Alpine 3.24 x86 (seule distribution 32 bits simple ; E13 : vivante, Wine 11.0 figé par un mainteneur unique ; WineHQ i386 comme roue de secours) ; Mono 10.4.1 et Gecko 2.47.4 depuis WineHQ (versions exigées par Wine 11.0). Consignes §8 : vérifier sans changer la version en silence. | S3, S5 ; E13 | Décidé (Wine 11.0, Mono et Gecko) ; distribution et versions : À valider |
| D28, 111 | DOS : **FreeDOS géré par Os-x86** dans v86 ; Wine ne couvre pas DOS. Un programme DOS tapé au terminal va **directement à Os-x86/FreeDOS, sans BoxedWine** (111) ; le terminal de Kazo l'affiche (154 B). Réserve 111 : la reconnaissance du contenu (COM, NE, PE, extensions) reste à concilier ; réserve 154 : DOS graphique non tranché. | S3 ; 111 ; 154 | Décidé, réserve |
| Fin de V1 | Quatre résultats : (1) Os-x86 Feature + API + app de gestion/lancement (BoxedWine) ; (2) Linux en ligne de commande et graphique ; (3) FreeDOS ; (4) **Windows graphique par Wine**, visé ; si trop complexe, V1 se clôt sans lui et il ouvre V2 (153). | consignes §4 ; 153 | Décidé |
| D25 → 150 B → 153 | Le repli « VM et DOS d'abord » ne clôt plus V1 (§4). **Jalon 0 redéfini sur les programmes console** (Wine `cmd`, DOS, Linux) tant que le bogue des fenêtres Wine 1x1 n'est pas résolu (150 B). **153, tes mots** : « on va essayer de la terminer à la fin de la V1, mais si trop complexe, on va la faire dans le début de la V2 ». Le critère est « si trop complexe » ; un essai dédié avec un autre Wine (WineHQ i386, E13) pour résoudre le bogue 1x1 est ma proposition, À valider. | 150, 153 ; E1, E4 | Décidé ; classement final de Wine graphique : Conditionnel (critère « trop complexe ») |
| 152 | **V1 démarre toujours à froid** (113). La **bouteille gabarit est livrée en fichiers dans l'image**, préfabriquée chez nous, copiée pour chaque nouvelle bouteille ; **jamais de `wineboot` chez l'usager** (151 A reformulée). L'**état gabarit** (VM déjà démarrée) est évalué pour V2. Chiffres E1 : à froid 62 à 65 s + Xorg 12 à 16 s ; depuis un état 2 à 4 s ; `wineboot` 7 min. | 113, 151 A, 152 A ; E1 | Décidé |
| C18 | Vrai Windows invité (média et licence de l'usager) : souhaité en V1 « si peu de changements », sinon V2. E13 : v86 a NX depuis 0.5.470 ; Windows 10/7 32 bits démarrent selon un contributeur ; verdict proposé : **« peu de changements » ne tient pas** (disque 32 Go, pas d'état gabarit, ni 9p ni agent) → V2 avec essai de faisabilité en V1. | consignes §4 ; E13 | À valider (verdict E13) |
| 118 | Jalons intermédiaires démontrables, chacun avec ses tests ; « fin de V1 » = les quatre résultats, le quatrième selon 153. Le détail des jalons n'est pas validé par là. | 118 | Décidé, réserve |
| V2 explicites | Quotas admin de stockage ; presse-papiers riche et images ; synchro des disques, de la bibliothèque et des décisions (114) ; attribution d'une VM à un usager (155) ; différentiel personnel sur base commune (102) ; code de sortie obligatoire (110). **Plus tard, calendrier ouvert** : veille/reprise usager (« possibilité V2 », §4) ; héritage fin des permissions (« V2 au plus tôt », 144) ; son par programme (« plus tard », 133). | consignes §4 ; 102, 110, 114, 133, 144, 155 | Décidé |
| V1 si possible, sinon V2 | Vrai code de sortie (110) ; exploration du disque interne depuis Kazo (§11) ; import d'une bouteille exportée ailleurs (§4) ; presse-papiers réel (136, selon E9) ; programmes trouvés dans les bouteilles (130). | consignes ; 110, 130, 136 | Conditionnel |
| 101 | Code jetable de mesure autorisé avant le plan (dossier `essais/`, jamais réutilisé, aucune PR). | 101 A | Décidé |

## C. Lancement des fichiers et terminal

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D62 B, D64, D53 | **Route Windows** : `./setup.exe` au terminal, le double-clic et les demandes d'ouverture passent par **l'association `.exe` → BoxedWine**. Os-x86 n'enregistre **aucune association** d'extension. Cette route n'est pas modifiée par la console avancée. | S6 ; consignes §5 | Décidé |
| Route du shell (ADR) | Le shell de Kazo apprend une route : un fichier lancé par son chemin dont l'extension est déclarée exécutable par une app va à l'app associée, **avant** le test du bit d'exécution et **avant** la lecture comme script. Un `.exe` ne doit pas échouer faute de bit d'exécution ; un `.bat` ne doit pas être lu comme script hsh. Mécanisme (enfant du shell, nouvelle instance, flux du shell, instance déjà ouverte), extensions exactes et ordre face au binfmt par en-tête : dans le plan, à valider (consignes §5 : « pas tous décidés »). | S6 (effets) ; consignes §5 | Décidé (principe) ; mécanisme et règle : À valider |
| D13 (ELF) | Un **ELF** tapé au terminal va à Os-x86 par un **binfmt par en-tête** (seule une Feature peut en enregistrer un, D38), vers le Linux par défaut (D24). | S2, S6 | Décidé |
| 111 | Un **`.exe` MZ pur (DOS)** tapé au terminal va à Os-x86/FreeDOS par ce binfmt, sans BoxedWine ; un `.exe` MZ+PE va à BoxedWine. Réserve : reconnaissance du contenu à concilier avec les associations ; `.com` (sans en-tête) : au double-clic par BoxedWine qui confie à Os-x86 (consignes §5) ; au terminal, à trancher avec la règle contenu/extension. | 111 ; consignes §5 | Décidé, réserve |
| 72.2 | Reconnaissance DOS/Windows, console/graphique = **service central d'Os-x86** offert aux clients. En cas de contradiction contenu/extension, préférence au contenu, sous réserve d'exceptions à présenter. | consignes §5 | Décidé (principe) ; règle : À valider |
| 109, 154 | **Programme console** lancé par `./x.exe` : minimum V1 = une fenêtre console séparée suffit ; sortie dans le terminal courant et mode brut non exigés, pas interdits (109). **154 B : le terminal de Kazo affiche** la console (E14) ; Os-x86 n'ouvre aucune fenêtre. Fermer la console arrête le programme qu'elle attend (72.15, 147) ; programmes graphiques ou détachés : Ouvert. | 109 ; 154 B ; 147 | Décidé |
| D27 → consignes §4 | Mode brut du terminal et adaptateurs console : **V2** (intégration avancée). 119 : en V1, shell interactif avec des touches manquantes acceptées. | consignes §4 ; 119 | Décidé |
| 110 | **Vrai code de sortie** : souhaité en V1 si possible, **obligatoire en V2** ; voie technique (changements au shell, ADR « code de sortie d'une fenêtre lancée du shell ») à étudier ; lancement via BoxedWine conservé. Tant que l'ADR n'existe pas : 0, 1 ou 130 (fait Kazo), et cette limite est nommée. | 62 B ; 110 | Décidé, réserve |
| 119 | **Linux en ligne de commande, V1** : shell interactif dans le terminal Kazo, même avec des touches manquantes ; intégration plus complète ensuite. Non validés (119 A) : la syntaxe (`x86 shell <vm>`, `x86 run`), le passage par l'agent, le repli sans agent (console série), et « une session de shell compte comme une application qui garde sa VM en marche » : À valider. | 119 ; consignes §5 | Décidé, réserve |
| D60 (révisé) | Noms `x86` et `boxedwine`, sans alias `wine`. `boxedwine` est le **nom court d'une fenêtre**, pas une commande : le shell le cherche après les commandes ; une app Terminal qui déclare `boxedwine` le cacherait ; seul `x86` sera protégé par la priorité des commandes de Feature. 72.8 (`boxedwine setup.exe` = `./setup.exe` ou double-clic ?) : Ouvert. | S5, S6 ; consignes §5 | Décidé ; 72.8 : Ouvert |
| Documents au terminal | Un document lancé au terminal suit l'association déjà choisie, sans demander ; dans une VM, l'ouverture suit l'invité ; depuis une app Kazo, une fenêtre Kazo. Sans gestionnaire : terminal et Fichiers proposent une installation par les formats déclarés dans le Store (sans nom codé en dur). Cas ambigus : dans le plan. | consignes §5 ; D58 | Décidé |
| 139 | **L'invité gère ses liens et ses documents** avec ses propres associations (pas d'intégration vers Kazo) ; un lien lancé depuis le terminal Kazo s'ouvre dans le navigateur Kazo. Remplace 139 A. | 139 | Décidé |
| D61.5, consignes §2 | BoxedWine ne pose jamais de question dans le terminal ; aucun consentement de sécurité ne se saisit dans la console d'un programme. | S5 ; consignes | Décidé |

## D. Bouteilles, identité des programmes et permissions

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D14, D19, D22 | Chaque programme lancé depuis Kazo est traité comme une application distincte, avec un **système de permissions propre à Os-x86**. En V1, permission par programme pour le **réseau** seulement ; une VM complète = une identité. C'est le programme lancé (dans la VM) qui déclenche la fenêtre ; les apps Kazo restent sous les permissions de Kazo. | S2, S3 | Décidé |
| D20 | **Une bouteille (préfixe Wine) par programme** lancé depuis Kazo, avec son espace réseau et son adresse IP, et son propre compte dans l'invité (D22). Ce qu'elle installe et lance partage son identité. Fait Wine : un `wineserver` par bouteille ouvre toutes les sockets, donc une décision réseau par bouteille, jamais par programme à l'intérieur. Base commune en copie-sur-écriture (fichiers ou blocs : Conditionnel, E2). | S3 ; vérification 134 | Décidé |
| 144, 134, 124.7 | **Règle générale de V1 : tout ce qui s'exécute dans une bouteille a les droits de cette bouteille** (programme lancé par un programme, commande interne de BoxedWine, console). Permissions fines (par exécutable) en V2 au plus tôt. La restriction « rien en V1 si l'héritage est reporté » (124.7, 134 A) est écartée. | 134 B, 144 A, 124.7 | Décidé |
| 145 | **Deux opérations distinctes** dans l'interface : « exécuter dans la bouteille » (commandes internes de BoxedWine : lancer, récupérer un résultat) et « ouvrir la console » interactive de l'usager, **que l'app ne peut ni lire ni écrire** ; droits de la bouteille dans les deux cas. Détails de R134.2 (arguments structurés, durée bornée, journal, avertissement affiché) : À valider. | 145 A | Décidé ; détails : À valider |
| 147 | Si la console attend le programme, la fermer termine le programme ; programmes graphiques ou détachés : Ouvert. | 147 | Décidé, réserve |
| 148 | **Même environnement et même résultat quel que soit le point de lancement** (Kazo ou invité) pour un programme déjà présent dans une bouteille ; il garde les permissions de sa bouteille. Identification exacte : à spécifier avec 108 et 146 (E12). | 148 | Décidé, réserve |
| D50 (corrigé), D41 | **Même programme, même bouteille compatible, quelle que soit l'app qui le lance** ; l'app créatrice n'est plus un critère (change D50). L'app qui demande l'exécution ne choisit pas la bouteille, ne gère pas son contenu, n'obtient aucun accès aux autres bouteilles. La sélection et la réutilisation relèvent de l'environnement d'exécution ; répartition exacte entre Os-x86 et la logique Wine de BoxedWine : À valider (consignes §6). Sélecteur de bouteilles d'Os-x86 seulement sur demande, avec avertissement ; l'API n'écrit jamais d'exécutable dans une bouteille. Limite acceptée : BoxedWine peut lancer des commandes dans ses bouteilles (145). | S4, S5 ; consignes §6, §19.3 | Décidé |
| Identité (E12) | Le journal retient : Windows = usager, sous-système, bouteille, chemin et empreinte SHA-256 du `.exe` d'origine ; Linux (D46) = usager, sous-système, chemin, empreinte ; changement d'empreinte = réexamen. **Règle proposée par E12** : « chemin dans la bouteille d'abord, clé de signature ensuite, empreinte toujours, sélecteur D41 en repli » ; pas de chaîne X.509 en V1 (.NET WASM ne vérifie pas Authenticode ; `SubtleCrypto` + ~2 500 lignes sinon). | D20, D46 ; consignes §6 ; E12 | Décidé (principe) ; règle E12 : À valider |
| D21, 108 | Droit effectif = **intersection** (Os-x86 ∩ appelant ∩ sous-système ∩ décision de l'usager ∩ relais) ; la fenêtre ne fait que restreindre. **108 : une autre app qui lance un programme autorisé « toujours » ne redemande pas, et il n'y a pas de plafond fondé sur le lanceur** (le plafond de 108 A est écarté ; l'origine du lancement ne change ni la bouteille ni les permissions, consignes §6). À réconcilier avec D21, D43, D52 : voir « Remplacé » ci-dessous et la proposition en section O. | S3 ; 108 ; consignes §6 | Décidé, réserve |
| D23, D45, D48 | Fenêtre de permission : fenêtre de bureau d'Os-x86, une par programme ; Autoriser/Refuser ; une fois, jusqu'à la fin du programme, ou toujours ; toute destination, ou cet hôte et ce port ; règles imposées par l'admin ; révocables dans la page des règles ; le réseau simulé ne demande jamais ; **aucun délai** imposé par Os-x86 (mais un programme ou une connexion peut abandonner avant). **Sans session : pas en V1** ; tout s'arrête à la déconnexion et les demandes en attente sont refusées ; la règle « refus + notification au propriétaire + case “même sans session” » attend un ADR « linger ». Notifications avec identifiant opaque seulement. | S3, S4 ; consignes §6 | Décidé |
| 107, 146 | Fichiers : **même bouteille, mêmes permissions**, mais « ça ne veut pas dire que tous les programmes voient tous les dossiers de la VM » (107). **Chaque bouteille accède aux dossiers pour lesquels elle a une permission ; deux bouteilles partagent un dossier seulement si toutes deux y sont autorisées** (146). Ni 107 A ni 146 A tels quels ; D22 (« un `.exe` voit son dossier et ses arguments ») est remplacée par ce principe. Mécanisme d'isolation par bouteille dans une VM commune : à concevoir (E6 : usagers invités + 0700 + `nosuid` tiennent ; E8 : un partage = un périphérique 9p). | 107, 146 ; E6, E8 | Décidé, réserve ; mécanisme : À valider |
| 124.8 | Ouvrir un dossier de Kazo à une VM au-delà de ses permissions : fenêtre d'Os-x86 de demande d'autorisation. | 124.8 | Décidé |
| D32, D42, 105, 140, 141 | Permissions déclarées par Os-x86 (ADR « permissions déclarées par un fournisseur ») : « lancer des programmes » (normale) ; « gérer les sous-systèmes » (dangereuse ; portée face à D32 : lecture (a)/(b) de la vérification 105, Ouvert) ; et pour « ses propres VM » (**105 : usager standard = ses VM ; admin = contrôle total**) : **141 = une permission qui couvre tout, plus une permission par cas** (courant ; suppression/export). Noms et niveaux : proposition `x86.vm.manage`, `x86.vm.use`, `x86.vm.destroy` (série 10, sans question) — À valider. **140 B** : administration graphique dès le premier jalon (Paramètres : sections VM et utilisateurs) avec le rôle de 156. | S3, S4 ; 105 C, 140 B, 141, 156 | Décidé ; noms : À valider ; mécanisme de l'admin graphique (rôle de 156 seul, ou plus une authentification par opération) : Ouvert |
| 143 | Permissions dangereuses d'une Feature : **mêmes règles que Kazo** (approbation admin, refus par usager, jamais d'attribution silencieuse) ; l'ADR corrige le bogue du semeur (issue #432). | 143 | Décidé |
| 123, 72.9, D43 (révisé) | Accord admin pour BoxedWine : **à l'installation de BoxedWine** ; à l'usage, opérations limitées aux droits de l'usager connecté ; changements Kazo nécessaires à spécifier (Kazo demande aujourd'hui au premier lancement, ADR 0049). Condition de 72.9 (consignes §3) : cet accord machine ne donne jamais à un usager standard un moyen de contourner ses droits ni de modifier des réglages réservés à l'admin ; chaque opération sensible est revérifiée (124). BoxedWine déclare `network.real.access` et les permissions de fichiers qu'il transmet (D43). | 123 ; 72.9 ; D43 | Décidé, réserve |
| 124.x | Opérations revérifiées par Os-x86 à chaque appel : 124.1 (VM commune : admin seul règle, supprime, partage ; démarrer/arrêter : usager admis, un à la fois ; restrictions par VM, certaines réservées aux admins) ; 124.2 (arrêt avec travail non enregistré : confirmation, selon ce que l'invité laisse détecter) ; 124.3 (suppression disque/bouteille/point : confirmation par défaut, option explicite au terminal, par exemple `--force`, droits vérifiés) ; 124.4 (import : image partagée = admin, son disque = usager) ; 124.5 (limites, règles imposées, refus machine : admin seul) ; 124.6 (permission réseau jamais par l'API) ; 124.8 ; 124.9 (export : confirmation, sans `--force`) ; 124.10 (app par défaut : confirmation de Kazo, D61.10). Réserves : 124.2 dépend de ce que l'invité laisse détecter ; `--force` n'est qu'un exemple de syntaxe. | 124.1 à 124.10 | Décidé, réserve |
| 125 / 142 | **Mode invité** : quand aucun autre compte n'existe, l'invité est un admin (ADR 0187) et fait les opérations admin d'Os-x86 avec les confirmations de Kazo ; 125 A écartée pour ce cas. | 125, 142 | Décidé |
| 122 | Limites et veto de l'admin : **page admin d'Os-x86 dans Paramètres → Fonctionnalités** plus une commande équivalente au terminal. Syntaxe de la commande et contrôles d'élévation (`sudo x86 …`, fenêtre de l'OS) : À valider. | 122 | Décidé, réserve |
| D61.1, D61.2 | Décisions de permissions et bibliothèque stockées **par usager, dans le stockage réservé d'Os-x86** (OPFS), jamais dans un fichier modifiable par toute app ; ce n'est pas une isolation face à du JavaScript malveillant de la même origine (ADR 0182) : à dire dans la spécification (modèle de menace, ADR proposé). Aucune opération « autoriser » dans l'API. | S5 ; consignes §6 | Décidé ; modèle de menace : À valider |
| D35, D61.3 | Pas de root pour les programmes et les commandes de l'API ; l'agent peut avoir les privilèges nécessaires ; arguments structurés, jamais une ligne de shell. `sudo` dans un Linux personnel = réglage propre à ce sous-système, sans droit sur Kazo. | S3, S5 ; consignes §6 | Décidé |
| 117 | Contre l'imitation des fenêtres de confiance : **marque de Kazo sur les fenêtres des programmes invités** (117 A, « première méthode ») ; position, forme, plein écran à spécifier. | 117 | Décidé, réserve |
| Garanties E6 (R134.5) | Prouvées dans la maquette : espace réseau par bouteille et impossibilité d'en changer (`user.max_user_namespaces=0`), pas d'accès aux autres bouteilles (0700, `nosuid`, aucun setuid), canal de l'agent réservé à root, programme sans décision sans réseau. À écrire comme règles explicites : UDP 53 et IPv6 bloqués. Non testé : console avancée réelle (E7), entrant, écriture sur le canal agent. | E6 | À valider |

## E. VM : propriété, cycle de vie, plusieurs VM, usagers

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| 102 | VM Programmes (Windows, Linux, FreeDOS) : **V1 = environnements partagés, utilisés à tour de rôle ; V2 = base commune et différentiel personnel persistant**. Change D11 et D24 (« par usager ») pour V1. Les bouteilles restent personnelles (D20, D22) ; pas d'installation Windows « pour tous » en V1 (D61.11). Vérifier (E7) ce qu'un programme écrit hors de son compte invité. | 102 ; consignes §7 | Décidé ; vérification : Conditionnel (E7) |
| 103 | **VM complète : privée ou accessible aux autres usagers Kazo, au choix** ; l'invité gère ses comptes ; un usager à la fois ; la VM privée est protégée effectivement. | 103 | Décidé |
| Un usager à la fois | Une même VM n'est utilisée que par un usager à la fois ; comptes distincts dans l'invité ; dossiers personnels reliés aux fichiers Kazo et séparés ; la liste des programmes d'un autre usager n'est pas révélée ; bibliothèque, récents, profils par usager. Deux VM n'écrivent jamais dans le même préfixe ou disque actif (verrou, E11). | consignes §7 | Décidé ; verrou : Conditionnel (E11) |
| Conteneurs toujours actifs | VM toujours actives gérées par l'admin : **partagées en V1, attribution personnelle en V2**. | Sujet sans numéro, 2026-11-11 | Décidé |
| 155, 124.1 | **Attribution d'une VM à un usager précis : V2.** En V1, les VM communes sont visibles des usagers admis, avec les restrictions par VM de 124.1 (admin seul). | 155 B | Décidé |
| Périphériques | Seuls les admins gèrent les périphériques des VM partagées ; l'usager gère ceux de la VM qu'il a créée. Portée et opérations à spécifier ; ne vaut pas validation de 124.3 ni des sous-points ouverts. | Sujet sans numéro, 2026-11-11 | Décidé, réserve |
| D59 C, D35 (révisé), 66, 67 | **Plusieurs VM à la fois si les ressources le permettent** (remplace « une VM en marche par session » de D35). L'usager voit les VM actives et peut : fermer une autre VM, ouvrir dans une VM compatible, créer une nouvelle VM. Quand poser la question, critères de compatibilité, limites de ressources : à proposer à partir des essais (pas les sept sous-points de 66 A). Même données et mêmes VM pour `x86` et l'API. | S5 ; consignes §7 | Décidé ; 66, 67 (détails) : Ouvert |
| 72.6 (remplacé) | Une VM démarrée pour des applications **s'arrête dès que toutes ses applications sont fermées**, sans délai ; fermer une app n'arrête pas la VM si une autre l'utilise ; une **VM démarrée manuellement reste active jusqu'à arrêt manuel** ; l'arrêt n'efface rien. « Application » = programme lancé ; qu'une session de shell (119) ou une console (145) compte aussi, et le cas d'un service de fond : à définir dans le plan. | consignes §7 ; 119, 145 | Décidé ; définition d'« application » : À valider |
| Conserver ou recréer | Conserver l'environnement s'il est lent à créer et peu coûteux à garder ; sinon recréer ; mesuré (E2 : 1 Gio écrit en 1 s en OPFS ; E1 : `wineboot` 7 min → 152 : bouteille gabarit copiée). Jamais une autorisation d'effacer programmes et données à la fermeture. | consignes §7 ; 152 | Décidé |
| 131 | Suppression d'un compte Kazo : **deux choix distincts** (dossiers personnels ; images et VM personnelles), VM partagées à part. Rangement par identifiant d'usager (GUID) plutôt que par nom (R105.5) et petit ADR « événement de suppression de compte pour les Features » : À valider. | 131 | Décidé ; GUID et ADR : À valider |
| D54 | Sous-système Programmes Windows : à Os-x86, géré pour BoxedWine ; suppression de bouteilles et révocation possibles ; désinstaller BoxedWine ou Os-x86 garde les données. Les « réglages verrouillés » de 54 A ne valent plus depuis que BoxedWine règle les VM (63 A, 120). | S5 ; 120 | Décidé (révisé) |

## F. Réglages, profils, images, mises à jour

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| 120 | **Les options propres à Wine vivent dans Os-x86, avec les réglages de la VM.** La réponse porte sur les options Wine ; elle ne valide pas chaque réglage ni le modèle de stockage opaque. Le reste de 120 A (BoxedWine garde seulement ses préférences d'interface : bibliothèque, récents, apparence, tutoriel ; réglages machine dans la page admin de 122 ; `x86` et BoxedWine modifient le même réglage au même endroit, ce dernier point étant déjà exigé par les consignes §7) reste à valider. | 120 ; consignes §2, §7 | Décidé, réserve ; reste de 120 A : À valider |
| 121 | **Profil partageable = la recette, pas le contenu** : réglages de la VM et liste de programmes ; ni disques, ni bouteilles, ni autorisations. Format, mécanisme d'import/export et sens exact du modèle « Linux avec Wine » de §8 : non tranchés (ma lecture : « Linux avec Wine » = la VM Programmes Windows de D24, et les modèles prêts de §8 sont des profils livrés par Os-x86 ; À valider). | 121 ; consignes §8 | Décidé, réserve |
| 126 | Consentement au téléchargement d'une image : **par usager, images officielles seulement** ; image présente = aucun téléchargement ; « ne plus demander » réactivable dans les réglages ; ne vaut ni pour une image apportée ni pour une mise à jour. Composant qui télécharge (Os-x86 par D61.7, BoxedWine qui propose l'image par D56 ; consignes §8 demandent de réconcilier) : À valider. | 126 | Décidé |
| 127 | **App qui exige une image récente : mettre à jour ou annuler** ; mises à jour ordinaires reportables (« maintenant » ou « à la fermeture de l'application »). Cas « le moteur ne démarre plus l'ancienne image » : non présenté → Ouvert. Point de restauration avant mise à jour, retour arrière, « attendre un état sûr » sans arrêter les autres apps de la VM : à proposer. | 127 ; consignes §10 ; D56 | Décidé, réserve |
| 128, 124.4 | Import : **libre pour l'image ou le disque personnel** ; **admin pour l'image partagée**. Réserve : vérifier que l'image personnelle reste dans les droits de l'usager. Les détails de 128 A (sans agent, capacités réduites affichées, quotas, page de 122, intégrité) : À valider. | 128 | Décidé, réserve |
| D44, D56 | Versions : l'interface donne l'identifiant et la version de l'image et de Wine ; une app déclare la version minimale dont elle a besoin ; Os-x86 documente les outils stables de chaque image ; états de veille abandonnés au changement de moteur. Retrouver un état utilisable après un échec : Décidé (§10, §19.10) ; mécanisme de retour arrière et ses limites : À valider. À réconcilier avec 127 dans le plan. | S4, S5 ; consignes §10 | Décidé ; retour arrière : À valider |
| Images (§8) | Images hors du noyau et du téléchargement de base ; intégrité vérifiée avant usage (manifeste SHA-256 par morceau : proposition, E3 à faire) ; fonctionnement hors ligne avec une image présente ; sources des composants copyleft offertes au même endroit que l'image (GPL-2 noyau et BusyBox, LGPL Wine). Hébergement : GitHub Releases impossible (pas de CORS) ; R2, serveur Kazo ou équivalent (E3 à faire). | consignes §8 ; vérification | Décidé ; hébergement : Conditionnel (E3) |

## G. Stockage, synchronisation, sauvegardes

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| 149 | **Images disque dans l'OPFS** (`createSyncAccessHandle` depuis le worker) comme voie principale ; **petites images (ex. Alpine) possibles dans IndexedDB** ; seuil à préciser (proposition : moins de 64 Mio, À valider). Base partagée et couches de différences : à essayer (E2 bis). E2 : OPFS 55 à 185 fois plus rapide par bloc ; quota 10 Gio ; `persist()` refusé sans geste. Les Fichiers de Kazo refusent tout fichier de plus de 16 Mio : aucun disque n'y entre. | 149 ; E2 | Décidé ; seuil et couches : À valider / Conditionnel |
| Cohérence d'un disque | Écriture durable, reprise après incident, interruptions, quota, éviction (Safari efface après 7 jours sans interaction, E10 ; Chrome Memory Saver sans événement) : à couvrir dans le plan ; signaler un échec de stockage même sans quota admin (V2). Un seul écrivain par disque (Web Locks, E11). | consignes §9 ; E10 ; E11 | Décidé (exigences) ; mécanismes : Conditionnel (E11) |
| 114 | **Synchronisation entre appareils : V2** pour les disques, la bibliothèque et les décisions ; mécanismes non choisis. En V1, pas de synchronisation des disques ; les fichiers du dossier personnel se synchronisent comme d'habitude. « Un disque ne s'ouvre qu'une fois » (verrou) : proposition, E11. | 114 | Décidé |
| Conflits | **Toujours demander** à l'usager ; jamais d'écrasement automatique ; versions et dates affichées, la plus récente proposée ; pas de « toujours appliquer ». C'est déjà la règle de Kazo pour les fichiers (ADR 0101 ; « le plus récent l'emporte » n'a jamais existé). Pour un disque entier (V2) : conflit par VM, mécanismes à proposer sans retirer le choix. | consignes §9 ; vérification | Décidé |
| 115 | **Sauvegarde demandée par l'usager** : objectif = retrouver l'environnement complet dans un seul fichier ; VM incluses par défaut, exclusion possible ; export d'une seule bouteille ; mécanisme et adaptations à proposer après E15 (la sauvegarde de Kazo est admin, machine entière, sans fenêtre ; téléchargement en flux à prévoir). Sauvegarde cohérente pendant la marche : comparer point de restauration puis export contre arrêt (E15). Image de base incluse ou non : Ouvert après E15. | 115 ; consignes §10 ; E15 | Décidé, réserve ; mécanisme : Conditionnel (E15) |
| D34, D48 | Points de restauration **disque** durables ; états de veille jetables, liés à la version de v86, dans l'OPFS d'Os-x86 ; veille/reprise usager en V2 ; état gabarit évalué en V2 (152). Périphériques = profil au prochain démarrage ; CD et disquette à chaud ; disques importés et exportés par téléversement et téléchargement. | S3, S4 ; consignes §4, §10 ; 152 | Décidé |

## H. Fichiers partagés et disque interne

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| §11 | Les programmes invités accèdent, selon les permissions de leur bouteille (107, 146) : au dossier personnel Kazo, aux dossiers locaux autorisés au navigateur (`hostfs`), aux montages (Kazo n'a aucun montage distant en V1 : `hostfs` seulement). Droits conservés (lecture seule reste lecture seule) ; un montage ne crée pas d'accès plus large ; présentation comme lecteur réseau ou dossier partagé ; montage devenu inaccessible = erreur claire, jamais « dossier vide ». | consignes §11 ; vérification | Décidé |
| Traduction des chemins | **Traduction automatique des chemins Kazo** des dossiers partagés vers leur point de montage dans l'invité (ex. `/home/<usager>` → `D:`), sans nouveau droit ; montages et permissions à réconcilier. | Sujet sans numéro, 2026-11-11 | Décidé, réserve |
| E8 (règles) | Programmes installés sur le disque de la VM, jamais sur le partage (9p 15 fois plus lent sur les petits fichiers, #1451 reproduit) ; partage réservé aux documents ; serveur 9p d'Os-x86 durci (30 exigences : liste blanche de noms, pas de résolution de chemin côté hôte, refus de mknod/fifo/liens durs/setuid/xattr, toute exception → EIO borné, un partage = un périphérique 9p). | E8 | À valider |
| Disque interne depuis Kazo | Exploration du disque interne d'une VM par l'explorateur Kazo : **fortement souhaitée en V1, report V2 accepté si trop complexe** ; forme, permissions, comportement pendant les écritures à proposer (E7 : lecture seule à l'arrêt ; par l'agent en marche). Distinct de l'accès de la VM aux dossiers Kazo. Icône de provenance VM : laissée de côté. Bouteilles invisibles aux autres apps (D61.12). | consignes §11 ; E7 | Conditionnel (E7) |

## I. Réseau

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D14 (ajout), D20, D21 | Permission réseau par programme (= par bouteille) : fenêtre au premier appel non encore décidé ; décision mémorisée ; intersection avec les limites du relais (4 flux, 300 s, 80/443 ; UDP non transmis par v86). « Autoriser » ne lève pas les limites du relais. | S2, S3 ; vérification | Décidé |
| §6, §14 | Autoriser tout l'accès réseau d'une application ou d'une VM, garder des journaux, conserver les limites de l'admin ; viser hôte et port ; durées ; révocation ; éviter l'avalanche de confirmations. | consignes §6 | Décidé (exigences) |
| 116 | Réseau simulé : **viser plus complet à terme, version complète peut-être en V2** ; en V1, examiner ce qui est faisable et proposer des options avec leurs limites (après E6). Ni 116 A (passerelle HTTP) ni 116 B (séparé) acceptées. Fait : le réseau simulé de Kazo est HTTP seulement. Le réseau simulé ne demande pas les permissions du réseau réel. | 116 ; consignes §14 | Ouvert (options à proposer) |
| Comparaison §6 (E6) | Résultat E6 : une VM à deux bouteilles ≈ 1,45 Gio navigateur contre ≈ 2,4 Gio pour deux VM ; l'étiquette « bouteille » (adresse source distincte, sans NAT) est fiable jusqu'au relais ; deux décisions appliquées et attribuées. **Proposition** : VM partagée quand l'attribution est fiable ; VM séparées quand l'isolation d'affichage, de clavier ou de presse-papiers est requise ; décision, limites et journal au relais côté Kazo, nftables de l'invité en défense en profondeur ; `relay_url` fixé par la Feature, jamais exposé à l'invité (bloque `inbrowser`/`fetch`). | E6 | À valider |
| Limites exposées | L'isolation réseau n'est pas une isolation totale : affichage, clavier et presse-papiers partagés dans une VM commune (D61.9, E4, E6). À dire dans la spécification. | consignes §6 ; E6 | Décidé |

## J. Fenêtres, processus, bibliothèque

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| D26, D55, 112 | Objectif confirmé : **une fenêtre Kazo par vraie fenêtre Windows** (et Linux), avec titre, icône, état. **112 : commencer simple (une fenêtre Kazo par programme, D26), réévaluer à la fin de V1** selon l'architecture retenue ; agent dans la VM à examiner, pas choisi ; ni obligation en fin de V1 ni report ferme en V2. E4 : critère atteint avec des fenêtres X11 natives par composite (XComposite + XDamage), pas avec Wine (1x1) ; 18 points que Kazo devrait ajouter ; « Kazo devrait devenir le gestionnaire de fenêtres de l'invité » est la conclusion de l'essai, À valider. | S3, S5 ; consignes §12 ; 112 ; E4 | Décidé ; calendrier : Conditionnel |
| §12 | Icône générique si aucune icône exploitable ; une fenêtre secondaire fermée ne tue pas l'application ; fermer la console arrête son programme ; plantage du composant propriétaire sans tuer les autres apps de la VM (D61.4, 72.15) ; fenêtre hors écran ramenée (déjà fait par Kazo). Apps graphiques Linux dans des fenêtres Kazo, même lancées au terminal, affichées par BoxedWine. | consignes §12 | Décidé |
| Processus | Programmes invités visibles dans le modèle de processus de Kazo ; arrêter A sans arrêter B ni la VM ; par l'agent ; « un lancement = un processus Kazo » (D35) à réconcilier avec les enfants et le multifenêtre ; `StartChild` ne sert jamais de base (issue #431) ; limites par processus de Kazo (ADR 0125) à exempter ou attribuer à la VM. Protocole : E7. | consignes §12 ; E7 | Décidé (exigences) ; mécanisme : Conditionnel (E7) |
| D57, 70, 71, 130 | Bibliothèque : **l'usager ajoute lui-même** les programmes ; tutoriel obligatoire au premier lancement ; entrée du menu Démarrer obligatoire, raccourci de bureau facultatif ; **BoxedWine dessine lui-même sa marque** en bas à droite de l'icône (70 A refusée) ; ADR Kazo pour l'icône de fenêtre et de raccourci ; chemins, `.lnk`, icônes et noms de l'invité = données non fiables. **130 : programmes trouvés dans les bouteilles = facultatif pour V1, V2 possible.** Non acceptés : limite de 64 entrées, identifiants opaques, nettoyage de 71, winemenubuilder (72.14). | S5 ; consignes §13 ; 130 | Décidé |
| 129 | Retirer une entrée de bibliothèque **laisse le programme installé** ; avant de supprimer une VM, avertir que les raccourcis seront supprimés ; comment désinstaller ensuite : Ouvert. | 129 | Décidé, réserve |
| Marque anti-imitation | Voir 117 (section D). | 117 | Décidé, réserve |

## K. Son, presse-papiers, souris et clavier

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| 133 | Son : **volume et muet par VM en V1 si les essais le permettent** (E5 : oui, pont worker → AudioWorklet sans trou sous charge ; `snd-sb16` dans `linux-virt` ; correctif v86 #1661 à embarquer : proposition, À valider) ; par programme plus tard (PipeWire double le CPU) ; vérifier la même chose pour les apps Kazo (souhaitable). v86 n'a que la SB16 PCM. | 133 ; E5 | Décidé ; ADR son Kazo : À valider |
| 74, 136 | Presse-papiers : **texte simple en V1 dans les deux sens** quand la fenêtre de l'invité est active ; images et formats riches en V2 ; pas d'isolation supplémentaire entre apps autorisées, mais pas de partage entre usagers ni de synchro en arrière-plan. **136 : presse-papiers réel en V1 si E9 réussit, sinon V2.** Transport, taille, articulation avec les permissions : dans le plan. | consignes §15 ; 136 | Décidé ; 136 : Conditionnel (E9) |
| 76, 132 | Capture de la souris permise ; **réglage par VM, exception par application** ; touche de libération configurable, toujours disponible, **aucune touche précise choisie** ; pendant la capture, priorité à l'application sur les raccourcis Kazo, dans les limites du navigateur (Ctrl+W, Ctrl+T hors plein écran ; Keyboard Lock Chromium seulement, Firefox/Safari par le plein écran, E10 : bureau seulement). ADR Kazo « tenir le clavier » (proposition, À valider). | consignes §15 ; 132 ; E10 | Décidé, réserve |

## L. Journal, tests, licences, conventions

| Réf. | Décision retenue | Source | Statut |
|---|---|---|---|
| 137 | Journal d'événements : **événements personnels visibles par l'usager et les admins ; événements système dans le journal général** ; mise à jour, permission accordée, conflit résolu, rattachés à la VM ou au programme. Demande un ADR Kazo « événements par usager ». | 137 ; consignes §16 | Décidé ; ADR : À valider |
| 138, §17 | Tests en V1 ; suites longues non lancées pour tout changement de Kazo ; déclencheurs sélectifs ; **PR dans Kazo-ca/Os au moment de l'implémentation** (138). | 138 ; consignes §17 | Décidé |
| §17 objectifs | 8 Go minimum visé (sans garantie de plusieurs VM) ; démarrage < 10 s très bon, < 30 s acceptable, à mesurer avec matériel, navigateur et cache ; fonctions réservées à Chrome/Edge affichées comme telles sans abandonner Firefox et Safari. E10 : plancher Chrome/Edge 108, Firefox 111, Safari 16.4 ; pas d'exigence SharedArrayBuffer (Safari sans COEP `credentialless`) ; mobile : VM de 256 Mio plein écran, à valider. E1 : démarrage à froid 62 s, hors cible ; 152 accepte le démarrage à froid en V1 (temps à afficher), et §17 garde l'obligation d'examiner les optimisations au-delà de 10 s. | consignes §17 ; E1, E10 | Décidé ; conséquences E10 : À valider |
| D8, D18, D47, §18 | Kazo garde CC BY-NC ; chaque app déclare sa licence ; **Os-x86 MIT** ; moteurs copyleft en paquets séparés ; **BoxedWine garde son nom et passe sous MIT**, sans code GPL de Boxedwine ; correction de la description « HackerOs » et de la note GPL-2 de Kazo au moment autorisé ; sources offertes pour ce qui est distribué (images) ; médias Windows fournis par l'usager. | S1, S2, S4 ; consignes §18 | Décidé |
| C43, jeux | Conventions de Kazo (ADR avant le code, documentation FR avec miroir EN, API, changelog, `features.md`). Positionnement : écosystème, pas un jeu, **sans exclure les jeux** parmi les logiciels utilisables ; divergence avec l'ADR 0135 (qui vaut dans Kazo-ca/Os seulement) à signaler ; formulation pour le dépôt Os-x86 et sa fiche à proposer. | consignes §18 | Décidé ; formulation : À valider |
| 135, 157 | Trois issues ouvertes dans Kazo-ca/Os le 2026-11-11 : #431 (`StartChild` sans capacité), #432 (semeur accorde tout identifiant hors catalogue), #433 (fichier de refus écrasable). Aucune PR. | 135, 157 B | Fait |
| 159 | Prénom remplacé par « l'usager » dans la copie du dépôt. | 159 A | Fait |
| Méthode | Numéros conservés ; 77 à 100 réservés à ChatGPT ; mes prochaines questions commencent à 160 ; aucune acceptation par défaut des séries 7 et suivantes ; question avant d'implémenter si une décision bloque ; pas de code de production avant l'approbation du plan ; pas de publication ni de fusion. | consignes §1, §18 ; règle du 2026-11-10 | Décidé |

## M. Décisions remplacées (traçabilité)

| Ancienne | Remplacée par | Ce qui change |
|---|---|---|
| D7 (Os-x86 seul exécute les .exe au terminal) | 58 A puce 2, 62 B, 64, 111 | Windows exige BoxedWine ; ELF et DOS restent à Os-x86. |
| D11, 49 A (app Sous-systèmes) | 63 A, consignes §2 | Plus d'app dans Os-x86 ; BoxedWine est l'interface de tous les environnements. |
| D11, D24 (sous-systèmes par usager) | 102, 103 | V1 : environnements partagés à tour de rôle ; VM complète privée ou partagée au choix ; bouteilles toujours personnelles. |
| D13 (binfmt générique pour MZ, PE et ELF ; Ctrl+C transmis ; code de sortie rendu) | 62 B, 110, 111 | PE par l'association ; MZ pur et ELF par le binfmt ; code de sortie : V1 si possible, obligatoire en V2 (110) ; Ctrl+C : à préciser (consignes §5). |
| D22 (« un `.exe` voit son dossier et ses arguments ») | 107, 146 | Fichiers par bouteille, selon ses permissions. |
| D25 (repli VM/DOS) | consignes §4, 150 B, 153 | Jalon 0 console ; Wine graphique visé en fin de V1, sinon début de V2. |
| D26 (une fenêtre par programme en V1, intégré plus tard) | consignes §12, 112 | Objectif fenêtre par fenêtre ; V1 commence simple, réévaluation en fin de V1. |
| D27 (mode brut en V1) | consignes §4, 109, 119 | V2 ; en V1, shell interactif avec des touches manquantes acceptées. |
| D28 (DOS en mode texte dans le terminal, acquis) | consignes §5, 111, 154 | Souhaité « si raisonnable » ; route directe Os-x86 confirmée ; le terminal affiche. |
| D32 (deux permissions) | 63 A, 105, 141, 143, 156 | Trois permissions au moins, dont une par cas pour « ses VM » ; permissions dangereuses déclarées ; un rôle Kazo. |
| D35 (« une VM en marche par session ») | 59 C, consignes §7 | Plusieurs VM si les ressources le permettent. Le reste de D35 tient. |
| D43 (accord admin à l'installation de BoxedWine) | 123 | Confirmé « à l'installation », mais avec des changements Kazo à spécifier (Kazo demande au premier lancement). |
| D50 (app créatrice comme critère de bouteille) | consignes §6, §19.3 ; 148 | Même programme, même bouteille, quelle que soit l'app. |
| D52 (un « toujours » pris pour toi ne couvre pas une autre app ; la fenêtre redemande) | 108 | Pas de nouvelle demande, pas de plafond fondé sur le lanceur. « L'OS marque l'expéditeur » (petit ADR) tient pour le journal. |
| D54 A (réglages verrouillés) | 63 A, 120 | BoxedWine règle les VM, avec la permission de 141. |
| D58 A puce 3 (`x86` refuse un shell dans Programmes Windows) | 134, 144, 145 | Console avancée en V1, droits de la bouteille. |
| D60 A (`boxedwine` commande protégée) | consignes §5 | `boxedwine` = nom court de fenêtre ; seul `x86` est protégé. |
| 63 A puce 4 (Linux, DOS, VM complètes par `x86` seulement) | consignes §2, 104 | BoxedWine gère tout ; `x86` fait pareil en ligne de commande. |
| 63 A (fenêtres simples d'affichage d'Os-x86) | 106, 154 B | Os-x86 n'ouvre aucune fenêtre de VM ; terminal de Kazo pour la console et DOS texte. |
| 64 (BoxedWine affiche les programmes Windows seulement) | consignes §12 | Aussi les apps graphiques Linux. |
| 113 (démarrage à froid) et 151 A (état gabarit) en tension | 152 A | Froid en V1 ; bouteille gabarit en fichiers ; état gabarit évalué en V2. |
| 72.6 (arrêt après quelques minutes) | consignes §7 | Arrêt immédiat ; VM manuelle gardée. |
| 72.11 (image téléchargée au premier programme) | consignes §8, 126 | Demander avant, « ne plus demander » par usager. |
| 125 A (admin refusé en mode invité) | 142 | L'invité est admin (ADR 0187). |
| 139 A (liens ouverts dans Kazo après confirmation) | 139 | L'invité gère ses liens et documents. |
| 140 A (`sudo x86` seulement en V1) | 140 B, 156 | Administration graphique dès le premier jalon, avec un rôle Kazo. |
| 141 A (une seule permission) | 141 | Une permission globale plus une par cas. |
| 134 A, 124.7 (rien dans une bouteille en V1) | 134 B, 144, 145 | Console et commandes internes en V1, droits de la bouteille. |
| C22 (sauvegarde sans les VM) | consignes §10, 115 | VM incluses par défaut dans la sauvegarde demandée par l'usager (sa forme, export personnel ou autre, n'est pas validée). |
| C31 (VM hors du réseau simulé) | consignes §14, 116 | Chaque VM un hôte, à terme ; V1 à proposer. |

## N. Ce qui reste ouvert (ta réponse requise)

Liste courte, point 10 de §20. Chaque point sera posé avec contexte et options dans la série 11 (à partir de 160), ou dans le plan là où il est dit.

1. **Mécanisme du contrôle total de l'admin** : 140 B et 156 ont tranché l'administration graphique dès le premier jalon avec un rôle Kazo (la lecture (a) « `sudo x86` seulement » de R105.2 est remplacée). Reste à dire si le rôle suffit, ou s'il faut en plus une authentification d'admin par l'OS à chaque opération (réserve de 140), et ce que devient « gérer les sous-systèmes » (D32) face au rôle.
2. **Réseau simulé en V1** (116) : options et limites à proposer après E6 ; la version complète peut-être en V2.
3. **Règle contenu/extension du shell** (72.2, 111) : `.com` au terminal, `.bat`, `.cmd`, `.msi`, sans extension, erreurs de format ; ordre face au binfmt.
4. **72.8** : `boxedwine setup.exe` = `./setup.exe` ou double-clic.
5. **66, 67** : quand poser la question « une autre VM tourne » ; critères de compatibilité ; limites de ressources ; ce que « créer une nouvelle VM » crée (profil de 121).
6. **Définition d'« application »** pour l'arrêt d'une VM (fenêtre simple, service de fond).
7. **127** : cas où le moteur ne démarre plus l'ancienne image ; retour arrière ; « attendre un état sûr ».
8. **147** : fermeture de la console avec des programmes graphiques ou détachés.
9. **129** : comment désinstaller un programme après le retrait de son entrée.
10. **121** : format et import/export des profils.
11. **115** : image de base incluse ou non dans l'export (après E15) ; sauvegarde pendant la marche.
12. **Wine graphique** (153) : quand et comment juger « trop complexe » ; l'essai dédié avec un autre Wine est une proposition à valider.
13. **C18** : verdict E13 (vrai Windows en V2 avec essai de faisabilité) à valider.
14. **Identifiants** : identifiant d'app, points d'entrée, espace de noms C#, nom NuGet (dans le plan, avant publication).
15. **DOS graphique** : 111 dit « sans BoxedWine », et le texte de l'option 154 B disait « DOS graphique passe par BoxedWine comme Windows » ; ta réponse (« terminal de Kazo ») ne tranche que le mode texte. À confirmer.

## O. Propositions à valider (mes recommandations et résultats d'essais)

Rien ici n'est retenu. Chacune sera présentée dans le plan avec son impact, ou dans la série 11.

- **E1/E4** : V1 garde une fenêtre Kazo par programme (D26 tel quel) ; fenêtre par fenêtre en V1.x avec un plan à part ; composite XComposite + XDamage comme mécanisme cible ; 18 points à ajouter dans Kazo.
- **E2/149** : seuil de 64 Mio pour IndexedDB ; `flush()` périodique et à l'arrêt ; `persist()` après un geste ; éviction traitée comme risque réel (avertissement, export).
- **E5/133** : embarquer un v86 avec le correctif #1661 ; pont worker → AudioWorklet ; muet global côté page gratuit ; ADR Kazo « son ».
- **E6/116, §6** : VM partagée quand l'attribution est fiable ; décision, limites et journal au relais ; nftables en défense en profondeur ; `relay_url` fixé par la Feature ; UDP 53 et IPv6 bloqués par règle explicite.
- **E8/107** : programmes sur le disque de la VM, partage pour les documents ; 30 exigences du serveur 9p ; un partage = un périphérique 9p.
- **E10** : pas d'exigence SharedArrayBuffer ; VM mobile de 256 Mio plein écran ; fonctions marquées non prises en charge par navigateur.
- **E12** : règle d'identité « chemin dans la bouteille d'abord, clé de signature ensuite, empreinte toujours, sélecteur D41 en repli » ; E12 bis (code jetable) pour prouver `System.Formats.Asn1` + `SubtleCrypto` sous Blazor.
- **E13** : verdict C18 ; WineHQ i386 comme roue de secours et pour l'essai dédié de 153.
- **R105.x** : « gérer ses environnements » dangereuse (R105.1) ; permissions dangereuses déclarées (R105.3, rejoint 143) ; VM rangées par identifiant d'usager (GUID), jamais par nom (R105.5). R105.2 (lecture (a), `sudo x86` seulement) est remplacée par 140 B et 156.
- **R134.x** : « exécuter dans la bouteille » non interactive, arguments structurés, durée bornée, journalisée ; fenêtre de console avec bouteille, droits réseau et avertissement ; garanties E6/E7.
- **141** : noms `x86.vm.manage`, `x86.vm.use`, `x86.vm.destroy`.
- **131** : ADR « événement de suppression de compte pour les Features ».
- **137** : ADR « événements par usager ».
- **D61.1** : ADR « modèle de menace » (apps Kazo malveillantes et noyau invité compromis hors périmètre).
- **Jeux** : formulation « écosystème, jeux non exclus » pour le dépôt Os-x86 et sa fiche.
- **108 ↔ D21/D43** : proposition de réconciliation : le plafond de D21 reste l'intersection avec les permissions **déclarées** par l'app lanceuse (D43 : BoxedWine déclare `network.real.access`), ce qui n'est pas un plafond « fondé sur le lanceur » au sens de 108 (l'origine ne change pas la décision du programme) ; une app sans `network.real.access` ne peut pas lancer un programme qui en a besoin, et le refus est affiché, sans redemander. À valider.

## P. Études encore à faire avant le plan définitif

| Essai | Nourrit | État |
|---|---|---|
| E3 distribution des images | C13-C14, 126 | À faire (disque : 18,7 Go libres) |
| E7 agent et processus | §12, E-disque interne, 102 (écritures hors compte), garanties console | À faire |
| E9 presse-papiers réel | 136 | À faire |
| E11 verrou et cohérence des disques | §9, 114 | À faire |
| E14 DOS et console dans le terminal | 111, 154 | À faire |
| E15 sauvegarde cohérente et image de base | 115 | À faire |
| Essai dédié Wine graphique (autre Wine, WineHQ i386) | 153, 150 | À faire |
| E2 bis (base partagée, couches de différences) ; E12 bis | 149 ; E12 | À faire |

Faits : E1, E2, E4, E5, E6, E8 (mesurés), E10, E12, E13 (lecture). Rapports dans [../essais/](../essais/).
