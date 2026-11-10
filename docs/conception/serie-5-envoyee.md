C'est noté : pour 36 à 39, 41 à 46 et 48, je retiens mes recommandations ; pour 47 B, BoxedWine garde son nom et passe sous MIT. Tout est dans le journal des décisions.

**Ma lecture de ta réponse 40** (la question 49 te permet de la corriger)
- BoxedWine reçoit toute demande d'exécuter un programme Windows : `./setup.exe` dans le terminal, le double-clic, « Ouvrir dans un environnement Windows… », les raccourcis du bureau et les demandes des autres apps. Il pilote ensuite Wine dans la VM d'Os-x86 (29).
- Os-x86 fait tourner les VM, fournit l'image avec Wine et applique les permissions. Je te propose qu'il garde quand même quelques écrans, surtout ceux de sécurité (49).
- Ça remplace ma lecture de 29, où Os-x86 gardait le binfmt et toute la logique de Wine. Ça change aussi ta décision 7 : Os-x86 seul ne lance plus de `.exe` Windows, il faut BoxedWine (58). Et en 13, « la fonctionnalité » qui reçoit le fichier devient BoxedWine.

Quatre analyses en parallèle ont relu le journal et le code de Kazo, puis une critique les a croisées, et trois relecteurs ont relu le brouillon de ces questions. Leurs constats de sécurité sont dans le contexte des questions 50, 51, 52 et 57. Kazo n'a pas bougé depuis le commit cd89ee0, et l'issue #419 est toujours ouverte.

Je garde pour la série suivante l'écran, le terminal Linux, le presse-papiers, le son et la souris : ces questions dépendent de 49 et 55. Une correction en passant : v86 n'émule pas de carte son HDA, seulement la Sound Blaster 16 ; ma carte du 8 novembre se trompait.

---

## Cinquième série : le partage entre Os-x86 et BoxedWine

**49. Où s'arrête BoxedWine?**
> Tu as dit qu'Os-x86 n'est que le backend et que BoxedWine est son interface. Tu as aussi accepté qu'Os-x86 ait son app Sous-systèmes et sa commande à la manière de `wsl` (11), et que la fenêtre de permission, la page des règles et la révocation des décisions restent à Os-x86 (23, 45, 48). Le nom BoxedWine (47) parle de Wine.

- A) BoxedWine pour les programmes Windows ; Os-x86 garde ses écrans de VM et de sécurité ✨(Recommandé)
  - **BoxedWine :** tout ce qui touche un programme Windows. Il reçoit les demandes d'exécution (terminal, double-clic, « Ouvrir dans un environnement Windows… », raccourcis, autres apps) et gère la bibliothèque, les fenêtres et les consoles des programmes, et les réglages de Wine.
  - **Os-x86 :** les VM, l'app Sous-systèmes, sa commande, les programmes Linux et DOS, et les VM complètes. L'app Sous-systèmes ne lance plus de programmes Windows : elle renvoie à BoxedWine. Elle lance toujours les programmes Linux et DOS.
  - **Os-x86 garde aussi ses fenêtres de sécurité :** la fenêtre de permission réseau et la page des règles (le sélecteur de bouteilles : voir 50). Raison : l'app qui lance un programme ne doit jamais répondre elle-même à la question de permission de ce programme. Sinon, un bogue ou une mauvaise mise à jour de BoxedWine donnerait le réseau à tous les `.exe` sans te demander. C'est le « système de permission distinct de celui de l'OS » de ta réponse 14.
- B) Comme A, mais l'app Sous-systèmes et la commande vont aussi dans BoxedWine (ça change 11). Conséquence : Os-x86 installé seul n'a plus ni écran ni commande, et BoxedWine gère aussi Linux, DOS et les VM complètes.
- C) Toute l'interface dans BoxedWine, fenêtres de permission comprises (ça change 23, 45 et 48). Conséquence : BoxedWine répondrait lui-même aux questions de permission de ses programmes.
- D) Autre : (Spécifiez)

*Les questions 50 à 61 supposent 49 A. Si tu choisis autre chose, je reprendrai celles qui en dépendent.*

