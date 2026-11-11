# Journal des décisions — Os-x86

Débat de conception. Aucun code avant que le plan soit approuvé.
Carte complète des questions et contraintes : [../recherche-2026-11-08/decisions.md](../recherche-2026-11-08/decisions.md) (numérotée C1 à C45 ci-dessous).

## Série 1 — envoyée le 2026-11-08, répondue le 2026-11-09

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 1 | Modèle d'exécution | **Les deux, en couches** : VM complète (sous-systèmes Linux et Windows) + lancement de .exe sans Windows, sur un même cadre de sous-systèmes. Recommandation acceptée. | C1 |
| 2 | Sens de « reproduire MsDos.Clone » | **L'organisation et la méthode** (cœur séparé des adaptateurs, tests systématiques, phases avec l'agent Copilot, hôte de démo autonome), **pas le CPU**. Recommandation acceptée. | C5 |
| 3 | Moteur | **Envelopper v86** dans un Web Worker ; le C# fait l'intégration, derrière une abstraction de moteur qui acceptera un moteur x64. Recommandation acceptée. | C4 |
| 4 | 32-bit et x64 | **Par paliers** : 32-bit avec v86 → Linux x86-64 sur un moteur choisi après banc d'essai → Windows x64. Recommandation acceptée ; statut du palier Windows x64 à préciser (Q17). | C2 |
| 5 | Bac à sable serveur | **Deux projets indépendants aux buts différents.** Le bac à sable serveur sert à *ne pas donner l'exécutable au client*. Os-x86 sert à *exécuter un exécutable sans serveur distant*. Conséquence : Os-x86 n'a pas de plan B serveur pour Windows moderne. | C3 |
| 6 | Livraison | **Mixte** : Marketplace + petits points d'extension génériques dans le noyau, par ADR. Recommandation acceptée, précisée par la décision 7. | C11 |
| 7 | Os-x86 et BoxedWine | **Os-x86 est une fonctionnalité facultative de l'OS, à la manière de WSL.** Détail ci-dessous. La licence d'Os-x86 peut changer. | C3, C12 |
| 8 | Licences | **Le cœur de Kazo OS garde sa licence (CC BY-NC 4.0). Chaque app déclare sa propre licence, indépendante de l'OS.** Le risque signalé est noté sans action prévue : le SDK est livré sous CC BY-NC et lié par des apps GPL. | C16, C17 |

### Décision 7 en détail (tes mots, reformulés)

- Os-x86 est une **fonctionnalité livrée** et installable ; Os-App-BoxedWine **dépend** d'elle. Installer BoxedWine installe Os-x86 en même temps.
- Grâce à elle, **l'OS exécute des .exe dans le terminal**. Elle se comporte un peu comme WSL dans Windows.
- Os-x86 fournit une **interface enregistrée dans l'OS**. Une app la demande à l'OS **sans dépendance dans son binaire** ; l'OS retourne **null** si la fonctionnalité n'est pas installée.
- Une app peut alors **demander l'installation** (usage facultatif) ou **imposer la dépendance** à sa propre installation.
- Une app peut **ouvrir le Marketplace sur un identifiant de paquet** pour faciliter l'installation.

### Ce que Kazo offre déjà pour la décision 7 (code lu le 2026-11-09)

**Ce qui existe :**
- **Dépendances déclarées**
  - Un manifeste liste `Dependencies`, chacune avec une plage de versions et un drapeau `Optional` (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:91, 208-212`).
  - Le catalogue refuse une app dont une dépendance obligatoire manque, et ce refus se propage aux apps qui en dépendent (`Platform/Kazo.Os.Platform.Core/AppCatalog.cs:437-451, 487-523`).
  - Activer une app active aussi ses dépendances déjà présentes (`Apps/AppInstallationService.cs:256`).
- **Ouvrir le Store sur une app** : l'app `org.kazoos.store` accepte `--app=<appId>` (`Apps/System/Kazo.Os.Apps.Store/StoreWindow.razor.cs:41,52`). Une app peut déjà l'appeler par `IAppIntentGateway.LaunchAsync`.
- **Communication entre apps**
  - Intents définis par une app : envoi sans réponse, charge JSON d'au plus 64 Kio (`Simulation.Abstractions/Gateways/AppGatewayContracts.cs:580`).
  - Bus de messages par sujets, avec des permissions exportées (ADR 0040, `Events/TopicMessagingContracts.cs:287`).
  - Contrôle des apps Service (démarrer, arrêter, état).
- **Un seul contexte .NET** : les apps installées se chargent toutes dans le contexte par défaut (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:316`). Un appel typé direct entre une app et une fonctionnalité est donc possible techniquement.
- **Le shell distingue déjà deux cas** : « commande introuvable » (code 127, y compris une commande désinstallée, `Shell/ShellExecution.cs:70-73`) et « non exécutable » (code 126).

**Ce qui manque :**
- **Installation des dépendances** : rien ne télécharge une dépendance manquante depuis le Store ou kazopkg. Aucune logique de dépendance n'a été trouvée dans l'installeur de paquets ni dans le Store.
- **Registre d'interfaces** : aucun mécanisme ne laisse un paquet installé fournir l'implémentation d'une interface définie par l'OS, résolue par les autres apps (avec null si elle est absente).
- **Binaires dans le shell** : aucun binfmt. Le shell refuse les binaires (`ShellInterpreter.Scripts.cs:67-120`).

### Effets sur la carte

