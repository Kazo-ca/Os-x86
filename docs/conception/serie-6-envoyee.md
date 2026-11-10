# Série 6 — texte envoyé le 2026-11-09

> **Réponses reçues :** 62 B (à BoxedWine, par l'association `.exe`) ; 63 A (« j'accepte ta recommandation ») ; 64 : « boxedWine. Os-x86 n'a pas d'association d'extension. »
> Les autres questions (65 à 76) restent ouvertes : leurs recommandations ne sont **pas** acceptées (« je n'accepte pas tes recommandations pour les questions après 64 »). Elles supposaient 62 A : elles doivent être revues avant d'être reposées (voir « Effets de 62 B et 64 » dans [journal-decisions.md](journal-decisions.md)).

---

C'est noté. Pour 50, 54, 56, 58, 60 et 61, je retiens mes recommandations. 49, 52, 53, 55 et 57 sont consignées avec tes précisions, et 51 D et 59 C avec tes mots, dans le journal des décisions. Plusieurs de ces réponses en modifient d'autres déjà prises : chaque question nomme ce qu'elle change.

**Ma lecture de tes réponses** (les questions 62, 63, 64 et 72 te permettent de la corriger)
- **Os-x86 est le moteur.** Il fait tourner les VM, choisit les bouteilles, applique les permissions et produit l'affichage des programmes (la « vue de l'invité »). Sa commande `x86` fait tout sans BoxedWine.
- **BoxedWine est une app ordinaire, la façon simple d'utiliser Os-x86.** Il reçoit le double-clic et les demandes des autres apps, affiche les programmes qu'il lance, et offre ce qu'Os-x86 n'a pas : réglages, fichiers récents, utilitaires de configuration.
- **« Os-x86 comme des DLL » :** BoxedWine appelle Os-x86.Interface et affiche la vue d'Os-x86 dans ses fenêtres ; les VM tournent dans Os-x86, partagées par toutes les apps (72.6).
- **Le terminal parle directement à Os-x86 (62).** Comme BoxedWine n'est pas une Feature (51), `./setup.exe` ne peut pas aller à lui (38) ; `boxedwine setup.exe` lui parvient, mais comme un double-clic, sans code de sortie (72.8). Ta décision 7 revient : Os-x86 seul lance les `.exe` depuis le terminal.
- **Les fenêtres d'Os-x86, si tu acceptes tout :** aucune fenêtre de réglages. Seulement ses fenêtres de sécurité (permission réseau, page des règles, sélecteur de bouteilles, confirmations), deux fenêtres de question (« une autre VM tourne » pour les lancements du terminal et des autres apps, 66 ; nouvelle image au prochain lancement depuis le terminal, 72.11) et des fenêtres simples d'affichage (écran d'une VM, programme graphique lancé au terminal, programme DOS ouvert au double-clic).

Trois analyses en parallèle (tes réponses, le code de Kazo, puis Wine, Boxedwine et v86), une critique croisée et trois relecteurs ont préparé cette série. Kazo est toujours au commit cd89ee0. Un constat qui n'est pas dans les questions : les versions récentes de l'émulateur Boxedwine (dont BoxedWine tient son nom) donnent à chaque programme son environnement privé et gardent une liste « récemment ouvert », comme ton BoxedWine. Leurs interfaces de bureau n'ont aucun réglage de mémoire, de disque ou de réseau : chez nous, ce sont des réglages de VM (63).

---

## Sixième série : le nouveau partage, puis le terminal, le presse-papiers, le son et la souris

**62. Où va `./setup.exe` tapé dans le terminal?**
> Par 38, seule une Feature peut enregistrer un binfmt (le mécanisme qui envoie `./setup.exe` à la bonne app), et BoxedWine reste une app ordinaire (51). Tu as écrit qu'on pourrait tout faire en ligne de commande avec Os-x86 seul (51), et que le terminal affiche les programmes console qu'il lance (53, 55). Une fenêtre Kazo n'a pas de code de sortie : seul ce chemin peut rendre celui du programme (13).

- A) À Os-x86, par un binfmt générique de Kazo ✨(Recommandé)
  - Le terminal affiche le programme console et reçoit son code de sortie complet.
  - Au terminal, tout fichier lancé par son chemin (`./setup.exe`) va à Os-x86 : d'abord selon l'en-tête (DOS ou Windows), puis selon l'extension (`.com`, `.bat`, `.cmd`, `.msi`). Le binfmt de Kazo reconnaît donc aussi des extensions.
  - Os-x86 seul lance les programmes Windows en ligne de commande : ta décision 7 revient. Le sous-système Programmes Windows peut naître d'une commande tapée au terminal ; il est alors « géré par Os-x86 ».
  - Ce qu'il faut pour lancer Wine (le petit lanceur, la base commune des bouteilles, 20) est dans l'image d'Os-x86. BoxedWine lance ses programmes de la même façon, en demandant à Os-x86 « lance ce fichier », et règle Wine (version de Windows, registre) par des commandes dans l'invité (29). Os-x86.Interface reste générique.
  - Je lis « les modifications du terminal » (53) comme deux ADR génériques de Kazo : le binfmt et le mode brut (le terminal passe chaque touche au programme, 27). Le terminal ne connaît pas Os-x86 par son nom (58).
  - Le double-clic, et l'ouverture d'un fichier demandée par une autre app (le « shell » de 52, qui passe par l'association `.exe`), vont toujours à BoxedWine. Avec Os-x86 seul, un double-clic sur un `.exe` ne lance rien : Kazo suggère BoxedWine (58). Une app qui tape `./setup.exe` dans un terminal qu'elle intègre arrive à Os-x86, et compte comme elle (52).
  - Ça change 49 A (le terminal n'est plus une porte de BoxedWine), 53 A (au terminal, programmes Windows, `.bat`, `.cmd` et `.msi` vont à Os-x86), 54 A (« géré pour BoxedWine »), 55 A (la puce « Lancé depuis le terminal »), 58 A (puces 2 et 5) et ma note sur 40 (« BoxedWine porte la logique de Wine »).
- B) À BoxedWine, par l'association `.exe` : pas de code de sortie, et Os-x86 seul ne lance aucun programme Windows
- C) À Os-x86, mais le terminal de Kazo appelle lui-même Os-x86.Interface, comme BoxedWine. C'est le plus proche de tes mots en 53. Le prix : une app système de Kazo connaît Os-x86 par son nom (ça change 58), et il faut quand même le mode brut.
- D) Autre : (Spécifiez)

