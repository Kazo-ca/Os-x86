# Série 14 — envoyée le 2026-11-11

Les propositions des deux essais du jalon 0 : E7 « agent et processus » ([../essais/e7-agent-processus.md](../essais/e7-agent-processus.md)) et E14 « DOS et console dans le terminal » ([../essais/e14-dos-terminal.md](../essais/e14-dos-terminal.md)). Même protocole : une lettre par numéro, deux choix réels au moins, une recommandation ✨, « Autre » toujours possible ; rien n'est accepté sans ta réponse par numéro. Numéros 205 à 218.

Ce qui est déjà réglé par les règles que tu as validées, sans question : les deux critères sont atteints (E7 : A arrêté sans toucher B ni la VM, 3 runs sur 3, 22 contrôles console sur 22 ; E14 : programme DOS texte et `cmd` Wine utilisables au terminal sous 100 ms), donc **les conséquences d'échec du plan ne s'appliquent pas** (ni « VM = un processus Kazo sans enfants » pour tout le monde, ni « console dans une fenêtre BoxedWine ») et les études du jalon 0 sont finies. Restent E11, E15, E2 bis, E9, E12 bis, E16 et la faisabilité Windows (171), qui ne bloquent pas J0.

---

## E7 : agent et processus

**205. Le protocole de l'agent (§1.1 du plan, D61.3)**
> Mesuré : un agent C statique de 108 Kio (170 Kio de RSS) qui lit une requête JSON par ligne sur un port virtio-console réservé à root (`hvc1`, 0600) et répond une ligne JSON portant le même `id`, avec des événements (`ready`, `exit`, `open-url`, `console-closed`) : 3,6 ms par aller-retour (190 ms par la console série), `launch` 10 à 24 ms, `kill` 10 à 20 ms, `list-processes` 0,4 à 0,7 s, prêt avant le shell série, boot inchangé. Trois défauts du prototype : une opération longue bloque les autres (`exec-in-prefix`), `list-windows` passe par `xwininfo`/`xprop` (3 à 5 s, liste vide pendant 1 à 3 s quand un client X meurt), une seule console à la fois.

- A) **Ce protocole comme base** (JSON par ligne, `id`, événements, opérations `ping`, `list-processes`, `launch`, `kill` par pid/groupe/bouteille/cgroup, `list-windows`, `mounts`, `exec-in-prefix`, `open-console`/`close-console`), avec trois corrections à l'écriture : une boucle non bloquante (un enfant par opération longue), les fenêtres suivies par une connexion X permanente (`MapNotify`/`DestroyNotify`, `_NET_WM_PID`), et plusieurs consoles par multiplexage sur un second port (un identifiant de console par trame) ✨(Recommandé)
  - Ce qui a été mesuré ; lisible dans les journaux ; un seul canal de commande.
- B) Comme A, mais un port virtio-console par console ouverte (v86 en déclare 4 : l'agent, puis 3 consoles au plus) : plus simple, plafond bas
- C) Protocole binaire (CBOR ou MessagePack) plutôt que JSON : moins d'octets, illisible dans les journaux, aucun gain mesurable à 3,6 ms
- D) Autre : (Spécifiez)

**206. L'unité de lancement et d'arrêt (ADR 17 « processus invités »)**
> Mesuré sur un arbre parent + deux enfants + un démon `setsid` : tuer le pid laisse 3 survivants sur 3 ; tuer le groupe en laisse 1 (le démon) ; `cgroup.kill` d'un cgroup v2 par lancement n'en laisse aucun, en 19 à 42 ms, et 256 processus d'une bombe en 7 ms ; toute une bouteille Wine (10 processus) en 1,2 ms sans toucher l'autre. Le pid rendu par `launch` est celui de `start.exe` sous Wine 11, pas du programme.