**50. Qui décide dans quelle bouteille tourne un fichier?**
> La permission réseau suit la bouteille (20). Pour Os-x86, une bouteille est une identité isolée générique : son usager dans l'invité, son réseau, son adresse IP. C'est BoxedWine qui en fait un préfixe Wine (29). Si BoxedWine choisissait lui-même la bouteille d'un nouveau fichier, un bogue ou une mauvaise mise à jour de sa part pourrait le glisser dans une bouteille déjà « toujours autorisée », sans aucune question. Ta réponse 41 parlait du sélecteur d'Os-x86. A ferme cette porte-là, mais pas celle des commandes que BoxedWine lance dans ses propres bouteilles (voir la limite).

- A) Os-x86 décide, BoxedWine demande ✨(Recommandé)
  - BoxedWine donne le chemin du fichier. Os-x86 le lit lui-même, avec les droits de BoxedWine plafonnés par ceux de l'app qui l'envoie (52), et calcule son empreinte SHA-256.
  - **Empreinte déjà connue :** sa bouteille, si c'est le même usager, le même sous-système et la même app qui a créé la bouteille. Ça précise 41, qui ne posait pas la condition de l'app.
  - **Empreinte inconnue :** une nouvelle bouteille.
  - **Seulement si tu le demandes** (« Ouvrir dans un environnement Windows… » ou l'option du terminal) : la bouteille que tu choisis dans le sélecteur d'Os-x86, avec l'avertissement de 41. Le sélecteur montre le nom que BoxedWine a donné à chaque bouteille, avec le fichier d'origine et l'empreinte calculés par Os-x86.
  - Os-x86 refuse d'écrire un exécutable (en-tête MZ ou ELF, ou extension exécutable de Windows) dans une bouteille par Os-x86.Interface. Ça précise 42. Ça ne ferme pas tout : voir la limite.
  - **Limite :** BoxedWine peut toujours lancer, avec les arguments qu'il veut, ce qui est déjà dans ses bouteilles, y compris un shell ou Wine lui-même. Par un bogue, il pourrait donc encore y créer ou y lancer un nouveau programme, par exemple un `.exe` du dossier visible (22), sans nouvelle question. La fenêtre de permission le dit : « Toujours » couvre aussi tout ce que BoxedWine lancera de lui-même dans cette bouteille. Un fichier qu'une autre app lui envoie redemande (52).
  - « Que l'usager a choisies par elle » (42) veut dire : choisies dans le sélecteur d'Os-x86 que l'app a ouvert.
- B) BoxedWine garde le lien entre empreintes et bouteilles, et son propre sélecteur ; Os-x86 calcule l'empreinte et applique la permission (ça change 41). Le prix : Os-x86 ne sait plus quand un nouveau fichier entre dans une bouteille déjà autorisée, et ne redemande rien dans ce cas.
- C) BoxedWine reçoit « gérer les sous-systèmes » (dangereuse) et décide de tout, même pour les bouteilles d'autres apps
- D) Autre : (Spécifiez)

**51. BoxedWine devient-il une Feature, et ses points d'entrée comptent-ils comme une seule app?**
> Par ta réponse 38, seule une Feature peut enregistrer un binfmt, le mécanisme qui envoie `./setup.exe` à la bonne app. BoxedWine a besoin d'une commande (terminal, code de sortie) et d'une fenêtre (double-clic, bibliothèque, fenêtres des programmes). Or une app ordinaire de Kazo est soit une commande, soit une fenêtre, soit un service, jamais deux à la fois.

- A) Oui, une Feature dont les points d'entrée comptent comme une seule app ✨(Recommandé)
  - BoxedWine s'installe au niveau système, par un admin, avec une dépendance obligatoire vers Os-x86. Ta réponse 43 demandait déjà l'approbation d'un admin.
  - Deux points d'entrée : une commande (binfmt, consoles, code de sortie) et une fenêtre à plusieurs instances (association `.exe`, entrée de menu, bibliothèque, fenêtres des programmes).
  - Pour ce qui lui appartient (bouteilles, données privées), tous les points d'entrée d'une Feature comptent comme une seule app. Ça vaut aussi pour Os-x86 et ça précise 37. Les permissions restent par point d'entrée (37).