*Les questions 63 à 72 supposent 62 A. Si tu choisis autre chose, je reprendrai celles qui en dépendent.*

**63. Que couvre l'interface de BoxedWine, et que reste-t-il de celle d'Os-x86?**
> 49 A laissait à Os-x86 son app Sous-systèmes (11). Ta réponse 51 dit qu'Os-x86 n'a ni paramètres, ni fichiers récents, ni utilitaire de configuration de disque, de machine ou de réseau. Je lis « pas de paramètres utilisateur » comme : pas de fenêtre de réglages ; Os-x86 garde quand même tes décisions et tes réglages de VM (72.3).

- A) Os-x86 n'a aucune app ; BoxedWine règle les VM de programmes Windows ✨(Recommandé)
  - **BoxedWine :** les programmes Windows et les VM qui les font tourner : bibliothèque, fichiers récents, réglages par programme, bouteilles, et les réglages de ces VM (mémoire, taille du disque, carte réseau, son, CD et disquette, 34). Les points de restauration et l'import ou l'export du disque touchent toutes les bouteilles de la VM, même celles des autres apps : ils passent par `x86` ou demandent « gérer les sous-systèmes » (42).
  - Pour ça, BoxedWine déclare une nouvelle permission dangereuse d'Os-x86, « configurer les VM de programmes Windows », approuvée par un admin (72.9). Elle couvre ces VM et rien d'autre. C'est une troisième permission, après les deux de 32.
  - **Os-x86 :** pas d'app. La commande `x86`, qui fait tout, configuration comprise ; ses fenêtres de sécurité (la page des règles, où tu révoques tes décisions, 23) ; et des fenêtres simples d'affichage : l'écran d'une VM Linux, DOS ou complète, ouvert avec `x86`, et un programme graphique lancé au terminal (64).
  - En v1, les réglages des VM Linux, DOS et complètes passent par `x86` seulement. Les VM en marche se voient avec `x86 list` et dans BoxedWine (66).
  - Ça change 11 et 49 A (plus d'app Sous-systèmes), 24 (« non modifiable ») et 54 A (« réglages verrouillés ») : les réglages des VM Programmes Windows changent avec BoxedWine et `x86`.
- B) Comme A, mais Os-x86 garde une petite app Sous-systèmes sans réglages (liste, démarrer, arrêter, écran, supprimer), comme le disait 49 A
- C) BoxedWine est l'interface de tout Os-x86, ta « façon simple d'utiliser Os-x86 » (51) : il liste, démarre, affiche et règle aussi les VM Linux, DOS et complètes. Os-x86 n'a que `x86` et ses fenêtres de sécurité, comme WSL sans interface. Le prix : BoxedWine demande « gérer les sous-systèmes » (dangereuse, approuvée par un admin à son premier lancement), et son nom parle de Wine (47). Ça change 11 et 49 A.
- D) Autre : (Spécifiez)