- A) **Un lancement = un cgroup v2** (`<prefix>/<seq>`), créé par l'agent avant `exec` ; arrêter = `SIGTERM` au programme puis, après un délai, `cgroup.kill` ; la liste des processus et des fenêtres est filtrée par cgroup ; l'identifiant du lancement, pas le pid, est ce qu'Os-x86 expose ✨(Recommandé)
  - Atomique, mesuré, et c'est aussi ce qui porte la console (208).
- B) Pid et groupe de processus seulement (pas de cgroup) : plus simple, mais laisse des orphelins et des démons, mesuré
- C) Autre : (Spécifiez)

**207. Les programmes invités dans le modèle de processus de Kazo (ADR 17, ADR 0125, #431)**
> Lu : Kazo n'a pas de processus « étranger » ; le plus proche est la tâche `KazoBackground` (ADR 0128, 0140) ; l'ADR 0125 pose des limites par processus (CPU, RSS, nombre) ; `StartChild` ne vérifie aucune permission (#431). Mesuré : une VM = 1,4 à 2,3 Gio dans le renderer ; un lancement = un cgroup dont l'arrêt est atomique ; la liste coûte 0,4 s ; `wine notepad` fait 10 processus Linux.

- A) **La VM est le processus Kazo** qui porte les limites de l'ADR 0125 (RSS = mémoire de la VM + surcoût mesuré, CPU = un cœur) ; **chaque lancement (= cgroup) est un processus Kazo enfant** de la VM, avec la bouteille pour identité, sans limites propres ; les processus invités créés par un programme ne sont pas exposés (ils meurent avec leur cgroup) ; une console est un enfant comme un autre ; Os-x86 enregistre l'enfant à la réponse de `launch` et le retire à l'événement `exit` ; rien ne passe par `StartChild` ✨(Recommandé)
  - Une limite une fois, au bon endroit ; un enfant par programme, pas par processus Linux.
- B) Chaque processus invité est un processus Kazo (10 pour Notepad) : fidèle, mais une liste de 0,4 s à chaque rafraîchissement et des limites par processus que la VM ne sait pas mesurer
- C) La VM seule est un processus Kazo, les programmes ne sont pas exposés (le modèle simplifié prévu en cas d'échec) : plus simple, mais « arrêter A sans arrêter B » disparaît de Kazo
- D) Autre : (Spécifiez)

**208. Fermer la console (147, 167 A) avec le cgroup**
> Mesuré : `SIGHUP` au shell de la console ne termine ni un démon (`sleep &`) ni `winecfg` lancés depuis elle ; `cgroup.kill` du cgroup de la console les termine tous en 170 à 200 ms sans toucher aux lancements normaux de la même bouteille. 167 A veut qu'un programme graphique lancé depuis la console continue et qu'un processus détaché sans fenêtre soit terminé avec le shell. Mécanisme disponible, non mesuré : déplacer un processus dans un cgroup frère (écrire son pid dans un autre `cgroup.procs`).

- A) **Ce qui est lancé depuis la console reste dans son cgroup ; à la fermeture, l'agent déplace dans un cgroup frère (un nouveau lancement) les processus qui ont une fenêtre** (pid de fenêtre par `_NET_WM_PID`, leurs enfants avec eux), puis `cgroup.kill` du reste : 167 A tel quel, mesurable ✨(Recommandé)
  - Garde 167 A ; un mécanisme que l'agent a déjà.
- B) Tout ce qui est lancé depuis la console meurt avec elle, fenêtre ou non (revient sur 167 A ; plus simple, et le comportement d'un terminal Linux)
- C) Seul le shell meurt (`SIGHUP`) ; démons et programmes graphiques survivent jusqu'à l'arrêt de la VM (revient sur 167 A pour le détaché)
- D) Autre : (Spécifiez)

**209. La réserve de 102 (environnements partagés, utilisés à tour de rôle)**
> Mesuré : avec un usager invité par bouteille (home 0700 sur 9p remonté `nosuid,nodev`), un programme Wine n'écrit que dans son home/préfixe et dans `/tmp/.wine-<uid>` (0700, illisible par l'autre) ; rien dans `/var/tmp`, `/dev/shm`, `/run`, `/srv`, rien chez l'autre bouteille. Ce que l'autre atteint : le serveur X partagé (fenêtres, presse-papiers, frappe), limite déjà notée en E6. Non testé : un installateur, un programme qui crée un service Windows.

