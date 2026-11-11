# Série 11 — envoyée le 2026-11-11

Les quinze points de la section N du [registre réconcilié](registre-reconcilie.md), regroupés en treize questions (160 à 172). Même protocole : une lettre par numéro ; une recommandation ✨ par question ; « Autre : (Spécifiez) » toujours possible. Rien n'est accepté sans ta réponse par numéro (ta règle du 2026-11-10). Deux points de la section N ne sont pas posés ici : l'image de base dans l'export (115) attend l'essai E15, et ce qui n'est que de la rédaction (format de profil, syntaxe exacte des commandes) ira dans le plan, à valider là.

Rappels : « bouteille » = préfixe Wine d'un programme ; « VM Programmes » = la machine Linux d'Os-x86 où tournent Wine, FreeDOS ou un Linux ; « relais » = le serveur de Kazo qui ouvre les vraies connexions Internet ; « réseau simulé » = le réseau interne de Kazo, HTTP seulement aujourd'hui ; « en-tête » = les premiers octets d'un fichier (MZ = DOS, MZ+PE = Windows, ELF = Linux) ; « ADR » = décision d'architecture écrite dans le dépôt Kazo avant le code.

---

### Admin et permissions

**160. Le contrôle total de l'admin (140 B, 156) : le rôle suffit-il, ou faut-il en plus une authentification à chaque opération?**
> Tu as choisi l'administration graphique dès le premier jalon (sections VM et utilisateurs dans les Paramètres) et un vrai rôle Kazo déclaré par la Feature. Fait vérifié : dans Kazo, toute opération d'administration exige une permission dangereuse **et** une authentification d'admin dessinée par l'OS à chaque opération, même pour un admin (mot de passe, billet de 30 s). Reste à dire ce que le rôle change à ça, et ce que devient « gérer les sous-systèmes » (D32).

- A) Le rôle donne l'accès aux pages et aux commandes ; chaque opération sensible (124.1, 124.4, 124.5 : régler, supprimer, partager une VM commune ; importer une image partagée ; changer une limite ou un veto) demande en plus l'authentification de l'OS, comme partout dans Kazo. « Gérer les sous-systèmes » disparaît : c'est le rôle, pas une permission d'app ✨(Recommandé)
  - Même règle que Kazo pour ses propres admins ; l'ADR « rôles déclarés par une Feature » donne aux pages de la Feature l'accès à l'agent d'authentification de l'OS. Une app ordinaire (BoxedWine) n'agit jamais sur les VM des autres : elle ouvre la page des Paramètres.
- B) Le rôle suffit : une fois connecté avec ce rôle, tout est permis sans authentification par opération
  - Plus simple, mais moins que ce que Kazo exige de ses propres admins ; une session laissée ouverte suffit à tout faire.
- C) Comme A, mais « gérer les sous-systèmes » reste une permission d'app, pour qu'une app (BoxedWine ou autre) puisse administrer toutes les VM avec l'authentification de l'OS à chaque opération
  - L'ADR du premier jalon grossit (agent d'authentification exposé aux apps, pas seulement aux pages de la Feature).
- D) Autre : (Spécifiez)

### Réseau

**161. Le réseau simulé en V1 (116) : quelle version?**
> Tu veux à terme que chaque VM soit un hôte et que Kazo ait sa propre adresse (§14), version complète « peut-être en V2 ». Faits : le réseau simulé de Kazo est HTTP seulement, sans adresse IP ; E6 a montré qu'un réseau Ethernet privé entre VM, avec une adresse par bouteille et sans NAT, étiquette le trafic de façon fiable jusqu'au relais, et que le mode `inbrowser` de v86 est bloqué par la configuration de la page.

- A) V1 : un réseau Ethernet privé d'Os-x86 entre les VM (une adresse par VM et par bouteille), sortie réelle par le relais de Kazo avec les permissions et les limites ; aucune liaison avec le réseau simulé de Kazo. V2 : la passerelle HTTP et l'ADR « hôtes enregistrés par une Feature » (les VM deviennent des hôtes nommés de `kazodns`) ✨(Recommandé)
  - Tout ce qu'E6 a mesuré ; aucun ADR Kazo réseau avant V2. Limite affichée : en V1, le navigateur de Kazo ne joint pas un serveur qui tourne dans une VM.
- B) La passerelle HTTP dès V1 (ADR Kazo au premier jalon) : une VM joint les apps HTTP de Kazo et Kazo joint les serveurs HTTP des VM par un nom
  - Un ADR de plus avant le premier jalon ; les services non HTTP restent entre VM seulement.
- C) Aucun réseau entre VM en V1 : chaque VM ne voit que le relais
  - Le plus simple ; deux VM ne se parlent pas.
- D) Autre : (Spécifiez)

### Lancement au terminal