**64. Qui affiche un programme graphique lancé depuis le terminal?**
> Ta précision de 55 réserve le terminal aux programmes console. Un programme graphique a besoin d'une fenêtre.

- A) Une fenêtre simple d'Os-x86 ✨(Recommandé)
  - Même vue que dans BoxedWine, avec le titre et l'icône du programme ; sans icône dans le programme, une icône générique de programme, jamais celle d'Os-x86. Marche avec ou sans BoxedWine.
  - Le terminal attend que la fenêtre se ferme et reçoit le code de sortie ; Ctrl+C arrête le programme. Fermer l'onglet du terminal l'arrête aussi.
  - Un programme DOS lancé au terminal y reste tant qu'il est en mode texte. S'il passe en mode graphique (un jeu), cette fenêtre s'ouvre avec l'écran de FreeDOS, et le terminal attend.
  - Ici, Os-x86 affiche lui-même, ce qui s'écarte de ta règle « Os-x86 génère le rendu, BoxedWine l'affiche » (49) ; mais ainsi, une Feature ne dépend jamais d'une app ordinaire. Ça change 55 A, où ce programme s'ouvrait dans une fenêtre BoxedWine.
- B) BoxedWine quand il est installé (Os-x86 le lui demande), sinon A
- C) Le terminal de Kazo affiche lui-même la vue d'Os-x86, dans une fenêtre à lui. Ça suit ta phrase de 53 (« le terminal s'occupe aussi d'afficher le rendu »). Le prix : le terminal intègre la vue d'Os-x86, qui tourne avec ses droits (dont « shell.host »).
- D) Autre : (Spécifiez)

**65. Le même programme lancé par le terminal et par BoxedWine : une bouteille ou deux?**
> Par 50, une bouteille n'est réutilisée que pour la même app créatrice. Avec 62 A, un double-clic sur un fichier déjà lancé au terminal créerait une deuxième bouteille, une deuxième question réseau et une deuxième installation.

- A) Une seule bouteille pour tes propres lancements ✨(Recommandé)
  - **Tes lancements :** une commande tapée dans un terminal que tu as ouvert, un double-clic, un de tes raccourcis ou une de tes entrées du menu Démarrer. Os-x86 vérifie auprès de Kazo que le lancement vient bien de toi (ADR de 52). Ils partagent une bouteille par usager, sous-système et empreinte, quelle que soit la porte d'entrée.
  - **Qui voit ces bouteilles :** l'app que tu as confirmée toi-même, dans une fenêtre d'Os-x86, pour gérer tes programmes Windows (BoxedWine en pratique). Être la seule app qui ouvre les `.exe`, ou le choix d'un admin ou d'une autre app, ne suffit pas. Cette fenêtre dit que l'app verra toutes tes bouteilles et pourra y lancer des commandes, même dans celles de tes lancements au terminal (la limite de 50 s'étend). Changer d'app redemande ta confirmation. Seule exception : une app à qui un admin a accordé « gérer les sous-systèmes » (32, 42).
  - Une app à qui tu ouvres un fichier par « Ouvrir avec… » ne reçoit que la bouteille de ce fichier. Une autre app ne voit que ses bouteilles et celles que tu lui donnes par le sélecteur (61.12). Un lancement demandé par une autre app garde ses propres bouteilles (52).
  - Désinstaller BoxedWine ne touche pas à tes bouteilles : elles restent à toi et marchent au terminal. Seules celles des autres apps sont marquées « app désinstallée ».
  - Ça change 50 A (« même app créatrice ») et 54 A.
- B) On garde 50 : des bouteilles séparées selon la porte d'entrée
- C) Quand BoxedWine est installé, le terminal lui demande quelle bouteille utiliser (ça change 50 : BoxedWine choisirait la bouteille)
- D) Autre : (Spécifiez)

**66. Plusieurs VM (59) : quand te pose-t-on la question, et qui exécute ton choix?**
> Une VM est un sous-système en marche. Les choix possibles : démarrer la VM du programme à côté des autres, l'ouvrir dans une VM déjà en marche, créer une nouvelle VM (67), ou fermer une autre VM. Si une bouteille servait à deux VM à la fois, Wine ne le verrait pas : chacune garderait sa copie du registre, et la dernière qui écrit gagnerait (déduit du code de Wine). C'est pourquoi, en v1, une bouteille reste sur le disque de sa VM.
>
> Exemple : ton Linux tourne et tu ouvres un `.exe`. Ton Linux n'a pas Wine, donc « VM existante » est grisé ; il te reste démarrer Programmes Windows à côté, créer une nouvelle VM, ou fermer ton Linux.