- B) Oui, mais chaque point d'entrée compte comme une app séparée : la fenêtre de BoxedWine aurait besoin de « gérer les sous-systèmes » (dangereuse) pour toucher aux bouteilles créées par sa commande
- C) Non : deux apps ordinaires (une commande et une fenêtre), et on change la règle 38 pour qu'une app ordinaire puisse enregistrer un binfmt. Un admin doit quand même approuver l'accès réseau de BoxedWine (43).
- D) Autre : (Spécifiez)

**52. Une autre app veut lancer un `.exe` : par où passe-t-elle?**
> Aujourd'hui, toute app qui a la permission normale « lancer des apps » peut ouvrir BoxedWine avec un fichier, et BoxedWine ne sait pas qui l'envoie. Le programme recevrait les droits de BoxedWine (tes fichiers, l'accès réseau approuvé par l'admin) au lieu de ceux de l'app qui l'envoie, et la fenêtre de permission ne nommerait que BoxedWine.
>
> Il faut donc, dans tous les cas, un petit ADR Kazo : l'OS dit à l'app qui reçoit une demande quelle app l'envoie, et de quel genre de demande il s'agit (ouverture d'un fichier, lancement avec des arguments, commande du terminal). Seules une ouverture de fichier faite dans les Fichiers ou sur le bureau et une commande tapée dans un terminal que tu as ouvert comptent comme toi. Une commande lancée par une app, même dans un terminal qu'elle intègre, compte comme cette app. Un raccourci du bureau ne compte comme toi que pour démarrer un programme de ta bibliothèque, sans arguments, puisque n'importe quelle app peut en écrire un (voir 57). Dans tous les cas aussi, une app peut piloter Wine elle-même dans ses propres bouteilles (42).

- A) Elle passe le fichier à BoxedWine, comme un double-clic ✨(Recommandé)
  - Le programme est plafonné à la fois par les droits de l'app qui l'envoie et par ceux de BoxedWine, pour le réseau et pour les fichiers passés au lancement. L'affichage et le presse-papiers restent ceux du sous-système (22).
  - Une décision « toujours » prise pour tes propres lancements ne couvre pas ceux d'une autre app : la fenêtre redemande et nomme les deux apps.
- B) BoxedWine refuse les fichiers envoyés par d'autres apps ; une autre app passe par Os-x86.Interface et pilote Wine elle-même, sous son propre plafond
- C) BoxedWine publie sa propre API pour les autres apps (38 le permet à une Feature)
- D) Autre : (Spécifiez)

**53. Qui reçoit les programmes DOS et les autres fichiers Windows?**
> Un `.exe` DOS commence par « MZ », comme un `.exe` Windows ; seul un champ plus loin dans l'en-tête les distingue. Par 28, DOS va au FreeDOS d'Os-x86. Au double-clic, Kazo choisit l'app selon l'extension seulement. La plupart des `.com` n'ont pas d'en-tête, mais certains sont de vrais fichiers MZ, parfois même Windows. Les `.bat`, `.cmd` et `.msi` n'ont pas d'en-tête « MZ », et rien ne distingue un `.bat` de DOS d'un `.bat` de Windows.

- A) D'abord selon l'en-tête, ensuite selon l'extension ✨(Recommandé)
  - **Dans le terminal,** Kazo lit d'abord l'en-tête, quelle que soit l'extension : un programme Windows va à BoxedWine ; tout autre fichier MZ (DOS, y compris les jeux avec DOS4GW) va au FreeDOS d'Os-x86.
  - Ensuite, selon l'extension : un `.com` sans en-tête va à Os-x86 ; `.bat`, `.cmd` et `.msi` vont à BoxedWine. Un `.bat` de l'époque DOS échouera donc sous Wine : on le lance dans FreeDOS par la commande d'Os-x86.
  - **Au double-clic,** BoxedWine s'inscrit pour `.exe`, `.msi`, `.bat` et `.cmd`, et renvoie un `.exe` DOS à Os-x86 ; Os-x86 s'inscrit pour `.com` et renvoie un `.com` Windows à BoxedWine.
  - Avec Os-x86 seul, DOS marche dans le terminal, mais pas au double-clic d'un `.exe`.