- A) **Lever la réserve de 102 pour les fichiers** (un usager invité par usager Kazo, comme E6 ; ce que l'usager suivant voit est son propre préfixe et `/tmp` collant) ; la réserve ne porte plus que sur l'affichage partagé, traité par l'affichage fenêtre par fenêtre (E4) ou un serveur X par bouteille, en V2 ✨(Recommandé)
  - Les faits sont là pour les fichiers ; l'affichage est un sujet connu et distinct.
- B) Garder la réserve entière jusqu'à un essai avec un installateur et un service Windows
- C) Autre : (Spécifiez)

**210. Le durcissement de l'image (recette, E8)**
> Mesuré : trois trous trouvés par l'inventaire des dossiers inscriptibles, hors des contrôles prévus : la racine `/` est en 0777 (mode du répertoire racine d'un tar de `docker export`), `/run` est en 1777 (défaut du tmpfs), `/dev/fb0` était lisible par un usager de bouteille. Le reste de la recette (usager par bouteille, `user.max_user_namespaces=0`, aucun setuid, `/home` et `/tmp` en `nosuid,nodev`, ports de l'agent en 0600, `RLIMIT_NPROC` 256) tient 22 contrôles sur 22.

- A) **Ajouter les trois corrections à la recette** (`chmod 755 /`, `/run` en `mode=0755`, `/dev/fb0` et `/dev/input/*` en 0600) **et un test d'image dans la CI** qui rejoue l'inventaire `find / -perm -o+w` et les 22 contrôles R134.5 à chaque image publiée ✨(Recommandé)
  - Ce qui a échappé aux contrôles a été trouvé par l'inventaire : il devient un test.
- B) Les trois corrections seulement, sans test automatique
- C) Autre : (Spécifiez)

**211. Le disque interne vu depuis Kazo (§11, V2)**
> Mesuré : un lecteur ext4 minimal lit `drive_c` en 2 à 24 ms depuis l'hôte, mais l'image exportée **sans `sync` est incohérente** (deux fichiers manquants aux trois runs) ; cohérente après `sync` ou `umount` ; le journal ext4 n'est pas rejoué. VM en marche : l'agent est la seule source cohérente (`find` en 200 ms, `exec-in-prefix` 100 ms).

- A) **Disque interne en V2 comme prévu, avec la règle écrite dès maintenant** : « à l'arrêt » = lecture seule après un arrêt propre de la VM (`umount`) ; VM en marche = par l'agent uniquement ; jamais de lecture hôte sur une image montée ✨(Recommandé)
  - Évite une exploration qui montre un état faux.
- B) Une forme V1 par l'agent seulement (lister et copier par `exec-in-prefix`, sans lecteur ext4 côté hôte) : moins de code, mais seulement VM en marche
- C) Autre : (Spécifiez)

**212. Les images sans agent (195 A : tableau des capacités)**
> Mesuré sur Alpine nue : boot 67 s, une console série root à 190 ms par commande, bloquée par toute commande longue ; aucune liste ni arrêt par programme, aucune bouteille, aucun événement. FreeDOS et un vrai Windows n'ont même pas la console série.

- A) **Une image sans agent = une VM qui est un processus Kazo sans enfants** (le modèle simplifié, de fait) ; écran, clavier, souris, disque et réseau de la page seulement ; la console série n'est offerte que si le profil de l'image (`.x86profile`) déclare un port série ; la page de la VM le dit à l'usager ✨(Recommandé)
  - Pas de promesse que l'image ne tient pas ; le profil porte l'information.
- B) Os-x86 tente d'injecter un agent minimal dans toute image Linux apportée (par 9p au boot) : plus de capacités, mais fragile et impossible pour DOS et Windows
- C) Autre : (Spécifiez)