**162. La règle contenu / extension du shell (72.2, 111) : laquelle?**
> Tu préfères le contenu détecté à l'extension, sous réserve d'exceptions (§5). Décidé : MZ+PE → association `.exe` → BoxedWine ; MZ pur (DOS) → Os-x86/FreeDOS ; ELF → Os-x86/Linux ; `.com` au double-clic → BoxedWine qui confie à Os-x86. Faits : un `.com` n'a pas d'en-tête ; un `.bat` est du texte, que le shell de Kazo exécuterait aujourd'hui comme script hsh ; `.msi` est un conteneur sans en-tête de programme.

- A) L'en-tête décide quand il existe ; l'extension décide sinon ; une contradiction refuse ✨(Recommandé)
  - MZ+PE ou MZ+NE → BoxedWine ; MZ pur → Os-x86 (DOS) ; ELF → Os-x86 (Linux) ; sans en-tête reconnu : `.com`, `.bat`, `.cmd`, `.msi` → l'app associée à l'extension (BoxedWine, qui confie un `.com` à Os-x86) ; ni en-tête ni extension connue → le shell fait comme aujourd'hui (script). Contradiction (par exemple `photo.png` avec un en-tête PE) : refus avec un message clair, jamais exécuté. Même règle au terminal et au double-clic ; seul l'affichage diffère (154).
- B) L'extension décide toujours ; l'en-tête ne sert qu'à afficher un avertissement
  - Plus prévisible pour l'usager ; un `.exe` DOS irait à BoxedWine (ça change 111).
- C) Autre : (Spécifiez)

**163. `boxedwine setup.exe` tapé au terminal (72.8) : comme `./setup.exe`, ou comme un double-clic?**
> `boxedwine` est le nom court de la fenêtre de BoxedWine. Fait : le shell lance une fenêtre par son nom avec les flux du shell et attend sa fermeture.

- A) Comme `./setup.exe` : console dans le terminal (154), le terminal attend la fin ✨(Recommandé)
  - Une seule façon de se comporter au terminal.
- B) Comme un double-clic : BoxedWine s'ouvre, le terminal rend la main tout de suite
- C) Autre : (Spécifiez)

**164. DOS graphique (111 contre 154 B) : qui l'affiche?**
> 111 envoie un programme DOS tapé au terminal « directement à Os-x86, sans BoxedWine » ; ta réponse à 154 (« terminal de Kazo ») règle le mode texte ; l'option 154 B que tu as choisie disait aussi « DOS graphique passe par BoxedWine comme Windows ». Os-x86 n'ouvre aucune fenêtre (106).

- A) Un programme DOS qui passe en mode graphique est affiché par BoxedWine, comme un programme Windows ; sans BoxedWine installé, message « installe BoxedWine » et le programme est arrêté ✨(Recommandé)
  - Cohérent avec 106 et 154 B ; le mode texte reste dans le terminal, sans BoxedWine.
- B) DOS graphique en V2 ; en V1, un programme DOS qui passe en graphique est arrêté avec un message
- C) Autre : (Spécifiez)

### Plusieurs VM

**165. Quand poser la question « une autre VM tourne » (66), et ce que « créer une nouvelle VM » crée (67)?**
> Décidé : plusieurs VM si les ressources le permettent ; l'usager voit les VM actives et peut fermer une autre VM, utiliser un environnement compatible ou en créer un. Faits : E1 et E6 mesurent environ 1,2 à 1,5 Gio de mémoire navigateur par VM de 512 Mio à 1 Gio ; v86 n'utilise qu'un cœur par VM ; ta cible est 8 Go minimum.

- A) Pas de question tant que la mémoire des VM en marche plus la nouvelle reste sous la limite (défaut : la moitié de la mémoire que le navigateur annonce, réglable par l'admin) ; au-delà, la fenêtre de choix (fermer, réutiliser, créer). « Compatible » = même genre (Programmes Windows, Linux, DOS) et même image. « Créer une nouvelle VM » = depuis un profil (121) ou en copiant les réglages de la VM courante ✨(Recommandé)
  - Aucune question dans le cas courant ; la limite est une règle admin de 122.
- B) Toujours demander dès qu'une autre VM tourne
- C) Autre : (Spécifiez)

**166. Qu'est-ce qu'une « application » pour l'arrêt automatique d'une VM (§7)?**
> Décidé : une VM démarrée pour des applications s'arrête dès que toutes ses applications sont fermées ; une VM démarrée à la main reste. Reste à dire ce qui compte.

- A) Compte : un programme lancé (console ou graphique), un shell interactif (119), une console de bouteille (145). Ne compte pas : un processus de fond laissé par un programme fermé, ni une fenêtre d'affichage sans programme ✨(Recommandé)
  - Un programme qui laisse un service tourner ne garde pas la VM vivante ; la VM démarrée à la main reste la façon de garder un service.
- B) Seuls les programmes lancés comptent ; un shell ou une console ouverts n'empêchent pas l'arrêt
- C) Autre : (Spécifiez)

### Console, bibliothèque, mises à jour

**167. Fermer la console quand elle a lancé un programme graphique ou détaché (147)?**
> Décidé : si la console attend le programme, la fermer termine le programme.

- A) Un programme graphique lancé depuis la console continue dans sa bouteille, avec sa fenêtre BoxedWine ; un processus détaché sans fenêtre est terminé avec le shell ✨(Recommandé)
  - Rien d'invisible ne survit à la console ; ce qui a une fenêtre se ferme par sa fenêtre.