- B) BoxedWine reçoit tout fichier MZ et toutes les extensions Windows, et renvoie les programmes DOS à Os-x86. Sans BoxedWine, DOS ne marche plus dans le terminal (ça change 28).
- C) BoxedWine exécute aussi DOS lui-même
- D) Autre : (Spécifiez)

**54. À qui appartient le sous-système « Programmes Windows », et que reste-t-il après une désinstallation?**
> Par 24, Os-x86 crée un sous-système « Programmes Windows » par usager au premier `.exe`. Avec ta réponse 40, ce premier `.exe` arrive chez BoxedWine.

- A) À Os-x86, qui le gère pour BoxedWine ✨(Recommandé)
  - Os-x86 le crée à la première demande de BoxedWine, avec la mention « géré pour BoxedWine ».
  - Dans l'app Sous-systèmes, tu ne changes pas ses réglages (mémoire, disque, image, périphériques), mais tu peux y supprimer une bouteille et révoquer ses décisions (23).
  - Supprimer une bouteille, là ou dans BoxedWine, supprime aussi ses décisions et son empreinte connue ; BoxedWine retire ses raccourcis.
  - **Désinstaller BoxedWine** garde les bouteilles et les décisions, marquées « app désinstallée ». Tu peux les supprimer ; réinstaller BoxedWine (même éditeur) les reprend.
  - **Désinstaller Os-x86** garde les VM de chaque usager, et le réinstaller les retrouve. Un admin peut les supprimer depuis les Paramètres de Kazo.
  - C'est une VM séparée de ton Linux : voir 59.
- B) À BoxedWine ; le désinstaller supprime les bouteilles, après confirmation (ça change 24)
- C) Les bouteilles vont dans ton Linux par défaut : pas de conflit entre VM (59), mais tes bouteilles se mêlent à ton Linux (ça change 24)
- D) Autre : (Spécifiez)

**55. Qui affiche les programmes Windows, et comment se comportent-ils?**
> Par 26, chaque programme Windows a sa propre fenêtre Kazo ; reste à savoir qui l'ouvre. Il lui faut l'image, la souris, le clavier, le focus, le presse-papiers et le son, sans rien de propre à Wine dans Os-x86.Interface (29). Kazo permet déjà à une app d'intégrer un composant fourni par une autre app (c'est ainsi qu'un terminal s'intègre ailleurs) ; ce composant tourne avec les droits de l'app qui l'affiche.

- A) Une fenêtre BoxedWine par programme, avec une « vue de l'invité » fournie par Os-x86 ✨(Recommandé)
  - La vue peint l'image, envoie la souris et le clavier, et gère le focus, le presse-papiers et le bouton muet. Elle agit avec les droits de BoxedWine : pour le presse-papiers et les fichiers, Kazo applique donc lui-même le plafond (22). Le plafond réseau (21) reste calculé par Os-x86. La même vue sert aux écrans de VM d'Os-x86.
  - BoxedWine lit l'en-tête du `.exe` pour savoir s'il est console ou graphique.
  - **Lancé depuis le terminal :** un programme console y affiche ses entrées et ses sorties, et reçoit Ctrl+C (27). Un programme graphique ouvre sa fenêtre, et le terminal attend qu'elle se ferme ; Ctrl+C l'arrête. Dans les deux cas, le code de sortie complet revient au terminal.
  - **Ouvert au double-clic,** un programme console s'affiche dans une fenêtre de BoxedWine qui contient un terminal Kazo (pareil chez Os-x86 pour un programme DOS). Il faut pour ça la permission « shell.host » (dangereuse), couverte par l'approbation de l'admin à l'installation (39).
  - Le petit lanceur qui rend le code de sortie complet (27) est un programme Windows. Il fait partie de l'image d'Os-x86 (44), pour que BoxedWine n'ait jamais à écrire d'exécutable dans une bouteille (50). Le prix : le corriger demande une nouvelle image.
  - Le titre de la fenêtre est le nom du programme ; la barre des tâches montre l'icône de BoxedWine (voir 57).
  - L'ADR des Features doit permettre à une Feature de fournir ce composant, avec un contrat défini dans Os-x86.Interface plutôt que dans le SDK de Kazo (pas encore testé).