## E14 : DOS et console dans le terminal

**213. La voie de la console Wine dans le Terminal (154 B, 162)**
> Mesuré : les deux voies tiennent le critère avec la même latence (≈ 20 ms par rafraîchissement à 33 ms de sondage) : (a) la console VGA tty1 lue par `get_text_screen`, (b) un pty relié à un port série ou virtio-console, rendu par un émulateur VT dans la page. Mais tty1 est unique par VM et exige le mode texte VGA, **incompatible avec Xorg et le pilote DRM `bochs` d'E17** ; le pty permet plusieurs programmes à la fois, une taille choisie au lancement (`stty size` vérifié), le code de sortie écrit par le lanceur sur le même canal (110) et Ctrl+C transformé en SIGINT par la discipline de ligne (code 58 dans les deux voies). Coût : un émulateur VT (xterm réduit de 250 lignes ici, ou xterm.js).

- A) **Voie pty : l'agent (E7) ouvre un pty par programme console et relaie ses octets vers le Terminal par virtio-console ; le Terminal rend les séquences avec un émulateur VT** (xterm.js, ou un émulateur réduit) ; code de sortie et Ctrl+C sur le même canal ; un port série n'est jamais partagé entre deux programmes ✨(Recommandé)
  - Plusieurs consoles, compatible avec la VM graphique, rien à faire pour Ctrl+C.
- B) Voie VGA tty1 pour la console Wine : pas d'émulateur VT, mais une seule console par VM et pas de programme console pendant que Xorg tourne
- C) Autre : (Spécifiez)

**214. DOS texte au Terminal : voie et fréquence (111, 154 B)**
> Mesuré : FreeDOS n'a pas de pty ; la voie VGA (`get_text_screen` ou mémoire texte, comparaison par ligne, attributs 16 couleurs) coûte 0,2 à 0,3 ms par tour. Latence touche → rendu à 33 ms de sondage : ≈ 20 ms pour Wine, ≈ 50 ms (max 95 ms) pour un DOS oisif, parce que v86 ne réveille pas le CPU en HLT à l'arrivée d'une touche mais au tic du PIT (55 ms) ; sans `FDAPM` : 14 ms mais 100 % de CPU. 16 ms de sondage gagne 10 ms pour 1,5 % de CPU ; 100 ms rate le critère.

- A) **Voie VGA à 33 ms de sondage**, comparaison par ligne, attributs rendus, `FDAPM APMDOS` dans l'image FreeDOS ; un onglet de terminal par VM ; proposer en amont à v86 le réveil en HLT sur l'IRQ clavier, sans en dépendre ✨(Recommandé)
  - Sous le critère, CPU négligeable ; le reste est un gain de confort possible.
- B) 16 ms de sondage : 10 ms de mieux pour 1,5 % de CPU en plus
- C) Autre : (Spécifiez)

**215. Ce que l'ADR 10 demande au Terminal de Kazo**
> Mesuré : tout le jeu de touches de Kazo passe (lettres, Shift, Ctrl+lettre, flèches, Tab, Retour, Entrée, Échap) ; sans Alt ni F1-F12, EDIT ne peut ni ouvrir, ni sauver, ni quitter par le menu (Ctrl+N/O/S existent, quitter non), nano perd ses raccourcis Méta, `mc` et `htop` leurs F-touches. Un accord Échap+lettre / Échap+chiffre, traduit par le Terminal en Alt+lettre / F1-F12, ouvre le menu Fichier d'EDIT si Alt est « tenu » (trois envois espacés de 40 ms). Les éditeurs attendent Home/End/Suppr/Page.

- A) **L'ADR 10 demande** : une surface « écran plein » à largeur fixe (80 colonnes, 25 lignes ou plus) avec attributs de couleur par cellule (16 couleurs) et curseur, rafraîchie par remplacement de lignes à ~30 Hz ; l'entrée touche par touche avec Ctrl+lettre ; **l'accord Échap+lettre / Échap+chiffre** traduit en Alt+lettre / F1-F12 ; Home, End, Suppr, Page si Kazo les a ; la taille connue à l'ouverture ; le redimensionnement à chaud en V2 ✨(Recommandé)
  - Rend EDIT, nano et `mc` complets sans mode brut ; l'accord est le mécanisme Méta des terminaux.