- A) Seulement quand il y a un vrai choix ✨(Recommandé)
  1. **Quand :** une autre VM tourne, et la VM du programme n'est pas déjà en marche. Si elle tourne déjà, il n'y a rien à choisir. La même question vaut quand tu démarres une VM toi-même.
  2. **VM existante :** seulement une VM d'Os-x86 qui peut lancer le programme (une autre VM Programmes Windows ; FreeDOS pour DOS ; un autre de tes Linux pour un ELF), et seulement s'il n'a pas encore de bouteille. Jamais ton Linux pour un programme Windows (58, 61.3).
  3. **Fermer une autre VM :** BoxedWine montre le choix, mais c'est Os-x86 qui affiche l'avertissement pour ce qui n'est pas enregistré et qui arrête la VM ; BoxedWine ne voit des autres VM que leur nom et leur mémoire. La mise en veille seulement si la maquette du jalon 0 la juge assez rapide.
  4. **Ressources :** Os-x86 additionne la mémoire des VM en marche, sous une limite fixée par l'admin (valeur par défaut fixée après la maquette du jalon 0). Un choix impossible est grisé. Ça change 35.
  5. **Qui demande :** BoxedWine pour ses lancements ; une fenêtre d'Os-x86 pour ceux du terminal et des autres apps (jamais de question tapée). Des options de `x86` peuvent répondre d'avance.
  6. **Toujours visibles :** les VM en marche sont listées par `x86 list` et dans BoxedWine, avec « Arrêter » (qui passe par la confirmation d'Os-x86).
  7. **Une VM ne démarre jamais deux fois,** même dans un autre onglet du navigateur ouvert sur Kazo : Os-x86 verrouille son disque.
- B) Comme A, mais la question revient à chaque lancement dès qu'une VM tourne
- C) Démarrer sans rien demander tant que les ressources suffisent ; demander seulement quand elles manquent
- D) A, sauf : (donne les numéros)
- E) Autre : (Spécifiez)

**67. Que veut dire « créer une nouvelle VM »?**
> Je lis ton « créer une nouvelle VM » (59) de deux façons. Un même disque de VM ne peut pas tourner deux fois.

- A) Un nouveau sous-système, gardé ✨(Recommandé)
  - Une nouvelle VM du même genre (Programmes Windows, FreeDOS ou Linux), neuve, avec les réglages par défaut. Le programme y reçoit une nouvelle bouteille ; ensuite, ce fichier va dans cette nouvelle bouteille.
  - Ça change 24 : un ou plusieurs sous-systèmes Programmes Windows par usager. Un nouveau programme va dans celui marqué par défaut (au départ, le premier).
  - C'est aussi la façon de séparer un programme dont tu te méfies (61.9) : il n'a plus le même affichage ni le même clavier que tes autres programmes. Le presse-papiers passe encore par Kazo quand tu changes de fenêtre (74), et son dossier visible reste partagé (22).
- B) Une VM jetable : neuve, effacée avec ses bouteilles à sa fermeture
- C) Pas de nouveau sous-système : « créer une nouvelle VM » veut seulement dire démarrer la VM du programme à côté des autres ; 24 reste
- D) Autre : (Spécifiez)

**68. Où s'ouvre un programme console au double-clic?**
> 55 A le mettait dans un terminal Kazo intégré à BoxedWine (ou à Os-x86 pour DOS). Ça demande « shell.host », la permission dangereuse qui permet de lancer des commandes dans le shell de Kazo : un admin devrait l'approuver au premier lancement de BoxedWine.

- A) Dans une vue console d'Os-x86 ✨(Recommandé)
  - Une vue console est un écran texte dessiné par Os-x86, sans shell de Kazo derrière : elle n'affiche que ce programme et ne peut lancer aucune commande.
  - Un programme Windows s'ouvre dans une fenêtre BoxedWine qui contient cette vue : Os-x86 génère le rendu, BoxedWine l'affiche (49). Un programme DOS s'ouvre dans une fenêtre simple d'Os-x86 (53), en texte puis en graphique.
  - Personne n'a besoin de « shell.host ». Ça change 55 A, pour Windows comme pour DOS.