- B) Os-x86 ouvre lui-même les fenêtres ; BoxedWine fournit le titre et l'icône
- C) Os-x86.Interface donne des briques de base (image, entrées, focus, son), et BoxedWine écrit sa propre vue
- D) Autre : (Spécifiez)

**56. Ces règles de versions te conviennent-elles?**
> Les deux paquets évoluent séparément. Exemple : BoxedWine compilé avec Os-x86.Interface 1.3, et Os-x86 1.2 installé. La copie 1.3 de l'API se charge (36), mais Os-x86 n'en connaît pas les nouveautés.
> 1. BoxedWine exige une version d'Os-x86 au moins égale à celle d'Os-x86.Interface avec laquelle il a été compilé (vérifié à la construction du paquet), et une version d'image minimale (44).
> 2. Un sous-système garde son image tant que tu n'acceptes pas la nouvelle ; pour Programmes Windows, c'est BoxedWine qui te la propose. Si tu refuses une image dont BoxedWine a besoin, il te la redemande au prochain lancement et ne lance rien d'ici là.
> 3. Après une nouvelle image, Wine met chaque bouteille à jour à son prochain démarrage. Ça suppose que la copie-sur-écriture se fait au niveau des fichiers, dans l'invité ; si elle se fait au niveau des blocs du disque, une nouvelle image voudra dire de nouvelles bouteilles. À trancher avec le stockage.
> 4. Une mise à jour d'Os-x86 abandonne les états de veille, car v86 refuse un état sauvegardé par une autre version. L'admin en est averti avant, et chaque usager touché, à sa prochaine connexion ; ce qui n'était pas enregistré dans les programmes est perdu.
> 5. Revenir à une version d'Os-x86 trop ancienne pour BoxedWine avertit que BoxedWine sera désactivé.

- A) Oui, toutes ✨(Recommandé)
- B) Non : les deux paquets sortent toujours ensemble, avec le même numéro de version
- C) Oui, sauf : (donne les numéros)
- D) Autre : (Spécifiez)

**57. Comment retrouve-t-on un programme Windows installé?**
> Kazo a des raccourcis de bureau qui lancent une app avec des arguments, et n'importe quelle app qui écrit dans ton dossier personnel peut en déposer un. Je n'ai trouvé dans le code de Kazo (recherche par mots seulement) ni entrée par programme dans le menu Démarrer, ni icône par fenêtre.

- A) La bibliothèque de BoxedWine et des raccourcis de bureau ✨(Recommandé)
  - La bibliothèque liste les programmes trouvés dans les bouteilles. Leurs noms et leurs icônes viennent des programmes eux-mêmes : BoxedWine les traite comme des données non fiables.
  - « Ajouter au bureau » crée un raccourci vers BoxedWine qui ne contient qu'un numéro de sa bibliothèque, jamais un chemin ni une commande.
  - Lancé par un raccourci, ou par tout autre lancement avec arguments, BoxedWine n'accepte qu'un numéro de sa bibliothèque et refuse un chemin. Un faux raccourci ne peut donc démarrer qu'un programme déjà dans ta bibliothèque, sans arguments ; pour ça, il compte comme toi.
  - L'icône et le raccourci lui-même sont dans ton dossier personnel : une autre app peut les modifier, et même faire pointer le raccourci vers une autre app. Un raccourci vers BoxedWine, lui, ne peut jamais démarrer qu'un programme de ta bibliothèque.
  - Pas d'entrée par programme dans le menu Démarrer en v1.
- B) Un ADR Kazo dès la v1 : une entrée de menu Démarrer par programme et une icône par fenêtre
- C) La bibliothèque seulement, sans raccourcis
- D) Autre : (Spécifiez)

**58. Que se passe-t-il si Os-x86 est installé, mais pas BoxedWine?**
> Aujourd'hui, `./setup.exe` rend le code 126 : « Permission denied » avec un conseil `chmod +x` si le fichier n'est pas exécutable, sinon « cannot execute binary file ». Les Fichiers disent qu'aucune application n'est enregistrée pour ce fichier. Ta réponse 13 voulait un message avec la commande d'installation.