- B) Tout ce que la console a lancé est terminé avec elle
- C) Autre : (Spécifiez)

**168. Désinstaller un programme après le retrait de son entrée de bibliothèque (129)?**
> Décidé : retirer une entrée laisse le programme installé ; supprimer une bouteille efface tout, avec confirmation (124.3).

- A) Une action « Désinstaller » dans BoxedWine lance le programme de désinstallation du programme dans sa bouteille (droits de la bouteille, 145) ; s'il n'en a pas, BoxedWine propose de supprimer la bouteille ✨(Recommandé)
- B) Pas de « Désinstaller » : seulement « supprimer la bouteille »
- C) Autre : (Spécifiez)

**169. Mise à jour indispensable quand le moteur ne démarre plus l'ancienne image (127)?**
> Décidé : une app qui exige une image récente → « mettre à jour ou annuler » ; les autres mises à jour se reportent. Ce cas-ci n'avait pas été présenté. Fait : un état sauvegardé de v86 est lié à sa version ; une image disque, elle, reste lisible.

- A) Os-x86 s'engage à démarrer les images de la même version majeure ; si une nouvelle version d'Os-x86 ne sait plus démarrer une image, elle le dit avant l'installation (notes de version, avertissement du Store) et au lancement propose « mettre à jour l'image maintenant » ou « annuler », avec un point de restauration automatique avant. Retour arrière = restaurer ce point et réinstaller l'ancienne version d'Os-x86 depuis le Store, si le Store la garde (à vérifier dans Kazo) ✨(Recommandé)
- B) Jamais de rupture : Os-x86 garde la capacité de démarrer toutes les images passées (coût : code de compatibilité à maintenir)
- C) Autre : (Spécifiez)

### Périmètre

**170. Wine graphique (153) : comment juger « trop complexe »?**
> Tes mots : visé en fin de V1, « si trop complexe » au début de V2. Le bogue des fenêtres Wine à 1x1 pixel (E1, E4) n'est pas résolu ; E13 donne un autre Wine à essayer (paquets i386 de WineHQ, 11.0 et 11.21).

- A) Un essai dédié d'une journée, maintenant, avec le Wine de WineHQ ; s'il résout le bogue, Wine graphique reste en fin de V1 ; sinon, une seconde boîte d'une journée sur le code de Wine ; après deux échecs, « trop complexe » : V1 se clôt sans lui, il ouvre V2 ✨(Recommandé)
  - Critère mesurable, deux jours au plus avant de trancher.
- B) Pas d'essai maintenant : on décide à la fin de V1, selon le temps restant
- C) Autre : (Spécifiez)

**171. Un vrai Windows invité (C18) : V2 avec un essai de faisabilité en V1?**
> Tu le voulais en V1 « si peu de changements ». E13 : v86 démarre Windows 10 et 7 32 bits depuis octobre (NX), mais un Windows utilisable demanderait un disque de 32 Go, pas d'état gabarit (licence), ni 9p ni agent, et aucun son (pas de pilote SB16 après XP).

- A) V2, avec en V1 un essai de faisabilité d'une journée (démarrage, temps, mémoire) qui fixe le périmètre V2 ✨(Recommandé)
- B) V1 quand même, en mode « VM complète sans intégration » (écran, clavier, souris, disque ; rien d'autre)
- C) Autre : (Spécifiez)

### Noms

**172. Les identifiants à fixer avant la première publication (D35) : ceux-ci?**
> Faits : un identifiant d'app Kazo est en minuscules, de la forme `a.b.c` ; les apps système de Kazo sont `org.kazoos.*` ; `Os-x86` avec tiret n'est pas un espace de noms C# ; le nom NuGet `Os-x86.Interface` est décidé (33 D) ; un nom qui commence par `Kazo.Os.` ferait échouer la compilation des apps clientes (règle KOSAPP0030, à revérifier pour les espaces de noms). Le dépôt BoxedWine n'a encore que sa licence.

- A) Feature `org.kazoos.x86` (points d'entrée `org.kazoos.x86.cli` pour `x86`, `.permission` pour la fenêtre de permission, `.rules` pour la page des règles, `.admin` pour la page des Paramètres) ; app `org.kazoos.boxedwine` ; espaces de noms C# `OsX86.Interface` (contrats), `OsX86` (Feature et x86-Lib), `BoxedWine` et `BoxedWine.Lib` (BwLib, `IBwLib`) ; paquets NuGet `Os-x86.Interface` (décidé) et `BoxedWine.Lib` ✨(Recommandé)
  - À revérifier avant publication : que `org.kazoos.*` n'est pas réservé aux apps système de Kazo (sinon `ca.kazo.x86` et `ca.kazo.boxedwine`).
- B) Autres noms : (Spécifiez)

---

On retient quoi pour 160 à 172? Réponds par numéro (par exemple « 160 A, 161 B, 172 A sauf l'espace de noms ») ; une ligne vide n'accepte rien.