- B) Comme 55 A : un terminal Kazo intégré, avec « shell.host » approuvée par un admin
- C) Dans un onglet du terminal Kazo ouvert par Os-x86 ; l'onglet ne reçoit qu'un numéro qui désigne ce lancement, jamais une ligne de commande (il faut changer le terminal pour ça)
- D) Autre : (Spécifiez)

**69. Que peut faire `x86` avec les programmes Windows?**
> Tu as écrit qu'on pourrait tout faire en ligne de commande avec Os-x86 seul. Or 58 A disait que `x86` refuse d'ouvrir un shell ou de lancer une commande dans Programmes Windows.

- A) Tout ✨(Recommandé)
  - Lancer un fichier ; lister et supprimer des bouteilles ; révoquer des décisions ; lancer une commande ou un shell dans une bouteille ; configurer les VM. Ça change la puce 3 de 58 A.
  - Lancer un nouveau fichier dans une bouteille existante : `--bouteille=<nom>`, ou `--bouteille` seul pour ouvrir le sélecteur d'Os-x86 (41). Os-x86 affiche alors l'avertissement de 41 dans une fenêtre et attend ta confirmation, jamais une réponse tapée (61.5). Si c'est une app qui le demande, la fenêtre la nomme : elle recevra cette bouteille (42).
  - `x86` ne peut jamais accorder une permission, seulement la révoquer (61.2).
  - **Limite :** un shell ou une commande dans une bouteille peut lancer n'importe quel programme avec le réseau déjà accordé à cette bouteille, sans l'avertissement de 41, comme BoxedWine en 50. La fenêtre de permission le dit : « Toujours » couvre tout ce qui sera lancé dans la bouteille, par toi avec `x86` ou par l'app qui la gère (ça précise 50).
  - Dans un terminal que tu as ouvert, `x86` compte comme toi ; lancé par une app, il compte comme elle (52).
- B) Lancer, lister et supprimer seulement ; on garde la puce 3 de 58 A
- C) Comme A, mais les commandes et les shells dans une bouteille seulement depuis un terminal que tu as ouvert
- D) Autre : (Spécifiez)

**70. Les icônes, les entrées du menu Démarrer et la marque de BoxedWine : comment Kazo les affiche-t-il?**
> D'après son code, Kazo prend aujourd'hui l'icône d'une fenêtre seulement dans le manifeste ; son menu Démarrer ne liste que des apps ; et l'icône d'un raccourci de bureau est lue, mais jamais dessinée. Un raccourci est un simple fichier que toute app peut réécrire (57).

- A) Un ADR générique, et une marque dessinée par Kazo ✨(Recommandé)
  - **Icône de fenêtre :** une app peut changer l'icône de sa fenêtre en marche, sans permission, comme son titre ; le bouton de la barre des tâches suit. L'image est une icône du paquet ou un PNG fourni par l'app, avec une taille maximale.
  - **Entrées du menu Démarrer :** avec une nouvelle permission normale, une app ajoute pour l'usager des entrées (nom, icône, et un numéro que seule l'app comprend, jamais un chemin ni une commande) qui ne lancent qu'elle-même, au plus 64 par app. Kazo les range hors de `~/.config`, les retire à la désinstallation et montre quelle app les a créées ; on peut les chercher et les épingler.
  - **Raccourcis de bureau :** Kazo dessine enfin leur icône.
  - **La marque que tu as demandée** (ça change ta précision de 57 : c'est Kazo, pas BoxedWine, qui la dessine) : en bas à droite, la petite icône de l'app que le raccourci ouvre, sur tout raccourci ou entrée dont l'icône n'est pas celle de cette app. Même résultat à l'écran, mais une autre app ne peut ni l'enlever ni la changer, et BoxedWine n'a pas à retoucher d'images.
  - **Limite :** sans marque sur les fenêtres, un programme Windows peut prendre le titre et l'icône d'une app de Kazo, même ceux de la fenêtre de permission d'Os-x86. La vraie fenêtre de permission ne demande jamais de mot de passe ; C ferme cette porte.
- B) Comme A pour les icônes de fenêtre, les entrées et les raccourcis, mais la marque comme tu l'as écrite : BoxedWine l'ajoute dans le PNG de chaque raccourci et de chaque entrée, et Kazo n'en dessine aucune. Une autre app pourrait l'imiter ou l'enlever sur un raccourci de bureau.
- C) A, plus la marque sur l'icône des fenêtres et de la barre des tâches qui changent d'icône en marche. Os-x86 et BoxedWine posent toujours une icône sur une fenêtre de programme (celle du programme ou une icône générique), donc elle porte toujours la marque : un programme Windows ne pourrait pas se faire passer pour une app de Kazo
- D) Autre : (Spécifiez)