- A) Une suggestion générique dans Kazo ✨(Recommandé)
  - Un paquet déclare dans sa fiche du Store les formats qu'il prend en charge. Quand aucune app installée ne convient, le terminal affiche la commande d'installation (en rappelant qu'il faut un admin) et rend 126 ; les Fichiers montrent un lien vers le Store. Kazo n'a ainsi aucun nom de produit codé en dur.
  - Os-x86 seul ne lance aucun `.exe` Windows depuis Kazo : sa commande refuse les programmes Windows, mais lance toujours les programmes DOS (`.exe`, `.com`, `.bat` : voir 53) et Linux. Dans une VM Windows complète (1), tu lances tes programmes à l'intérieur de Windows.
  - La commande d'Os-x86 refuse d'ouvrir un shell ou de lancer une commande dans le sous-système Programmes Windows. Un programme Windows n'y entre que par BoxedWine, ou par une autre app qui pilote Wine dans ses propres bouteilles par Os-x86.Interface (52), et toujours par le chemin de 50.
  - Si tu installes Wine toi-même dans ton Linux, c'est un programme Linux comme un autre (46), sans bouteille.
  - L'image avec Wine n'est téléchargée qu'à la première demande de BoxedWine (à revoir si elle partage la base de ton Linux, avec le stockage).
- B) Os-x86 enregistre un gestionnaire de secours qui nomme BoxedWine
- C) On garde les messages actuels
- D) Autre : (Spécifiez)

**59. Une autre VM tourne déjà quand tu lances un programme : que se passe-t-il?**
> Par défaut, une seule VM tourne à la fois par session (35). Programmes Windows (24), FreeDOS (28) et ton Linux sont des VM séparées : ouvrir un `.exe` pendant que ton Linux tourne entre en conflit.

- A) Une fenêtre d'Os-x86 te propose de mettre l'autre VM en veille ou de l'arrêter ✨(Recommandé)
  - Même pour un lancement depuis le terminal : jamais de question dans le terminal ; la commande attend ta réponse.
  - Si tu refuses, le lancement échoue avec un message clair.
  - La mise en veille peut prendre quelques secondes ; la maquette du jalon 0 le mesure (48).
- B) Refus direct, avec un message qui dit quelle VM arrêter
- C) Deux VM à la fois par défaut, quand la mémoire le permet (ça change 35)
- D) Autre : (Spécifiez)

**60. Quels noms pour les commandes, et qui gagne en cas de conflit?**
> Le shell trouve une app fenêtrée par la fin de son identifiant, et abandonne si deux apps ont la même fin. Pour un nom de commande, une app système gagne ; sinon, c'est à peu près l'ordre alphabétique des identifiants qui décide, et l'autre app ne reçoit qu'un avertissement dans le journal système.

- A) `boxedwine` et `x86`, avec des règles de conflit dans Kazo ✨(Recommandé)
  - La commande de BoxedWine : `boxedwine`, sans alias `wine` (dans l'invité, `wine` reste la commande de Wine).
  - La commande d'Os-x86 : `x86`.
  - Je te proposerai les identifiants exacts (Feature, points d'entrée, espace de noms C#) dans la spécification ; chaque point d'entrée aura une fin d'identifiant unique.
  - ADR du binfmt : un format est relié à un identifiant d'app, jamais à un nom de commande ; si deux Features veulent le même format, un admin choisit.
  - ADR des Features : les commandes d'une Feature passent avant celles des apps ordinaires.
- B) Pareil, avec l'alias `wine` pour BoxedWine
- C) Pareil, mais un autre nom pour la commande d'Os-x86 : (Spécifiez)
- D) Autre : (Spécifiez)