- B) Même liste sans l'accord Échap : EDIT ne peut pas quitter par son menu, nano sans Méta, `mc` infirme
- C) Demander à Kazo le mode brut complet (toutes les touches, dont Alt et F-touches) dès V1, alors que D27 le reportait en V2
- D) Autre : (Spécifiez)

**216. Ctrl+C dans le Terminal**
> Mesuré : par le pty, Ctrl+C est un octet 0x03 que la discipline de ligne transforme en SIGINT ; Wine termine le programme avec le code 58 ; la page ne fait rien. Si le Terminal de Kazo réserve Ctrl+C à la fermeture de la commande, rien n'interrompt plus un programme invité en boucle.

- A) **Ctrl+C va au programme invité** (SIGINT par le pty, Ctrl+C DOS par le scancode) ; pour tuer le programme lui-même : Ctrl+Pause si Kazo l'a, sinon un bouton du Terminal ou `x86 kill` depuis un autre onglet ✨(Recommandé)
  - C'est ce que les programmes attendent ; tuer reste possible.
- B) Ctrl+C ferme la commande Terminal et tue le cgroup du lancement : simple, mais plus d'interruption douce
- C) Autre : (Spécifiez)

**217. DOS graphique pendant une session au Terminal (164 A, précision)**
> Mesuré : v86 émet `screen-set-size` avec `bpp ≠ 0` 63 à 109 ms après le passage en mode graphique, et `bpp 0` au retour en texte ; `get_text_screen` ne se vide pas (écran figé) ; au boot, la taille texte passe par 80x256 et 80x16. 164 A dit : DOS graphique affiché par BoxedWine comme un programme Windows ; sans BoxedWine, message et arrêt. Reste à dire ce qui arrive à une VM DOS **déjà lancée par le Terminal** quand le programme passe en graphique.

- A) **Le signal est `screen-set-size` avec `bpp ≠ 0` ; BoxedWine reprend la VM DOS déjà lancée** (même VM, le canvas de v86 montré dans une fenêtre BoxedWine, le Terminal l'indique) et la rend au Terminal au retour en mode texte ; sans BoxedWine : message et arrêt (164 A) ✨(Recommandé)
  - Pas de double VM ni de redémarrage ; le signal est fiable.
- B) Le Terminal refuse DOS graphique avec un message et arrête le programme, même avec BoxedWine (l'usager relance par BoxedWine) : plus simple, un redémarrage de VM
- C) Autre : (Spécifiez)

**218. Les dépendances à v86 non documentées (C6)**
> Mesuré : la voie pty exige `uart1: true`, option non documentée de v86 ; le curseur et les attributs du texte n'ont pas d'API publique (lus dans `vga.cursor_address` et par `screen: { ansi: true }` + `screen_adapter.get_text_screen()`) ; v86 ne réveille le CPU en HLT qu'au tic du PIT. E7 : `virtio_console` existe (4 ports) et suffit à la place de `uart1`.

- A) **Préférer `virtio_console` (documenté, 4 ports) à `uart1` pour l'agent et les consoles Linux ; pour DOS, lire le curseur et les attributs par les champs internes en les isolant dans un seul adaptateur d'Os-x86, et proposer en amont à v86 une API publique (curseur, attributs) et le réveil sur IRQ clavier ; pas de fork tant que ces champs existent** ✨(Recommandé)
  - Le minimum de dépendance interne, dans un seul fichier, et des contributions ciblées.
- B) Forker v86 dès maintenant pour y ajouter ces API : maîtrise complète, mais une branche à suivre à chaque version
- C) Autre : (Spécifiez)

---

On retient quoi pour 205 à 218? Réponds par numéro ; une ligne vide n'accepte rien.