**71. « Ajouter une application à Kazo OS » : où navigues-tu, et que montre la bibliothèque?**
> Les Fichiers de Kazo ne voient pas l'intérieur des bouteilles. Un `.lnk` vient d'un programme : c'est une donnée non fiable.

- A) Dans tes bouteilles et dans tes fichiers, avec des propositions après une installation ✨(Recommandé)
  - **Où :** dans une fenêtre de BoxedWine qui montre les bouteilles de tes lancements (65), avec leurs programmes installés et les raccourcis `.lnk` de leur menu Démarrer ; dans le sélecteur d'Os-x86 pour une autre bouteille ; ou dans le sélecteur de fichiers de Kazo pour un `.exe` qui marche sans installation, qui reste où il est.
  - **Lecture des `.lnk` et des icônes :** par Wine, dans l'invité, comme des données non fiables ; jamais dans Kazo.
  - **Entrées :** menu Démarrer obligatoire, bureau facultatif (57).
  - **Après une installation lancée par BoxedWine,** il te propose les nouveaux programmes trouvés, comme le fait l'émulateur Boxedwine ; tu choisis lesquels ajouter.
  - **La bibliothèque** montre les programmes ajoutés, et ceux trouvés dans tes bouteilles, marqués « pas dans Kazo ».
  - **Retrait :** retirer le programme de la bibliothèque, supprimer sa bouteille ou désinstaller BoxedWine retire ses entrées du menu Démarrer et du bureau.
- B) Comme A, sans propositions après une installation : seulement ce que tu ajoutes toi-même
- C) Autre : (Spécifiez)

**72. Ces choix techniques te conviennent-ils?**
> 1. Les points d'entrée d'Os-x86 comptent comme une seule app pour ses données et ses bouteilles.
> 2. Os-x86 reconnaît le type du fichier (DOS ou Windows, console ou graphique) dans les octets qu'il lit pour l'empreinte. Ça change 55 A, où BoxedWine lisait l'en-tête.
> 3. Je lis « pas de paramètres utilisateur » (51) comme : pas de fenêtre de réglages. Os-x86 garde quand même tes décisions, ton Linux par défaut et tes réglages de VM. Les réglages changent avec `x86`, et dans BoxedWine pour les VM Programmes Windows (63). Une décision ne s'accorde que dans la fenêtre ou la page des règles d'Os-x86 ; `x86` et BoxedWine peuvent seulement la révoquer (61.2, 61.5).
> 4. Les réglages « réseau » de BoxedWine ne touchent que la carte réseau de la VM ; accorder le réseau à un programme reste à Os-x86.
> 5. BoxedWine ouvre une fenêtre par programme (plusieurs instances) ; la bibliothèque est une fenêtre sans programme. Si une fenêtre plante, seul son programme s'arrête.
> 6. Je lis « Os-x86 comme des DLL » (49) ainsi : les VM tournent dans Os-x86, partagées par toutes les apps, et fermer BoxedWine n'arrête aucune VM. Une VM sans programme ni fenêtre ouverte s'arrête d'elle-même après quelques minutes (délai fixé dans la spécification).
> 7. Après l'ADR de 52, BoxedWine accepte un chemin seulement quand Kazo lui dit qui l'envoie : l'ouverture d'un fichier (Fichiers, bureau ou autre app), une demande directe d'une app nommée par Kazo, ou la commande `boxedwine` tapée dans ton terminal. Un raccourci du bureau, une entrée du menu Démarrer ou un lancement sans expéditeur connu ne porte qu'un numéro de bibliothèque (ça précise 57 A).
> 8. `boxedwine fichier.exe` agit comme un double-clic : un programme console s'ouvre dans une fenêtre BoxedWine (68), pas dans le terminal, et la commande rend 0 quand la fenêtre se ferme (1 si elle plante, 130 après Ctrl+C). Le code de sortie complet ne vient que de `./fichier.exe` ou de `x86`. Une app qui déclare une commande `boxedwine` cacherait BoxedWine, car Kazo cherche les commandes avant les fenêtres : seul `x86` est protégé (ça change 60 A).
> 9. Les permissions dangereuses de BoxedWine (accès réseau réel, configuration des VM) sont approuvées par un admin à son premier lancement, pas à l'installation, et cet accord vaut pour tous les usagers (ça change 43). Sans accord, BoxedWine s'ouvre quand même, mais ses programmes n'ont pas de vrai réseau et il ne peut pas régler les VM.
> 10. Une installation personnelle de BoxedWine quand Os-x86 manque passe par l'ADR des dépendances (12) : Kazo propose d'installer Os-x86 en même temps, avec l'accord d'un admin ; sinon, l'installation s'arrête avec un message clair. Aujourd'hui, elle réussit, puis BoxedWine n'apparaît nulle part (une ligne dans le journal système seulement).
> 11. Une nouvelle image est proposée par BoxedWine, et par Os-x86 dans une fenêtre au prochain lancement depuis le terminal (jamais une question tapée). L'image avec Wine est téléchargée au premier programme Windows, quelle que soit la porte d'entrée (ça change 56, règle 2, et la puce 5 de 58).
> 12. BoxedWine range sa bibliothèque, ses fichiers récents et ses réglages dans son propre stockage du navigateur, jamais dans `~/.config` (61.1), et y sépare lui-même les usagers. Comme en 61.1, aucune permission de Kazo n'ouvre ce stockage aux autres apps, mais une app qui exécute son propre JavaScript pourrait le modifier (limite connue de Kazo).
> 13. Dans le Linux de la VM, l'agent d'Os-x86 (son petit programme dans l'invité) inscrit Wine comme programme qui ouvre les `.exe` : c'est le binfmt de Linux, différent de celui de Kazo. D'après sa recette de construction, le paquet Wine d'Alpine ne le fait pas.
> 14. Le petit outil de Wine qui crée des raccourcis Linux (winemenubuilder) est coupé pour les lancements normaux. Os-x86 ne l'appelle que pour extraire une icône en PNG, dans l'invité : pour ses propres fenêtres (64) et à la demande de BoxedWine.
> 15. Fermer ce qui affiche un programme (la fenêtre BoxedWine, la fenêtre simple d'Os-x86 ou l'onglet du terminal) arrête ce programme dans l'invité ; si cette fenêtre ou la commande plante, pareil (ça élargit 61.4).

- A) Oui, tous ✨(Recommandé)
- B) Oui, sauf : (donne les numéros)
- C) Autre : (Spécifiez)