**61. Ces choix techniques du partage te conviennent-ils?**
> 1. **Les décisions de permission** vont dans le stockage du navigateur réservé à Os-x86 (OPFS), séparé par usager par Os-x86 lui-même ; BoxedWine range sa bibliothèque de la même façon. Jamais dans `~/.config`, que toute app ayant la permission normale d'écrire dans ton dossier personnel peut modifier. Aucune permission de Kazo n'ouvre ce stockage aux autres apps, mais une app malveillante qui exécute son propre JavaScript pourrait l'atteindre (limite connue de Kazo).
> 2. **Aucune opération « autoriser » dans Os-x86.Interface.** Une app peut lire l'état des permissions de ses propres bouteilles, les révoquer ou ouvrir la page des règles d'Os-x86. Seuls toi et l'admin pouvez accorder : toi dans la fenêtre ou la page des règles, l'admin par ses règles.
> 3. **Root dans l'invité.** L'agent d'Os-x86 (un petit programme d'Os-x86 qui tourne dans le Linux de la VM et y exécute ses demandes) est le seul à tourner en root. Les programmes lancés par Kazo et les commandes d'Os-x86.Interface reçoivent une liste d'arguments, jamais une ligne de shell, et tournent sous l'usager propre à leur bouteille, dans son réseau. Pas de sudo dans les sous-systèmes gérés. Dans tes Linux à toi, sudo reste un réglage par sous-système, avec un avertissement : les règles réseau par programme n'y sont plus garanties (comme prévu en 35). Une distribution que tu apportes n'a pas d'agent.
> 4. **Si la commande ou la fenêtre de BoxedWine plante ou est fermée de force,** Os-x86 arrête le programme dans l'invité. Une décision « jusqu'à la fin du programme » suit le lancement tel qu'Os-x86 l'a enregistré.
> 5. **Aucun consentement ne se tape dans le terminal :** un programme console pourrait y afficher une fausse question.
> 6. **Wine Mono et Wine Gecko** (les remplaçants de .NET et d'Internet Explorer que Wine veut télécharger à la création d'une bouteille) font partie de l'image : environ 0,15 à 0,25 Go de plus à télécharger, mais créer une bouteille ne télécharge rien et ne déclenche aucune question réseau. Si Alpine ne les fournit pas, Os-x86 les ajoute lui-même.
> 7. **BoxedWine ne se connecte pas au réseau pour lui-même en v1 :** son accès réseau (43) ne sert qu'aux programmes qu'il lance.
> 8. **Les correctifs de Wine** (par exemple pour les bureaux virtuels) vont dans le paquet de l'image, avec l'offre des sources. BoxedWine, sous MIT, ne contient aucun code du projet Boxedwine (l'émulateur GPL-2 dont il tient son nom).
> 9. **Limite connue :** dans une même VM, les programmes partagent l'affichage, le clavier et le presse-papiers. Un programme peut voir les fenêtres et les frappes des autres, et leur envoyer des clics et des frappes : il peut donc faire agir un programme d'une autre bouteille, avec le réseau et les fichiers de celle-ci. Les permissions par programme ne couvrent que le réseau (22), et seulement face à un programme qui agit seul. Chaque bouteille a son propre usager, donc ses propres fichiers, sauf par ce détour.
> 10. **Changer l'app par défaut d'une extension** (par exemple `.exe`) demandera ta confirmation, et seul l'OS pourra écrire le fichier de ces choix (petit ADR Kazo). Aujourd'hui, une app qui n'a que des permissions normales peut le faire en silence, par l'API ou en écrivant directement `~/.config/kazoos/file-associations.json`.
> 11. **Pas d'installation d'un programme Windows pour tous les usagers en v1 :** chaque usager a ses bouteilles (24).
> 12. **Une autre app ne voit pas les bouteilles de BoxedWine,** sauf celles que tu lui donnes par le sélecteur d'Os-x86 (50).
> 13. **À faire à l'implémentation :** passer le dépôt Os-App-BoxedWine sous MIT, corriger sa description (qui parle encore de « HackerOs ») et mettre à jour la note de Kazo qui lie BoxedWine à la GPL-2.

- A) Oui, tous ✨(Recommandé)
- B) Oui, sauf : (donne les numéros)
- C) Autre : (Spécifiez)

---

Aucun code n'a été écrit.

On retient mes recommandations pour les questions 49 à 61? Une ligne vide ou « ok » veut dire oui ; sinon, réponds par numéro (par exemple « 50 B, 61 B sauf 3 »).