- **C15 (points d'extension du noyau)** : trois points d'extension sont ajoutés.
  - le mécanisme « fonctionnalités facultatives » (interface définie par l'OS, implémentée par un paquet, null si absent) ;
  - la résolution des dépendances par le Store et kazopkg ;
  - le lancement des exécutables dans le terminal (binfmt).
- **C35 (ouvrir un .exe)** : l'exécution depuis le terminal fait maintenant partie du cœur de la fonctionnalité. Elle n'est plus reportée à une v2.
- **C16 et C17 (licences)** : tranchées par la décision 8. La licence d'Os-x86 reste à confirmer (Q18).

## Série 2 — envoyée et répondue le 2026-11-09

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 9 | Rôle de BoxedWine | **B : BoxedWine n'est qu'une interface.** Os-x86 lance Wine *à l'intérieur* de sa propre VM Linux (v86), sans le moteur Boxedwine. | C3, C8 |
| 10 | Où vit l'interface | **B : un paquet NuGet de contrats publié par Os-x86**, référencé à la compilation par les apps qui l'utilisent. | nouveau |
| 11 | Paquet et installation | **A**, recommandation acceptée : un seul paquet (service à la demande, app « Sous-systèmes », commande de terminal à la manière de `wsl`), installé au niveau système par un admin ; chaque usager a ses propres sous-systèmes. | C12, C20 |
| 12 | Dépendances | **A**, recommandation acceptée : le Store et kazopkg résolvent les dépendances (ADR), un seul écran de consentement, un appel du SDK « demander l'installation » pour une dépendance facultative, approbation admin habituelle ; désinstaller Os-x86 avertit et désactive les apps dépendantes. | C12, C15 |
| 13 | `./setup.exe` dans le terminal | **A**, recommandation acceptée : le shell reconnaît MZ, PE ou ELF et passe le fichier à la fonctionnalité par un binfmt générique (ADR). Console : entrées et sorties dans le terminal, Ctrl+C transmis, code de sortie rendu. Graphique : une fenêtre Kazo. Sans la fonctionnalité : message et commande d'installation. | C35, C15 |
| 14 | Droits de ce qui est lancé | **A, avec un ajout.** L'app appelante déclare une permission ; ce qu'elle lance hérite au plus de ses droits. **Ajout :** quand un exécutable lancé fait un appel réseau que l'usager n'a pas encore autorisé ou refusé, une fenêtre lui demande la permission. Chaque exécutable est alors traité comme une application distincte, avec un **système de permissions propre à Os-x86**, distinct de celui de l'OS. | C20 |
| 15 | Portée de l'interface v1 | **B, avec une précision** : en plus de lancer et suivre des programmes et de gérer les sous-systèmes, le **contrôle complet des VM** (disques, instantanés, périphériques). Demander une interface dont l'implémentation n'est pas installée retourne **null, comme `GetService<T>()` avec le DI d'ASP.NET**. | nouveau |
| 16 | .exe visés en priorité | **A**, recommandation acceptée : les programmes 32-bit (et DOS) d'abord ; les `.exe` 64-bit viendront avec le palier x64, sans date. | nouveau |
| 17 | Windows 10/11 x64 | **A**, recommandation acceptée : objectif de recherche à long terme, sans date, en paliers mesurés (7, puis 10, puis 11), livré seulement s'il atteint des critères d'utilisabilité, sans jamais bloquer les paliers 32-bit. | C2, C9 |
| 18 | Licence d'Os-x86 | **A**, recommandation acceptée : Os-x86 reste sous MIT ; chaque moteur copyleft est un paquet séparé qui se branche sur Os-x86. | C16 |

### Ce que la vérification du 2026-11-09 a trouvé

Cinq recherches en parallèle : le code de Kazo (commit cd89ee0), Wine sur Linux i686, l'affichage et la console, les permissions réseau par programme. Chaque recherche a été revue par un vérificateur indépendant. Aucune affirmation importante n'a été réfutée ; une trentaine de détails ont été corrigés (versions, lignes, dates).

**Décision 9 (Wine dans la VM Linux)**
- **Bonne nouvelle pour le 64-bit.** Un Linux x86-64 avec le « nouveau WoW64 » de Wine lance les `.exe` 64-bit, 32-bit et 16-bit dans un même préfixe ; Alpine x86_64 livre Wine ainsi par défaut. Les `.exe` 64-bit arrivent donc au palier 2 (Linux x86-64), pas au palier 3 (Windows x64).
- **Aucun précédent.** Personne n'a montré Wine dans v86 ; le mainteneur juge la combinaison « hors de portée » du projet (v86 #316), et un Puppy Linux avec Wine prenait environ 15 minutes à démarrer son bureau (#455). v86 a un seul cœur, des calculs x87 lents et un ralentissement attendu d'environ 10× (#1005). Un état « déjà démarré » est indispensable, et la voie doit être validée par une mesure.
- **Distribution.** Alpine 3.24 x86 (32-bit) livre Wine 11.0 : 68,3 Mio compressé, 220,7 Mio installé, 25 dépendances directes. Avec un serveur X et un préfixe : environ 0,5 à 0,8 Go. v86 fournit déjà des outils pour construire une image Alpine x86.
- **Limites du 32-bit.** Un Wine i686 ne crée que des préfixes « win32 », dépréciés depuis Wine 11.0 et non convertibles : passer au palier x64 voudra dire réinstaller les programmes Windows. v86 n'a ni SSSE3, ni SSE4, ni AVX. Wine 11.11 et plus exige SSE2, que v86 a.
- **DOS.** Wine n'exécute plus de programmes DOS depuis 2018 (winevdm exige DOSBox). Les `.exe` DOS purs demandent une autre voie.
- **Licences.** L'image contient du GPL-2 (noyau, BusyBox) et du LGPL (Wine) : il faut offrir les sources au même endroit que l'image.

**Décision 10 (NuGet de contrats, null comme le DI d'ASP.NET) : impossible sur le Kazo d'aujourd'hui**
- Un `.kazoapp` ne contient que les assemblies déclarées, pas les DLL NuGet (`AppPackageWriter.cs:54-66` ; issue #419 ouverte depuis le 2026-11-07).
- L'installeur refuse une app qui référence une DLL qu'elle n'embarque pas : `apps.package.missing-api` (`AppPackageApiCheck.cs:80-90`). L'app appelante échouerait donc à *son* installation, avant même de demander l'interface.
- La première DLL chargée sous un nom simple gagne, sans égard à la version, pour toute la durée de la page (`InstalledAppsBoot.cs:300-306`). Une vieille copie des contrats embarquée par une app pourrait masquer celle d'Os-x86.
- Aucun registre de services entre apps : les apps installées n'ajoutent jamais rien au conteneur DI (`InstalledAppsBoot.cs:343-391`), et `[Inject]` d'un type hors SDK est interdit à la compilation (KOSAPP0031). Le plus proche : les contrats de composants (ADR 0148, 0156), réservés aux apps fenêtrées. L'ADR 0148 a d'ailleurs rejeté un service DI global parce qu'il forcerait à charger le fournisseur au démarrage.
- Un `GetService<T>()` synchrone ne peut pas charger un fournisseur qui n'est pas encore en mémoire : il faut une résolution asynchrone.
- Le nom NuGet `Kazo.Os.X86.Contracts` ferait échouer la compilation de chaque app appelante (KOSAPP0030, `build/Kazo.Os.Apps.targets:401-430`) ; `Kazo.Os.Apps.X86.Contracts` passe.

**Décision 11 (un seul paquet)**
- Un `.kazoapp` contient un seul manifeste et un seul type d'app (`AppPackage.cs:47, 78`). Une commande de terminal n'est permise que sur une app Terminal, et les associations de fichiers que sur une app fenêtrée (`AppManifestValidator.cs:295-321`). Os-x86 compte donc au moins trois apps.
- Un service ajouté sans redémarrage ne démarre qu'au prochain démarrage de l'OS (ADR 0214 §1.3).

**Décision 13 (terminal)**
- Le terminal de Kazo n'a pas de mode brut : Ctrl+C annule la commande une seule fois (`TerminalPanel.razor:1132-1139`), et les flux sont du texte UTF-16 sans pty.
- Wine tronque les codes de sortie à 8 bits (0xC0000005 devient 5).
- Le shell renvoie 126 avec « chmod +x » avant de lire le fichier (`ShellInterpreter.Scripts.cs:57-66`) : un `.exe` téléchargé sans bit d'exécution échouerait.

**Décision 14 (chaque exécutable est une application)**
- Le plafond « au plus les droits de l'appelant » ne s'applique de façon stricte qu'au code invité. Face à une app .NET appelante, il dépend de la bonne foi de celle-ci : toutes les apps installées partagent la page et ont « les mêmes droits que l'OS » (`KazoAppPackages.cs:20-23`), et `KazoExecutionContext.Enter` est public.
- Une permission propre à Os-x86 ne peut pas être déclarée aujourd'hui (`manifest.capability.unknown`, `AppManifestValidator.cs:846-863`).
- Sous Wine, c'est **wineserver** qui crée et connecte toutes les sockets TCP, et il y a un wineserver par préfixe. Une identité réseau fiable par programme demande donc **un préfixe par programme**, avec son propre espace de noms réseau et sa propre adresse IP. Dans un préfixe partagé, distinguer deux `.exe` se contourne facilement.
- Limites qu'aucun « Autoriser » ne lève avec le relais actuel : 4 flux par appareil, toutes apps confondues ; ports 25, 445 et autres toujours bloqués ; UDP limité à 53/123, et v86 ne transmet pas d'UDP ; sans serveur, pas d'Internet réel.
- La fenêtre de permission doit être une app fenêtrée d'Os-x86 : un service n'a pas d'interface, les actions de notification ne rappellent pas l'app, et aucune fenêtre ne peut être modale pour tout le système.

**Décision 15 (contrôle complet des VM)**
- v86 refuse un état sauvegardé par une autre version de v86 ou avec d'autres options (`state.js:215-225`) : une mise à jour peut rendre des instantanés inutilisables.
- Les périphériques sont fixés à la construction de la VM (pas d'ajout à chaud).
- Un disque ou une ISO ne peut pas venir d'un chemin du VFS (16 Mio par fichier).
- Kazo n'a pas d'API de processus étranger : un programme lancé par l'interface n'apparaîtrait ni dans `ps` ni dans le Gestionnaire des tâches.

**Dépendances (décision 12)** : une app qui déclare Os-x86 comme dépendance facultative avec une version maximale est *retirée du catalogue* si une version hors plage est installée ; elle ne reçoit pas null (`AppCatalog.cs:487-525`).

### Effets sur la carte (série 2)

- **C8** : remplacé. Les `.exe` passent par Wine dans une VM Linux d'Os-x86 ; plus de moteur Boxedwine.
- **C3** : Os-App-BoxedWine devient une interface ; son rôle exact est à préciser (série 3).
- **C10 (DOS)** : Wine ne couvre pas DOS ; à trancher en série 3.
- **C14 (images)** : l'image Linux doit inclure Wine ; elle dépasse les limites d'un paquet.
- **C15** : la liste des ADR noyau s'allonge : bibliothèques d'app (#419) et règle de versions partagées, registre de fonctionnalités et passerelle de résolution, permissions déclarées par un fournisseur, identité vérifiée de l'appelant, résolution des dépendances, binfmt, mode brut du terminal, paquet à plusieurs apps, processus étrangers.
- **C34 (affichage)** : une fenêtre par programme Windows devient une exigence (décision 13) ; à trancher en série 3.
- **C6 (v86 officiel ou fork)** : quelques correctifs v86 sont probables (ports virtio-console), ce qui pousse vers un v86 corrigé.

## Série 3 — envoyée et répondue le 2026-11-09

Sauf mention contraire, la recommandation a été acceptée (option A).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 19 | Quel appel ouvre la fenêtre de permission | **Celui du programme lancé** (dans la VM). Les apps Kazo appelantes restent sous les permissions de Kazo. | D14 |
| 20 | « Une application » pour un programme Windows | **Une bouteille (préfixe Wine) par programme lancé depuis Kazo**, avec son réseau isolé et son adresse IP. Ce qu'elle installe et lance partage son identité. Décision mémorisée par usager, sous-système et bouteille ; la fenêtre montre le chemin et l'empreinte SHA-256 du `.exe` d'origine, et redemande si l'empreinte change. Base commune en copie-sur-écriture. | D14, D9 |
| 21 | Droit effectif | **La fenêtre ne fait que restreindre** : intersection des droits d'Os-x86, de l'appelant, du sous-système, de la décision et du relais. Elle n'apparaît que si les autres couches permettent ; sinon refus immédiat avec notification. Appelant : l'usager pour un lancement au terminal, au double-clic ou dans l'app Sous-systèmes ; l'app pour un appel par l'interface ; le lanceur pour un programme lancé dans la VM. | D14 |
| 22 | Portée | En v1, permission par programme pour le **réseau** seulement, pour les bouteilles et les ELF lancés depuis Kazo. Une VM complète = une identité. Fichiers et presse-papiers par sous-système, plafonnés par l'appelant ; un `.exe` voit son dossier et ses arguments. Chaque bouteille a son propre usager dans l'invité. | D14 |
| 23 | Fenêtre de permission | **A, avec deux changements.** Fenêtre de bureau, une par programme ; Autoriser ou Refuser, une fois, jusqu'à la fin du programme ou toujours ; toute destination ou cet hôte et ce port ; règles imposées par l'admin ; décisions révocables dans l'app Sous-systèmes ; le réseau simulé ne demande jamais. **Changement 1 : aucun délai.** La fenêtre attend la réponse sans limite. **Changement 2 : sans session.** Si aucun usager n'est connecté : refus, et une notification qui contient un lien vers la page de configuration, pour ajouter une règle qui autorisera à l'avenir même quand aucune session n'est ouverte. | D14 |
| 24 | Où tournent les programmes | Un sous-système **« Programmes Windows » géré par Os-x86** par usager (image de base en lecture seule + changements), créé au premier `.exe`, visible mais non modifiable. Les ELF vont au sous-système Linux par défaut. | D9, D11, D13 |
| 25 | Mesure préalable | **Maquette au jalon 0** (lancement d'un `.exe` console et du Bloc-notes depuis un état déjà démarré) ; critères fixés après une première mesure ; en cas d'échec, VM et DOS d'abord, réévaluation au banc d'essai x64. | D9 |
| 26 | Fenêtre d'un programme graphique | En v1, **une fenêtre Kazo par programme** par les bureaux virtuels Wine découpés dans l'écran de la VM ; mode intégré plus tard, après une maquette. | D13, C34 |
| 27 | Programmes console | **ADR Kazo « mode brut du terminal »** en v1 ; un lanceur dans l'invité rend le code de sortie complet. | D13 |
| 28 | DOS | **Sous-système FreeDOS** géré par Os-x86 dans v86 ; mode texte dans le terminal Kazo. MZ seul → DOS ; MZ+NE et MZ+PE → Wine. | C10 |
| 29 | Os-App-BoxedWine | **C (pas ma recommandation) : l'interface d'Os-x86 reste générique**, et BoxedWine pilote Wine en envoyant des commandes dans l'invité. | D9 |
| 30 | DLL de contrats à l'exécution | Chaque app appelante embarque la DLL ; Kazo termine #419 et ajoute une règle de bibliothèques partagées (une copie par nom, celle du fournisseur ou la plus récente compatible d'abord, refus d'une version majeure incompatible). *À concilier avec la réponse 33.* | D10 |
| 31 | API de résolution | `await GetServiceAsync<T>()` → null s'il n'y a pas de fournisseur utilisable ; `IsAvailable<T>()` synchrone ; `GetStatus` donne la raison ; permission manquante = exception ; une dépendance facultative hors plage compte comme absente. | D10, D15 |
| 32 | Permissions et identité | ADR Kazo « permissions déclarées par un fournisseur » : « lancer des programmes » (normale) et « gérer les sous-systèmes » (dangereuse) ; l'OS fournit l'identité vérifiée de l'appelant ; une app ne contrôle que les sous-systèmes de l'usager en session. | D14, D15 |
| 33 | Forme du paquet | **D (pas ma recommandation) : un nouveau type d'application « Feature ».** Os-x86 référence **Os-x86.Interface**, un autre paquet NuGet, inclus avec l'application. Les autres apps utilisent ce NuGet pour demander une interface au système, et ne référencent que la fonctionnalité « Os-x86 ». Une Feature peut être référencée par d'autres apps et peut avoir des commandes, des fenêtres, des services et des pages web. | D11, D10 |
| 34 | Instantanés et périphériques | Points de restauration du disque durables, états de veille jetables ; périphériques = profil au prochain démarrage + CD et disquette à chaud ; disques importés et exportés par téléversement et téléchargement. | D15 |
| 35 | Choix techniques | Tous acceptés : un fournisseur par contrat, Os-x86 au niveau système seulement ; pas de root dans l'invité ; une VM en marche par session ; image téléchargée à part ; Wine 11.0 épinglé ; `.exe` sans bit d'exécution permis ; un lancement = un processus Kazo. **Le nom NuGet devient `Os-x86.Interface`** (réponse 33), qui ne tombe pas sous la règle KOSAPP0030. | — |

### Lecture des réponses 23, 29 et 33 (revue par deux relecteurs indépendants)

- ~~**29 C** veut dire : *aucun type propre à Wine dans Os-x86.Interface*. Os-x86 garde toute la logique Wine nécessaire aux décisions déjà prises : bouteilles, sous-système géré, une fenêtre par programme, lanceur, binfmt. BoxedWine n'est qu'un client de l'interface générique.~~ **Remplacé par la réponse 40** : BoxedWine reçoit l'exécution des programmes Windows et porte la logique Wine ; Os-x86 est le backend. Voir la série 5.
- **33 D** et **30 A** se concilient ainsi : « référencer la fonctionnalité » veut dire une dépendance de manifeste sur la Feature. Le NuGet ajoute toujours une copie d'Os-x86.Interface dans l'app appelante, ce qui permet de retourner null quand Os-x86 est absent (à confirmer, série 4).
- **Nom NuGet** : `Os-x86.Interface` remplace `Kazo.Os.Apps.X86.Contracts`. « Os-x86 » est le nom affiché. L'identifiant d'app (format `a.b.c` en minuscules) et l'espace de noms C# (`Os-x86` n'en est pas un valide) restent à fixer avant la première publication.
- **23 « aucune limite »** : Os-x86 n'impose aucun délai à la fenêtre. La connexion reste en attente tant que la pile TCP de l'invité réessaie : `tcp_syn_retries` est réglé à son maximum (127) dans l'espace réseau de chaque bouteille, soit environ 4 h. Un programme qui a son propre délai (navigateurs, WinINet) abandonne plus tôt et reçoit la réponse à sa tentative suivante.

### Ce que les relecteurs ont trouvé

- **Rien ne tourne quand personne n'est connecté.** Kazo arrête toutes les apps d'une session à la déconnexion, services compris. Seules les apps système intégrées tournent sans session (`AppLifecycleOrchestrator.cs:736-827`). La règle « sans session » de la réponse 23 ne peut donc s'appliquer qu'après un ADR Kazo de type « linger » (série 4).
- **Notifications** : une app ne notifie que l'usager pour qui elle tourne, et la file est en mémoire (100 par usager), donc perdue au rechargement (`AppNotificationGateway.cs:38-61`). Une notification peut porter une action qui lance une app avec des arguments (`NotificationAction.LaunchApp`).
- **Une Feature à plusieurs points d'entrée contredit le modèle de Kazo.** Le type, le point d'entrée, la commande, le processus et l'état de service existent une seule fois par identifiant d'app. Le validateur applique les règles de type au manifeste entier (une page web ne peut pas demander `filesystem.*`). C'est le plus gros ADR Kazo de la série.
- **Installer sans redémarrer marche déjà en partie.** Fenêtres et commandes ajoutées à chaud se lancent tout de suite (ADR 0214 §1.1-1.2). Seul le démarrage *automatique* d'un service attend le prochain démarrage. Les vrais obstacles : une copie plus ancienne de l'interface déjà chargée, et l'approbation admin de `network.real.access` qui surgit à la première demande.
- **Les Fichiers de Kazo ne verront jamais l'intérieur d'une bouteille**, qui vit dans le disque de la VM. « Ouvrir dans… » ne peut pas lister les bouteilles dans un sous-menu (entrées statiques), mais une entrée fixe qui ouvre un sélecteur d'Os-x86 est possible (`shell.context-menu`).
- **Arrêt à la déconnexion** : Kazo laisse environ 5 s à une app pour s'arrêter, puis la tue. Sauvegarder la RAM d'une VM dans ce délai est improbable.
- **Points d'extension pour plusieurs moteurs** : reportés au choix du moteur x64 (C9). Le modèle « plusieurs fournisseurs, choix de l'admin » de l'ADR 0156 existe déjà.

## Série 4 — envoyée et répondue le 2026-11-09

Sauf mention contraire, la recommandation a été acceptée (option A).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 36 | Copies de l'interface | « Référencer la fonctionnalité » = une dépendance de manifeste, facultative ou obligatoire, vers la Feature. Le NuGet ajoute une copie d'Os-x86.Interface dans l'app. Une seule copie chargée par page : la plus récente compatible, choisie au premier besoin. Une version mineure ne fait qu'ajouter des membres. Copie plus ancienne déjà chargée à l'installation de la Feature → redémarrage. Un null n'est jamais mis en cache. | D30, D33 |
| 37 | Structure d'une Feature | Une seule identité (installation, mise à jour, retrait, Store, écran des permissions). Un identifiant enfant par point d'entrée, avec son propre type, visible séparément dans `ps`. La Feature déclare l'ensemble des permissions, affiché une fois ; chaque point d'entrée déclare son sous-ensemble, vérifié selon son type. | D33 |
| 38 | Règles des Features | Seules les Features fournissent une interface, déclarent des permissions de fournisseur et enregistrent un binfmt. Installation au niveau système seulement, par un admin. Section « Fonctionnalités » dans les Paramètres. Une Feature peut dépendre d'une autre Feature. | D33, D32, D35 |
| 39 | Utilisable tout de suite | Oui. Le consentement de l'admin à l'installation couvre les permissions dangereuses de la Feature. Exception : copie plus ancienne de l'interface déjà chargée → redémarrage. Mise à jour ou retrait : redémarrage seulement si le code est chargé. | D33, D12 |
| 40 | Qui reçoit l'exécution | **Pas ma recommandation.** Tes mots : « Os-x86 est juste le backend, et boxedWine son interface ... donc c'est BoxedWine qui s'occupe de recevoir la commande de l'exécution. » Lecture et conséquences : série 5. | D29, D13 |
| 41 | Choix de la bouteille | Une empreinte SHA-256 déjà connue réutilise sa bouteille. Mise à jour, correctif ou greffon : un sélecteur de bouteilles, ouvert par « Ouvrir dans un environnement Windows… » ou une option de terminal, avertit que le programme recevra les permissions réseau de la bouteille. *Propriétaire du sélecteur : à revoir avec la réponse 40.* | D20 |
| 42 | Permission des commandes et fichiers de l'invité | Dans les identités que l'app a créées, ou que l'usager a choisies par elle : « lancer des programmes ». Ailleurs : « gérer les sous-systèmes ». | D32, D29 |
| 43 | Droits des programmes lancés par BoxedWine | On garde D21 : BoxedWine déclare `network.real.access` et les permissions de fichiers qu'il transmet, avec approbation admin à son installation. | D21, D29 |
| 44 | Compatibilité de l'invité | L'interface donne l'identifiant et la version de l'image et la version de Wine ; Os-x86 documente les outils stables de chaque version d'image ; une app déclare la version d'image minimale dont elle a besoin. | D29 |
| 45 | Sans session | Pas en v1 : tout s'arrête à la déconnexion. La règle « sans session » est inscrite dans la spécification et s'appliquera après un ADR « linger » (refus gardé par Os-x86, montré au propriétaire à sa prochaine connexion avec un lien vers les règles ; case « même sans session » ; propriétaire absent mais autre usager connecté = pas de session). | D23 |
| 46 | Programmes Linux | « Toujours autoriser » mémorise usager, sous-système, chemin et empreinte SHA-256 ; on redemande si l'empreinte change. | D22 |
| 47 | Nom et licence de BoxedWine | **B (pas ma recommandation) : on garde le nom BoxedWine, et la licence passe à MIT.** | D29 |
| 48 | Choix techniques | Tous acceptés : notification « X attend ta réponse » ; demandes en attente refusées à la déconnexion ; lien de notification avec un numéro de demande opaque seulement ; identifiants fixés avant la première publication et nom NuGet réservé tôt ; états de veille dans l'OPFS d'Os-x86 ; la maquette mesure aussi le temps de sauvegarde d'un état. | — |

### Lecture de la réponse 40 (analyse du 2026-11-09)

Quatre analyses en parallèle (effet sur les décisions, faits de Kazo, partage de la confiance, thème interface), une critique croisée, puis trois relecteurs du brouillon de la série 5. Rapports (en anglais) : [../recherche-2026-11-09/reponse-40/](../recherche-2026-11-09/reponse-40/).

- **Lecture retenue (à confirmer par 49)** : BoxedWine reçoit toute demande d'exécuter un programme Windows (terminal, double-clic, « Ouvrir dans un environnement Windows… », raccourcis, autres apps) et pilote Wine par des commandes dans l'invité. Os-x86 fait tourner les VM, fournit l'image avec Wine et applique les permissions.
- **Effets** : la décision 7 change (Os-x86 seul ne lance plus de `.exe` Windows) ; en 13, « la fonctionnalité » qui reçoit le fichier devient BoxedWine ; la lecture de 29 ci-dessus est remplacée.
- **Faits de Kazo (cd89ee0, aucun commit plus récent ; #419 toujours ouverte)** :
  - les associations de fichiers ne se choisissent que par extension, jamais par contenu (`FileAssociationResolver.cs:386-393`) ; le `Priority` d'un gestionnaire n'est jamais lu ;
  - une app avec des permissions normales peut se rendre, en silence, l'app par défaut d'une extension (`KazoApps.SetDefaultHandlerAsync`, `AppCatalogGateway.cs:146-151`) ;
  - `LaunchAsync` et l'ouverture de fichier n'indiquent pas l'expéditeur (`AppIntentDispatcher.cs:209-242`) ; un raccourci `.desktop` lance une app avec n'importe quels arguments ;
  - `~/.config/<app>` d'une autre app s'écrit avec `filesystem.user-home.write`, une permission normale (`FileSystemService.cs:760-787`) ;
  - pour un nom de commande, les apps système gagnent, puis l'ordre d'activation ; le perdant n'a qu'un avertissement (`AppCatalog.cs:343-372`) ;
  - aucun binfmt ni aucune lecture d'en-tête MZ, PE ou ELF ; `./x` rend 126 (« Permission denied » sans bit d'exécution, sinon « cannot execute binary file », `ShellInterpreter.Scripts.cs:57-83`) ;
  - un composant intégré tourne avec les droits de l'app qui l'affiche (ADR 0063, 0156) ; un contrat dont le type vient d'Os-x86.Interface n'est pas testé.
- **Sécurité** : la fenêtre de permission doit rester à celui qui applique la règle ; un front end ne doit jamais répondre à ses propres questions. Si BoxedWine choisissait la bouteille, il pourrait glisser un nouveau programme dans une bouteille « toujours autorisée ». Aucune opération « autoriser » dans l'API. Même avec Os-x86 qui choisit la bouteille, BoxedWine peut encore lancer des commandes dans ses propres bouteilles (42) : la protection vise les erreurs de choix de bouteille, pas un BoxedWine défaillant. Dans une même VM, le serveur d'affichage commun laisse un programme piloter les fenêtres d'une autre bouteille : la permission réseau par programme ne tient que face à un programme qui agit seul.
- **Corrections de la carte** : v86 n'a que la Sound Blaster 16 (et le haut-parleur du PC), pas de HDA (C38). La lecture de 35 : seuls les programmes lancés par Kazo et les commandes de l'API ne sont jamais root ; sudo reste un réglage par sous-système dans les Linux de l'usager (`critique.md:195`).

## Série 5 — envoyée et répondue le 2026-11-09

Le partage entre Os-x86 et BoxedWine. Texte complet envoyé : voir l'historique de la conversation ; résumé des options retenues ci-dessous. Sauf mention contraire, la recommandation a été acceptée (option A).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 49 | Où s'arrête BoxedWine | **A, avec une précision** : BoxedWine pour les programmes Windows ; Os-x86 garde ses fenêtres de sécurité (permission réseau, page des règles). Tes mots : « Os-x86 génère le rendu, BoxedWine l'affiche. Un peu comme le bureau et les fenêtres fonctionnent. On pourrait voir Os-x86 comme des DLL utilisées par BoxedWine. » *Ce que 49 A laissait d'autre à Os-x86 (app Sous-systèmes, écrans de VM) est tranché par 63 A : plus d'app Sous-systèmes.* | D40, D11 |
| 50 | Choix de la bouteille | Os-x86 décide par l'empreinte SHA-256 (même usager, sous-système et app créatrice) ; nouvelle bouteille sinon ; sélecteur d'Os-x86 seulement sur demande, avec l'avertissement de 41 ; Os-x86 refuse d'écrire un exécutable dans une bouteille par l'API. Limite acceptée : BoxedWine peut encore lancer des commandes dans ses bouteilles. | D20, D41, D42 |
| 51 | BoxedWine est-il une Feature | **D (pas ma recommandation) : non.** Tes mots : « BoxedWine reste une application qui utilise la feature Os-x86. Faut voir BoxedWine comme une façon simple d'utiliser Os-x86. On pourrait tout faire par ligne de commande avec seulement Os-x86. Mais Os-x86 n'a pas de paramètres utilisateur, pas de fichier récent, pas d'utilitaire de configuration de disque, machine, réseau, etc. » | D37, D38 |
| 52 | Une autre app lance un `.exe` | **A, avec une précision** : elle peut demander à BoxedWine directement, ou demander au shell de l'exécuter ; le shell passe alors par le registre des associations d'extension. L'OS marque l'expéditeur (petit ADR) ; plafond = expéditeur ∩ BoxedWine ; un « toujours » pris pour toi ne couvre pas une autre app. | D21, D43 |
| 53 | DOS et autres fichiers Windows | **A, avec une précision** : d'abord l'en-tête, ensuite l'extension. Tes mots : « Quand le terminal utilise la fonctionnalité Os-x86, il s'occupe aussi d'afficher le rendu. Le terminal devra donc avoir des modifications pour lui permettre d'utiliser Os-x86. » | D28, D13 |
| 54 | Sous-système Programmes Windows | À Os-x86, géré pour BoxedWine ; réglages verrouillés, mais suppression de bouteilles et révocation possibles ; désinstaller BoxedWine ou Os-x86 garde les données. | D24 |
| 55 | Affichage des programmes | **A, avec une précision** : une fenêtre BoxedWine par programme avec une vue de l'invité fournie par Os-x86. Tes mots : « Lancé depuis le terminal : il s'occupe aussi de rendre l'application si c'est une application console. » | D26, D27 |
| 56 | Versions | Les cinq règles acceptées (versions minimales, nouvelle image proposée par BoxedWine, mise à jour des bouteilles, états de veille abandonnés, retour en arrière). | D36, D44 |
| 57 | Programmes installés | **A, avec une précision** : chaque app déclare son icône dans son manifeste ; le SDK change pour qu'une app puisse changer l'icône de sa fenêtre (le bouton de la barre des tâches suit) et déclarer un raccourci avec une icône (nouvel ADR). Dans BoxedWine, une entrée de menu « ajouter une application à Kazo OS » : l'usager navigue jusqu'à l'exécutable ou au raccourci (`.lnk`) ; entrée du menu Démarrer obligatoire, raccourci de bureau facultatif. BoxedWine ajoute sa petite icône en bas à droite de l'icône PNG d'origine de chaque raccourci. | nouveau |
| 58 | Os-x86 sans BoxedWine | Suggestion générique dans Kazo (formats déclarés dans la fiche du Store). *Tranché par 62 B : la puce « Os-x86 seul ne lance aucun `.exe` Windows » tient.* | D7, D13 |
| 59 | Une autre VM tourne déjà | **C (pas ma recommandation)** : plusieurs VM à la fois si les ressources le permettent. Tes mots : « Mais quand une VM tourne, on doit l'afficher à l'utilisateur avec la possibilité de fermer une autre VM, ouvrir cette application dans une VM existante ou créer une nouvelle VM. » | D35 |
| 60 | Noms | Commandes `boxedwine` et `x86` ; identifiants exacts dans la spécification ; un format relié à un identifiant d'app ; conflit entre Features tranché par un admin ; commandes d'une Feature avant celles des apps ordinaires. | D48 |
| 61 | Choix techniques | Les 13 points acceptés (stockage OPFS des décisions et de la bibliothèque, aucune opération « autoriser », root réservé à l'agent et sudo par sous-système, fin du programme si BoxedWine plante, pas de consentement au terminal, Wine Mono et Gecko dans l'image, BoxedWine sans réseau pour lui-même, correctifs de Wine dans l'image, limite de l'affichage partagé, confirmation du gestionnaire par défaut, pas d'installation pour tous en v1, bouteilles invisibles aux autres apps, tâches de dépôt). | — |

### Lecture des réponses de la série 5 (analyse du 2026-11-09 ; lecture remplacée par 62 B et 64)

Trois analyses (cohérence, faits de Kazo, faits de Wine, Boxedwine et v86), une critique croisée, puis trois relecteurs et un vérificateur final de la série 6. Rapports (en anglais) : [../recherche-2026-11-09/serie-6/](../recherche-2026-11-09/serie-6/).

- ~~**Lecture retenue :** Os-x86 est le moteur (VM, bouteilles, permissions, affichage par la « vue de l'invité ») avec une commande `x86` qui fait tout. Le terminal lui parle directement par un binfmt générique de Kazo : `./setup.exe` va à Os-x86, pas à BoxedWine, et le terminal affiche les programmes console. BoxedWine est une app ordinaire (une fenêtre) : double-clic, demandes des autres apps, bibliothèque, fichiers récents, réglages. Ta décision 7 revient (Os-x86 seul lance les `.exe` depuis le terminal) ; la lecture de 40 ci-dessus ne vaut plus pour le terminal.~~ **Remplacée par 62 B et 64 (série 6)** : `./setup.exe` va à BoxedWine par l'association `.exe` ; Os-x86 seul ne lance aucun programme Windows (58 A, puce 2) ; BoxedWine affiche les programmes Windows lancés au terminal. BoxedWine reste une app ordinaire (51 D). Les faits ci-dessous restent vrais.
- **Faits de Kazo (cd89ee0) :**
  - une fenêtre n'a pas de code de sortie (`AppLifecycleOrchestrator.cs:1232, 1259`) : seul un point d'entrée de type commande rend le code de sortie ;
  - le shell cherche les commandes avant les noms courts de fenêtres (`ShellInterpreter.SimpleCommands.cs:158-179`) ;
  - l'icône d'une fenêtre ne vient que du manifeste (`WindowLaunchCoordinator.cs:111-127`) ; le menu Démarrer lance les apps sans arguments ; `Icon=` d'un `.desktop` est lu mais jamais dessiné ;
  - une app ordinaire reçoit ses permissions dangereuses au lancement, d'un accord d'admin valable pour la machine (ADR 0049) ;
  - une installation personnelle dont la dépendance obligatoire manque réussit, puis l'app est écartée du catalogue avec une seule ligne de journal (`AppCatalog.cs:487-503`) ;
  - un gestionnaire de fichier est choisi sans demander à personne quand une seule app déclare l'extension (`FileAssociationResolver.cs:198-214`).
- **Faits de Wine et v86 :** deux VM sur le même préfixe démarrent chacune leur wineserver, sans conflit détecté ; le registre suit la règle « le dernier qui écrit gagne » (déduit du code de Wine). Les disques durs de v86 sont fixés au démarrage, sans virtio-blk. Le 9p de v86 est environ 12 fois plus lent sur de petits fichiers (issue #1451). winemenubuilder crée des raccourcis XDG et des icônes PNG dans l'invité ; il se coupe par `WINEDLLOVERRIDES`.
- **Le vrai Boxedwine :** ses interfaces récentes donnent un environnement privé par app et gardent `LastOpened` ; aucun réglage de mémoire, de disque ou de réseau.

## Série 6 — envoyée le 2026-11-09, répondue en partie

Texte complet envoyé : [serie-6-envoyee.md](serie-6-envoyee.md). **62, 63 et 64 ont une réponse. Les recommandations des questions 65 à 76 ne sont pas acceptées (tes mots : « je n'accepte pas tes recommandations pour les questions après 64 ») : ces questions restent ouvertes.** Elles supposaient 62 A ; l'usager a choisi 62 B, donc elles doivent être revues avant d'être reposées. La suite du débat se fait avec ChatGPT : voir [reprise-chatgpt.md](reprise-chatgpt.md).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 62 | Où va `./setup.exe` tapé au terminal | **B (pas ma recommandation) : à BoxedWine, par l'association `.exe`.** Os-x86 seul ne lance aucun programme Windows (58 A tient) ; une fenêtre n'a pas de code de sortie. | D7, D13, D53 |
| 63 | Interface de BoxedWine ; ce qui reste à Os-x86 | **A, recommandation acceptée** (tes mots : « 63 j'accepte ta recommandation, comme 61 »). Os-x86 n'a aucune app : la commande `x86` (configuration comprise), ses fenêtres de sécurité (dont la page des règles) et des fenêtres simples d'affichage (écran d'une VM Linux, DOS ou complète, ouvert avec `x86`). BoxedWine règle les VM Programmes Windows (mémoire, taille du disque, carte réseau, son, CD et disquette) par une nouvelle permission dangereuse « configurer les VM de programmes Windows » (troisième permission de 32) ; points de restauration et import ou export du disque par `x86` ou « gérer les sous-systèmes ». En v1, les VM Linux, DOS et complètes se règlent par `x86` seulement. Ça change 11 et 49 A (plus d'app Sous-systèmes), 24 et 54 A (réglages plus verrouillés). *La puce qui mettait « un programme graphique lancé au terminal » dans une fenêtre d'Os-x86 est remplacée par 64 (BoxedWine l'affiche). « Les VM en marche se voient dans BoxedWine » dépend de 66, ouverte.* | D11, D49, D24, D54, D32 |
| 64 | Qui affiche un programme graphique lancé au terminal | **BoxedWine**, pour ce qui lui arrive par l'association (les programmes Windows). Tes mots : « boxedWine. Os-x86 n'a pas d'association d'extension. » Un ELF ou un programme DOS graphique lancé au terminal : à trancher (voir les effets). | D55, D53 |

### Effets de 62 B et 64 (à faire confirmer dans la suite du débat)

- **Le terminal passe par BoxedWine pour les programmes Windows**, comme dans ma lecture de 40, mais par l'association `.exe` et non par un binfmt. 49 A garde son texte sur ce point. Deux exceptions : au terminal, le shell choisit l'app par l'extension, plus par l'en-tête (ça change 53 A, puce 1) ; et le code de sortie complet ne revient plus au terminal (ça change 55 A). Ma lecture « le terminal parle directement à Os-x86 » de la série 6 est abandonnée.
- **Décision 7 :** Os-x86 seul ne lance plus de `.exe` Windows ; il faut BoxedWine (58 A, puce 2, tient).
- **Le shell de Kazo doit apprendre une nouvelle route** (ADR Kazo, dans la ligne de ta précision de 52). Un fichier lancé par son chemin (`./setup.exe`), dont l'extension est déclarée exécutable par une app (`.exe`, `.msi`, `.bat`, `.cmd`, et `.com` selon la question DOS), va à l'app associée comme une fenêtre lancée par son nom : enfant du shell, nouvelle instance, avec les flux du shell ; le terminal attend sa fermeture.
  - Cette route passe avant le test du bit d'exécution (35 permet un `.exe` sans ce bit ; aujourd'hui le shell rend 126 avant de lire le fichier, `ShellInterpreter.Scripts.cs:57-66`), et avant la lecture comme script (un `.bat` est du texte, et `hsh` l'exécuterait aujourd'hui, `ShellInterpreter.Scripts.cs:78-127`).
  - Aujourd'hui, `./x` ne regarde jamais les associations (`ShellInterpreter.Scripts.cs:26-128`) ; `xdg-open setup.exe` ouvre déjà par l'association, mais rend la main tout de suite.
  - **À trancher :** seulement les extensions déclarées exécutables, ou toutes (comme le `cmd` de Windows, où taper `photo.png` l'ouvre) ; l'ordre face au binfmt par en-tête d'Os-x86 (ELF, et DOS si on le garde).
- **Gestionnaire de `.exe` au terminal :** avec 62 B, l'app par défaut pour `.exe` reçoit aussi chaque `./x.exe` tapé dans ton terminal, comme venant de toi (52). L'ADR de 61.10 (confirmer tout changement d'app par défaut) devient donc nécessaire pour le terminal aussi. Aujourd'hui, une app seule à déclarer `.exe` est choisie sans demander (`FileAssociationResolver.cs:198-214`). Si plusieurs apps la déclarent, le choix se fait dans une fenêtre, jamais au terminal (61.5).
- **Code de sortie (62 B ; ça change 13 et 27) :** une fenêtre Kazo n'a pas de code de sortie, et tu l'as accepté avec 62 B. Le terminal reçoit 0 quand la fenêtre se ferme, 1 si elle plante, 130 après Ctrl+C (`AppLifecycleOrchestrator.cs:1233-1259`, `ShellInterpreter.SimpleCommands.cs:285-296`). Il reçoit 0 tout de suite si BoxedWine n'a qu'une instance déjà ouverte (`ShellInterpreter.SimpleCommands.cs:282-284`). Le lanceur de 27 ne rend plus son code au terminal ; il peut encore le rendre à BoxedWine. Un ADR qui donnerait un code de sortie aux fenêtres changerait 62 B : ne le proposer que comme tel.
- **Programmes console lancés au terminal (tes précisions de 53 et 55 tiennent) :** la console s'affiche dans le terminal. Kazo donne déjà les flux du shell (entrée, sortie, erreurs) à une fenêtre lancée par son nom (`AppLifecycleOrchestrator.cs:548-559`, `ShellInterpreter.SimpleCommands.cs:259-278`). BoxedWine relaie la console du programme dans ces flux, sans que le terminal connaisse Os-x86 (58 A). **À trancher, seulement le comment :**
  1. la nouvelle route donne ces flux comme un lancement par le nom ;
  2. le mode brut (27) vaut aussi pour une fenêtre attachée au shell ;
  3. aujourd'hui, Ctrl+C au terminal ferme la fenêtre lancée (`ShellInterpreter.SimpleCommands.cs:291-295`), donc BoxedWine et le programme (61.4), au lieu de transmettre Ctrl+C au programme (13) ;
  4. ce que montre la fenêtre de BoxedWine pendant ce temps (à vérifier : une fenêtre peut-elle tourner sans être visible?) ;
  5. BoxedWine ne pose jamais de question dans le terminal (61.5).
- **Programmes Linux (ELF) et DOS au terminal :** 62 B ne couvre que les programmes Windows.
  - **ELF : 13 tient.** Un binfmt d'Os-x86 par en-tête (seule une Feature peut en enregistrer un, 38) envoie un ELF au Linux par défaut (24). Ce n'est pas une association d'extension, donc 64 ne l'interdit pas. **À trancher :** qui affiche un ELF graphique lancé au terminal (64 ne parle que de BoxedWine, qui ne lance pas de Linux ; 63 A donne à Os-x86 des fenêtres simples d'affichage).
  - **DOS : à trancher**, deux voies. (a) Le binfmt d'Os-x86 reconnaît aussi un MZ DOS par l'en-tête, avant l'association : 28 (mode texte dans le terminal) et 53 A (« d'abord l'en-tête ») tiennent, et DOS marche au terminal avec Os-x86 seul. (b) Un `.exe` DOS arrive à BoxedWine par l'association `.exe`, et BoxedWine le confie à Os-x86 par Os-x86.Interface : avec Os-x86 seul, `./jeu.exe` rend 126 (ça change 53 A, puce 4), et il faut dire qui affiche le programme DOS.
- **« Os-x86 n'a pas d'association d'extension » (64) :** Os-x86 ne s'inscrit pour aucune extension, pas même `.com` (ça change 53 A, puce 3). **À trancher :** qui reçoit `.com` au double-clic : BoxedWine, qui confie un programme DOS à Os-x86, ou personne (DOS seulement par `x86`). Avec Os-x86 seul, aucun double-clic ne lance quoi que ce soit.
- **Commande `boxedwine` (60 A) :** BoxedWine est une fenêtre (51 D), sans commande. `boxedwine` est le nom court de sa fenêtre, que le shell cherche après les commandes (`ShellInterpreter.SimpleCommands.cs:158-179`) : une app qui déclare une commande `boxedwine` la cache, et seul `x86` est protégé (ça change 60 A). **À trancher (72.8) :** `boxedwine setup.exe` se comporte-t-il comme `./setup.exe` (console dans le terminal) ou comme un double-clic?
- **Carte :** C15 : l'ADR « binfmt » devient deux ADR (la route du shell vers l'association, et le binfmt par en-tête d'une Feature pour les ELF, et DOS selon la question ci-dessus). C35 : un `.exe` tapé au terminal passe par BoxedWine.
- **Questions de la série 6 touchées :**
  - 63 A est acceptée (voir le tableau), sans la fenêtre « programme graphique lancé au terminal » (64).
  - 65 : la partie « une bouteille ou deux » est sans objet (terminal et double-clic passent tous deux par BoxedWine, donc par la même app créatrice) ; la partie « qui voit tes bouteilles » se règle avec 69 et 61.12.
  - 66 (« qui demande ») est à revoir.
  - 68 tient pour Windows ; sa partie DOS dépend de la question DOS.
  - 69 est à revoir : `x86` ne lance aucun programme Windows (62 B ; 58 A, puce 2), donc l'option « Tout » et `--bouteille` pour un nouveau fichier tombent. Restent la puce 3 de 58 A (shell ou commande dans une bouteille) et ce que `x86` peut faire des bouteilles de BoxedWine (lister, supprimer, révoquer). Il y a une tension avec ta phrase de 51 (« on pourrait tout faire par ligne de commande avec seulement Os-x86 ») : à te signaler, pas à trancher sans toi.
  - 72 : points 2 (BoxedWine doit savoir avant le lancement si le programme est DOS ou Windows, console ou graphique), 5 (une instance par programme devient nécessaire), 7, 8, 11, 13 (seulement « différent de celui de Kazo »), 14 (Os-x86 n'a plus de fenêtre pour un programme graphique lancé au terminal) et 15 à réécrire. 61.4 parle encore de « la commande de BoxedWine », qui n'existe plus (51 D).
  - 71 et 73 suivent les réponses à 65, 63 A et 69 ; 67, 70 et 74 à 76 restent valables à peu près telles quelles.

### Titres des questions de la série 6

62. Où va `./setup.exe` tapé au terminal (D7, D13, D53, D58)
63. Ce que couvre l'interface de BoxedWine ; Os-x86 sans app (D11, D49, D51, D24, D54, D32)
64. Qui affiche un programme graphique lancé au terminal (D55)
65. Une seule bouteille pour tes propres lancements ; qui voit tes bouteilles (D50, D54, D61.12)
66. Plusieurs VM : quand demander, qui exécute (D59, D35)
67. Sens de « créer une nouvelle VM » (D59, D24)
68. Programme console ouvert au double-clic : vue console d'Os-x86 (D55)
69. Ce que peut faire `x86` avec les programmes Windows (D58, D41, D50)
70. ADR des icônes, des entrées du menu Démarrer et de la marque (D57)
71. « Ajouter une application à Kazo OS » et bibliothèque (D57)
72. Choix techniques (15 points)
73. Linux dans le terminal (C36)
74. Presse-papiers (C37)
75. Son (C38)
76. Souris et clavier (C39)

## Consignes du 2026-11-10 (réponses données avec ChatGPT)

Tu as continué le débat avec ChatGPT et m'as remis un document de consignes : [consignes-2026-11-10.md](consignes-2026-11-10.md). Il complète et corrige le journal ; ses réponses récentes priment sur les formulations qu'elles remplacent. Il n'est pas encore fondu dans les tableaux ci-dessus : la vérification du 2026-11-10 ([verification-consignes-2026-11-10.md](verification-consignes-2026-11-10.md)) liste ce qu'il change, ce qu'il contredit et ce qu'il laisse ouvert. Le registre réconcilié viendra avec le plan, après les séries 7 et 8.

- **Nouvelles réponses à enregistrer** (source : consignes) : 72.2 (détection centralisée du type par Os-x86), 72.6 remplacé (arrêt immédiat des VM d'applications ; VM manuelle gardée), 72.9 (accord admin machine, sous condition), 72.10 (BoxedWine propose d'installer Os-x86), 72.11 remplacé (demander avant le premier téléchargement), 72.15 (fermer la fenêtre arrête le programme), 74 (texte seul), 76 (capture permise), 70 A refusée (marque par BoxedWine, 57 tient), 71 (ajout manuel, tutoriel obligatoire), C22 (VM dans la sauvegarde), C31 (réseau simulé : chaque VM un hôte), C40-C41 (8 Go, < 10 s / < 30 s), C43 (conventions, jeux non exclus), C18 (vrai Windows en V1 si peu de changements).
- **Décisions changées par les consignes** : 63 A (BoxedWine gère aussi Linux), D25 (le repli VM/DOS ne clôt plus V1), D27 (mode brut en V2), D28 (DOS au terminal « si raisonnable »), D50 (même bouteille quelle que soit l'app), D58 A puce 3 (console dans une bouteille acceptée), D43 (accord admin, voir 123), 64 (BoxedWine affiche aussi les programmes Linux).
- **Contradictions à trancher** : séries [7](serie-7-envoyee.md) (101 à 119) et [8](serie-8-envoyee.md) (120 à 139). Les numéros 77 à 100 restent réservés à ChatGPT. Essais prévus : [etudes-prevues.md](etudes-prevues.md).

## Réponses du 2026-11-10 aux séries 7 et 8 (premier lot : 101, 105, 134)

Tes consignes du soir (document « Consignes pour commencer ») répondent à trois questions et **changent la règle de lecture pour ce lot : les autres questions des séries 7 et 8 ne sont pas acceptées par défaut, elles restent à examiner.** Une recommandation n'est retenue qu'après ta validation explicite. Si une décision ouverte bloque une partie du travail, je te demande de trancher avant d'implémenter ; les essais et les tâches indépendantes continuent.

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 101 | Code jetable de mesure | **A, acceptée** : les prototypes jetables commencent tout de suite, avant le plan, « notamment démarrage, stockage, affichage et son » (E1, E2, E4, E5 de [etudes-prevues.md](etudes-prevues.md) ; E3 et E6 à E9 suivent). Pas du code de production, aucune PR. Je présente les résultats, les limites et mes recommandations. | — |
| 105 | Gestion des VM par BoxedWine | **C, tes mots** : « un utilisateur standard peut gérer seulement ses propres VM. Un administrateur dispose d'un contrôle total sur les VM. » À vérifier dans Kazo : comment les permissions existantes appliquent cette distinction, et la portée de « gérer les sous-systèmes » face à D32 (« une app ne contrôle que les sous-systèmes de l'usager en session »). Résultat de la vérification : [verification-105-134.md](verification-105-134.md). | D32, D42 |
| 134 | Console avancée | **B, précisée par tes mots** : console avancée en V1. « Si l'héritage fin des permissions est trop complexe, les commandes lancées dans la console utilisent les droits déjà accordés à la bouteille. » À vérifier : les garanties, et la distinction entre cette console et les commandes internes dont BoxedWine a besoin pour lancer les programmes. Résultat : [verification-105-134.md](verification-105-134.md). Ça change D58 A puce 3 (confirmé) ; la recommandation 134 A tombe. | D20, D50, D58 |
| 140-149 | Questions issues de ces réponses | Posées dans [serie-9-pour-chatgpt.md](serie-9-pour-chatgpt.md) (document autonome, à transmettre à ChatGPT) : 140 contrôle total de l'admin, 141 permission « gérer ses environnements », 142 mode invité, 143 permissions dangereuses déclarées par une Feature, 144 à 148 console avancée, 149 stockage des disques (essai E2). | — |
| 150 | Fenêtres Wine à 1x1 pixel, périmètre du jalon 0 | **B, tranchée le 2026-11-10** : le jalon 0 est redéfini sur les programmes console (Wine `cmd`, programmes DOS, Linux) ; Wine graphique est repoussé à un jalon ultérieur sans date. Le bogue reste à comprendre à part (pistes dans [../essais/e1-wine-v86.md](../essais/e1-wine-v86.md), [../essais/e4-fenetres.md](../essais/e4-fenetres.md) et E13 : paquets i386 de WineHQ pour tester un autre Wine). Question posée dans [point-etape-2026-11-10.md](point-etape-2026-11-10.md). | D25, D26 |
| 151 | Bouteille gabarit | **A, tranchée le 2026-11-10** : la première bouteille est fabriquée hors ligne dans l'état gabarit ; chaque nouvelle bouteille est une copie de cette bouteille gabarit (copie-sur-écriture à chiffrer), jamais un `wineboot` chez l'usager ; une mise à jour de Wine remplace l'état gabarit et la bouteille gabarit ensemble (C13-C14, 126). | D41 |

## Reste à débattre après la série 6 (réponses 62, 63 et 64)

Détail des recherches vérifiées du 2026-11-09 : [../recherche-2026-11-09/](../recherche-2026-11-09/) (en anglais ; `critique.md` liste les ambiguïtés qui ont servi à la série 3, `reponse-40/` celles de la série 5).

- **C36 à C39** : envoyées en série 6 (73 à 76). **C33** (création d'un sous-système) attend la réponse à 67 (63 A est acceptée).
- **Série 6 à revoir après 62 B :** les points « À trancher » des effets ci-dessus, puis les questions 65 à 76.
- **Points ouverts par l'analyse** : un deuxième front end Windows (une autre app que BoxedWine qui lance des programmes Windows : voit-elle les bouteilles et la bibliothèque de BoxedWine? voir 52, 61.12 et 65) ; l'export d'une seule bouteille (aujourd'hui, 34 n'exporte que le disque entier d'une VM).
- **Reste de la carte** : C6 (v86 officiel ou fork) ; C7 et C9 à confirmer seulement (4, 9, 17, 24 et 25 les couvrent) ; C13-C14 (images), C15 (ordre des ADR), C18 (Windows : ISO et licence), C19 (worker dans le modèle de processus) ; C21-C23 (stockage), avec deux points laissés ouverts : la copie-sur-écriture des bouteilles au niveau des fichiers ou des blocs (56, règle 3) et l'image avec Wine qui partage ou non la base de ton Linux (58, puce 5) ; C24 est couvert par 34 et 48 ; C25-C29 (fichiers), C30-C32 (réseau), C40-C41 (performance), C42-C44 (structure et tests), C45 (jalons). C10 (DOS) est tranchée par 28.