### Le terminal, le presse-papiers, le son et la souris

**73. Comment utilise-t-on un sous-système Linux dans le terminal?**
> La commande `x86` (60), le lancement des ELF (13) et le mode brut du terminal (27) sont décidés. Il reste la forme de la commande et le canal du shell.

- A) Une commande `x86` à sous-commandes ✨(Recommandé)
  - `x86 list`, `start`, `stop`, `shell <sous-système>`, `run <sous-système> -- <commande>`, et `--bouteille=<nom>` pour viser une bouteille (69). `x86` tout seul ouvre un shell dans ton Linux par défaut, comme `wsl`. La syntaxe exacte sera dans la spécification.
  - Le shell passe par l'agent d'Os-x86 : un vrai terminal, plusieurs sessions à la fois. Sans agent (une distribution que tu apportes), par la console série : un port série émulé, une seule session, plus lente.
  - Un bouton « Terminal » n'existe que dans une app qui gère les sous-systèmes à l'écran. Avec 63 A, il n'y en a pas (BoxedWine ne montre des autres VM que leur nom, leur mémoire et « Arrêter », 66) : `x86 shell` suffit. Avec 63 B, il est dans l'app Sous-systèmes ; avec 63 C, dans BoxedWine, qui aurait alors besoin de « shell.host » approuvée par un admin.
- B) Comme A, mais seulement les options courtes de `wsl` (`-l`, `-d`, `-e`, `--shutdown`)
- C) Comme A, mais sans agent : console série seulement
- D) Autre : (Spécifiez)

**74. Jusqu'où va le presse-papiers?**
> Chaque VM en marche a son propre presse-papiers, commun à tous ses programmes (22). Vers ton vrai ordinateur, Firefox et Safari n'acceptent d'écrire que pendant un geste de ta part (un clic, une touche), et Chrome aussi, sauf permission : une copie faite dans l'invité peut arriver trop tard et se perdre.

- A) Du texte dans les deux sens, quand une fenêtre de l'invité a le focus ✨(Recommandé)
  - De Kazo vers l'invité quand une fenêtre de cette VM prend le focus ; de l'invité vers Kazo quand un programme copie pendant qu'une fenêtre de cette VM a le focus.
  - Par l'agent d'Os-x86, sans la limite de 64 Kio de l'autre voie offerte par v86.
  - Plafonné par l'app qui affiche la vue : BoxedWine et les fenêtres d'Os-x86 déclarent la lecture et l'écriture du presse-papiers.
  - **Limite (61.9) :** ce que Kazo pousse dans une VM, tout programme de cette VM peut le lire, même celui d'une autre app.
  - Vers ton ordinateur, un petit ajout à l'ADR du presse-papiers de Kazo (0065) réessaie la copie à ton clic ou à ta touche suivante.
  - Images et fichiers plus tard.
- B) Du texte synchronisé en continu, même sans focus, comme VirtualBox
- C) Texte, images et fichiers dès la v1
- D) Pas de presse-papiers en v1
- E) Autre : (Spécifiez)

**75. Comment gère-t-on le son?**
> Comme carte son, v86 n'émule que la Sound Blaster 16 (plus le haut-parleur du PC) ; c'est aussi la carte de DOS et de Windows 9x. Chaque VM mélange le son de ses programmes : Kazo reçoit un flux par VM en marche. Kazo n'a aucune API de son. Le son de v86 saccade et se répète quand Kazo est occupé (bogue connu de v86).

- A) La Sound Blaster 16, un flux par VM, mélangés par Os-x86 ✨(Recommandé)
  - Os-x86 joue le son d'une façon qui ne dépend pas de l'occupation de Kazo, avec une solution de repli quand le navigateur ne le permet pas.
  - Un bouton muet dans chaque vue coupe le son de toute sa VM. Un programme console lancé au terminal n'a pas de vue : on coupe le son de sa VM avec `x86`.
  - Le volume se règle pour chaque sous-système avec `x86`, ou dans BoxedWine pour les VM Programmes Windows.
  - Le son est un périphérique du profil (34), coupé par défaut si la maquette du jalon 0 le juge trop coûteux.
  - Une API de son dans le SDK de Kazo viendra plus tard, par un ADR.
- B) Un flux par programme dès la v1, avec un bouton muet par fenêtre
- C) Pas de son en v1
- D) Créer d'abord l'API de son du SDK de Kazo
- E) Autre : (Spécifiez)

**76. Comment partage-t-on la souris et le clavier?**
> Une souris « absolue » suit exactement ton pointeur ; une souris « relative » ne reçoit que des déplacements, et il faut alors la capturer, comme dans un jeu. Une fenêtre de programme Windows peut presque toujours utiliser une souris absolue (sauf les jeux qui capturent la souris). Un écran de VM entier (bureau Linux, DOS, VM complète) en demande souvent une relative.
>
> Aujourd'hui, Kazo prend ses raccourcis avant toute app (Alt+Q ferme la fenêtre), et aucune app ne peut les lui demander. Le verrouillage du clavier (qui laisse une page recevoir Échap, Ctrl+W, etc.) n'existe que dans Chrome et Edge, en plein écran. v86 envoie les touches physiques : l'invité doit avoir la même disposition de clavier que toi.

- A) Pas de capture dans les fenêtres de programmes, capture sur les écrans de VM ✨(Recommandé)
  - **Fenêtres de programmes :** souris absolue, sans capture en v1 ; les raccourcis de Kazo gardent la priorité, le reste va au programme. Les jeux qui capturent la souris marcheront mal.
  - **Écrans de VM :** un clic capture la souris et le clavier. Kazo garde toujours une touche pour te libérer, affichée à l'écran : Ctrl droite seule, comme VirtualBox, réglable. Pas Ctrl + Alt droite : sous Windows, le navigateur voit AltGr comme Ctrl + Alt droite, et taper @ ou [ te libérerait.
  - Pendant la capture, Kazo laisse passer ses autres raccourcis à la VM, grâce à un ADR Kazo « une fenêtre peut tenir le clavier ». Il n'accorde le clavier qu'après un clic de ta part dans la fenêtre. Hors du plein écran de Chrome ou d'Edge, les raccourcis du navigateur (Ctrl+W, Ctrl+T, Échap…) restent au navigateur.
  - **Disposition du clavier :** une valeur par sous-système, réglée avec `x86`, ou dans BoxedWine pour les VM Programmes Windows ; détectée automatiquement dans Chrome et Edge.
- B) Comme A, sans ADR : même en capture, les raccourcis de Kazo gardent la priorité
- C) Capture partout, aussi dans les fenêtres de programmes
- D) Autre : (Spécifiez)

---

La création d'un sous-système (choisir un modèle tout prêt ou tout régler toi-même) attend tes réponses à 63 et 67.

Aucun code n'a été écrit.

On retient mes recommandations pour les questions 62 à 76? Une ligne vide ou « ok » veut dire oui ; sinon, réponds par numéro (par exemple « 63 B, 72 B sauf 8 »).
