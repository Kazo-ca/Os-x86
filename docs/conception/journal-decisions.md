# Journal des décisions — Os-x86

Débat de conception. Aucun code avant que le plan soit approuvé.
Carte complète des questions et contraintes : [../recherche-2026-11-08/decisions.md](../recherche-2026-11-08/decisions.md) (numérotée C1 à C45 ci-dessous).
État final de chaque point, par thème, avec les décisions remplacées et ce qui reste ouvert : [registre-reconcilie.md](registre-reconcilie.md) (2026-11-11). Le plan (consignes §20) : [plan-2026-11-11.md](plan-2026-11-11.md), avec ses propositions à valider dans la [série 12](serie-12-envoyee.md) (173 à 199). Ce journal reste la source chronologique.

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

## Réponses du 2026-11-11 aux séries 7, 8 et 9 (lot complet, 102 à 149)

Source : ton document [reponses-2026-11-11.md](reponses-2026-11-11.md) (consigné avec ChatGPT pendant un appel, avec les extraits de transcription). Règles de lecture reprises de ce document : un « oui » ne valide que ce qui a été présenté à l'oral, pas les sous-options non lues ; une réponse libre n'est pas forcée dans A, B ou C ; les réserves « à préciser » de chaque réponse restent applicables. Les réponses plus récentes (144, 146, 150, 151) priment sur les formulations antérieures. Les tensions relevées sont posées en [série 10](serie-10-envoyee.md).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 102 | Propriété d'une VM Programmes | **Par phases** : V1 environnements partagés utilisés à tour de rôle ; V2 base commune et différentiel personnel persistant. Ne pas confondre avec le choix privée/partagée des VM complètes (103). | D32 |
| 103 | VM complète commune | **Tranchée** : VM complète privée ou accessible aux autres usagers Kazo, au choix ; l'invité gère ses comptes ; un usager à la fois ; la VM privée est protégée effectivement. | 105 |
| 104 | BoxedWine et les VM complètes | **Oui**, BoxedWine gère aussi les VM complètes ; le rôle d'interface ne confère pas tous les droits. | 63 |
| 106 | Écran sans BoxedWine | **Tranchée** : Os-x86 est le moteur et le cadre de rendu ; il n'ouvre aucune fenêtre de VM propre. Ses fenêtres de sécurité restent (63 A). Tension avec 109 et 111 → série 10. | 63 |
| 107 | Portée des fichiers | **Principe** : même bouteille, mêmes permissions ; ça ne veut pas dire que tous les programmes voient tous les dossiers de la VM (précisé par 146). Ni 107 A ni 146 A tels quels. | D22, 146 |
| 108 | Autre app lance un programme autorisé « toujours » | **Tranchée, règle modifiée** : pas de nouvelle demande et **pas de plafond fondé sur le lanceur** (le plafond de 108 A est écarté). À réconcilier avec D21, D43, D52. | D21, D43, D52 |
| 109 | Sortie d'un programme console lancé par `./x.exe` | **Minimum V1** : une fenêtre console séparée suffit. Sortie dans le terminal courant et mode brut non exigés, pas interdits. Qui affiche cette fenêtre → série 10. | D27, 106 |
| 110 | Vrai code de sortie | **Calendrier** : souhaité en V1 si possible, obligatoire en V2 ; voie technique (changements au shell) à étudier ; lancement via BoxedWine conservé. | 62 |
| 111 | `.exe` DOS au terminal | **Principe** : un programme DOS tapé au terminal va directement à Os-x86/FreeDOS, sans BoxedWine ; les programmes Windows passent par BoxedWine. Reconnaissance du contenu (COM, NE, PE, extensions) à concilier. | D7, D28 |
| 112 | Fenêtre par fenêtre | **Commencer simple**, réévaluer à la fin de V1 selon l'architecture retenue (agent dans la VM à examiner, pas choisi). Ni obligation en fin de V1 ni report ferme en V2. Essai E4 fait. | D26 |
| 113 | État « déjà démarré » | **V1 : toujours démarrer à froid** ; état modèle évalué pour V2. Tension avec 151 A → série 10. | D25, 151 |
| 114 | Synchro entre appareils | **V2** pour les disques, la bibliothèque et les décisions d'autorisation. Mécanismes non choisis. | — |
| 115 | Sauvegarde demandée par l'usager | **Objectif** : retrouver l'environnement complet dans un seul fichier ; VM incluses par défaut, exclusion possible. Mécanisme et adaptations à proposer (E15). | C22 |
| 116 | Réseau simulé | **Orientation** : viser plus complet à terme, version complète peut-être en V2 ; examiner ce qui est faisable en V1 et proposer des options avec leurs limites (après E6). Ni 116 A ni 116 B acceptées. | C31, E6 |
| 117 | Fausses fenêtres de confiance | **Marque de Kazo** sur les fenêtres des programmes invités (première méthode) ; position, forme, plein écran à spécifier. | — |
| 118 | Jalons démontrables | **Confirmé** ; le détail des jalons n'est pas validé par là. | — |
| 119 | Linux en ligne de commande | **Point de départ V1** : shell interactif dans le terminal Kazo, même avec des touches manquantes ; intégration plus complète ensuite. | D27, E14 |
| 120 | Réglages d'une VM | **Options Wine dans Os-x86**, avec les réglages de la VM. | — |
| 121 | Profil partageable | **Contenu** : réglages de la VM et liste de programmes ; ni disques, ni bouteilles, ni autorisations (« la recette, pas le contenu »). Format et import/export ouverts. | — |
| 122 | Limites et veto de l'admin | **Page admin Os-x86 dans Paramètres → Fonctionnalités** plus commande équivalente au terminal. | D43 |
| 123 | Accord admin pour BoxedWine | **À l'installation de BoxedWine** ; à l'usage, opérations limitées aux droits de l'usager connecté. Changements Kazo nécessaires à spécifier. | D43 |
| 124.1 | VM commune : administration | **Accepté avec précision** : admin seul règle, supprime, partage ; démarrer/arrêter : usager admis, un à la fois ; **plus** attribution d'une VM à un usager précis et restrictions par VM (certaines réservées aux admins, même pour démarrer). Calendrier → série 10. | 102, 103 |
| 124.2 | Arrêt avec travail non enregistré | **Confirmation** ; vérifier ce que l'invité permet de détecter. | 66 |
| 124.3 | Suppression disque/bouteille/point | **Confirmation par défaut** ; au terminal, option explicite (ex. `--force`) pour la sauter ; droits vérifiés dans tous les cas. Modifie 66 A.5. | 66 |
| 124.4 | Import | Image partagée : **admin** ; son propre disque : **usager**. | 128, D34 |
| 124.5 | Limites admin | **Admin seul** change une limite, une règle imposée, un refus machine. | — |
| 124.6 | Permission réseau | **Jamais par l'API** : fenêtre de permission ou page des règles. | D61.2 |
| 124.7 | Commande ou shell dans une VM | **Réconcilié avec 134 et 144** : la restriction « rien en V1 si l'héritage est reporté » est écartée ; permission exacte à préciser. | 134, 144 |
| 124.8 | Dossier Kazo hors permissions | **Fenêtre d'Os-x86** de demande d'autorisation. | 107 |
| 124.9 | Export disque/bouteille | **Confirmation** ; l'option terminal de 124.3 n'est pas étendue à l'export. | — |
| 124.10 | App par défaut d'une extension | **Confirmation de Kazo** maintenue. | D61.10 |
| 125 / 142 | Mode invité | **Quand aucun autre compte n'existe, l'invité est un admin** et peut faire les opérations admin d'Os-x86 avec les confirmations de Kazo. 125 A écartée pour ce cas. | ADR 0187 |
| 126 | « Ne plus demander » le téléchargement | **Par usager, images officielles seulement** ; image présente = aucun téléchargement ; réactivable dans les réglages. | C13 |
| 127 | Mise à jour indispensable | **App exigeant une image récente : mettre à jour ou annuler** ; mises à jour ordinaires reportables. Cas du moteur qui ne démarre plus l'ancienne image : non présenté. | — |
| 128 | Image apportée | **Import libre pour l'image personnelle**, admin pour l'image partagée ; vérifier que l'image personnelle reste dans les droits de l'usager. | D34 |
| 129 | Retirer une entrée de bibliothèque | **Laisse le programme installé** ; avant de supprimer une VM, avertir que les raccourcis seront supprimés. Comment désinstaller ensuite : ouvert. | — |
| 130 | Programmes trouvés dans les bouteilles | **Facultatif pour livrer la V1**, report en V2 possible (pas seulement un interrupteur). | — |
| 131 | Suppression d'un compte | **Deux choix distincts** : dossiers personnels, et images/VM personnelles (garder les uns sans les autres) ; VM partagées à part. | ADR 0169 |
| 132 | Capture de la souris | **Réglage par VM, exception par application** ; touche de libération configurable, toujours disponible ; aucune touche précise choisie. | — |
| 133 | Son | **Volume et muet par VM en V1 si les essais le permettent** (E5 : oui, pont worker → AudioWorklet) ; par programme plus tard ; vérifier la même chose pour les apps Kazo (souhaitable). | 75, E5 |
| 135 | Trois issues dans Kazo-ca/Os | **Accord** pour les ouvrir, sans PR. Rien n'est ouvert encore ; confirmation directe demandée en série 10 avant toute publication. | — |
| 136 | Presse-papiers réel | **V1 si les essais réussissent (E9), sinon V2** ; s'ajoute au partage de texte Kazo–invité de V1. | 74, C37 |
| 137 | Journal d'événements | **Événements personnels : usager et admins ; événements système : journal général.** | — |
| 138 | Tests entre dépôts | **PR dans Kazo au moment de l'implémentation** (« plus propre »). | — |
| 139 | Liens et documents | **L'invité gère ses liens et documents** avec ses associations (pas d'intégration vers Kazo) ; un lien lancé depuis le terminal Kazo s'ouvre dans le navigateur Kazo. Remplace 139 A. | — |
| 140 | Contrôle total de l'admin | **Administration graphique dès le premier jalon** (140 B), avec dans les Paramètres de Kazo deux sections, VM et utilisateurs, et **un rôle qui permet de gérer les VM** ; articulation avec les permissions des apps à préciser → série 10. | D32, D42 |
| 141 | Permission « ses propres VM » | **Une permission qui couvre tout, plus une permission par cas** (courant ; suppression/export). Noms et niveaux à proposer. | — |
| 143 | Permissions dangereuses d'une Feature | **Mêmes règles que Kazo** : approbation admin, refus par l'usager, jamais d'attribution silencieuse (l'ADR doit corriger le bogue du semeur). | ADR 0040 |
| 144 | Droits de la bouteille | **Règle générale de V1** : tout ce qui s'exécute dans une bouteille a les droits de cette bouteille ; permissions fines en V2 au plus tôt. Généralise 134 et 124.7. | D20, D50 |
| 145 | Deux opérations | **Distinctes** : commandes internes de BoxedWine (lancer, récupérer un résultat) et console interactive de l'usager, que l'app ne peut ni lire ni écrire ; droits de la bouteille dans les deux cas. | 134 |
| 146 | Dossiers entre bouteilles d'une même VM | **Chaque bouteille accède aux dossiers pour lesquels elle a une permission** ; deux bouteilles partagent un dossier seulement si toutes deux y sont autorisées. Mécanisme d'isolation dans une VM commune à concevoir (E6, E8). | 107, D22 |
| 147 | Fermeture de la console | **Si la console attend le programme, la fermer termine le programme** ; autres cas (graphiques, détachés) ouverts. | — |
| 148 | Programme déjà dans une bouteille | **Même environnement et même résultat quel que soit le point de lancement** (Kazo ou invité) ; permissions de sa bouteille conservées ; identification à spécifier avec 108 et 146 (E12). | D41, E12 |
| 149 | Images disque | **OPFS comme voie principale ; petites images (ex. Alpine) possibles dans IndexedDB** ; seuil à préciser ; base partagée et couches de différences à essayer. | E2 |

Série 10 (posée le 2026-11-11 dans [serie-10-envoyee.md](serie-10-envoyee.md), répondue le même jour) :

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 152 | 113 contre 151 A | **A** : démarrage à froid en V1 ; bouteille gabarit livrée en fichiers dans l'image, préfabriquée, copiée par bouteille, jamais de `wineboot` chez l'usager ; état gabarit (VM déjà démarrée) évalué pour V2. 151 A reformulée ainsi, 113 tient. | D25, D41 |
| 153 | Wine graphique et la fin de V1 | **Tes mots** : « on va essayer de la terminer à la fin de la V1, mais si trop complexe, on va la faire dans le début de la V2 ». Objectif de fin de V1 sans jalon daté ; si trop complexe, V1 se clôt sans lui et il ouvre V2. | D25, 150 |
| 154 | Affichage de la console (109) et de DOS (111) | **B : le terminal de Kazo affiche** (E14) ; Os-x86 n'ouvre aucune fenêtre (106 tient). Réserve : DOS graphique non tranché par cette réponse. | 106, D28, E14 |
| 155 | Attribution d'une VM à un usager (124.1) | **V2**. En V1, les VM communes sont visibles des usagers admis, avec les restrictions par VM de 124.1 (admin seul). | 102 |
| 156 | « Rôle qui gère les VM » (140) | **B, tes mots** : « un vrai rôle Kazo. La fonctionnalité devra avoir la possibilité de déclarer des rôles » → ADR Kazo « rôles déclarés par une Feature » à écrire, avant le premier jalon. | 140, ADR |
| 157 | Trois issues dans Kazo-ca/Os (135) | **Ouvre directement** : ouvertes le 2026-11-11 sur `main` 6aef3e0 : [Kazo-ca/Os#431](https://github.com/Kazo-ca/Os/issues/431) (`StartChild` sans capacité), [Kazo-ca/Os#432](https://github.com/Kazo-ca/Os/issues/432) (semeur : identifiants hors catalogue accordés en silence), [Kazo-ca/Os#433](https://github.com/Kazo-ca/Os/issues/433) (fichier de refus écrasable). | 135 |
| 158 | Os-X86.Interface et Ix86 | **A** : `Os-x86.Interface` = paquet de contrats qui contient `Ix86` ; `x86-Lib` l'implémente ; la Feature enregistre l'instance ; BwLib dépend de `Ix86`, BoxedWine de `IBwLib`. | D10 |
| 159 | Prénom dans le document | **A** : remplacé par « l'usager » dans la copie du dépôt. | — |

Série 11 (160 à 172), posée le 2026-11-11 dans [serie-11-envoyee.md](serie-11-envoyee.md) à partir des points ouverts du [registre](registre-reconcilie.md), répondue le même jour. Tes mots : « 162. A) sauf dans le cas que c'est l'application Terminal. […] pour le reste, je suis d'accord avec tes recommendations. Tu peux commencer le plan. » Toutes les recommandations sont donc acceptées explicitement, 162 avec ta précision.

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 160 | Contrôle total de l'admin | **A** : le rôle (156) ouvre les pages et les commandes ; chaque opération sensible (124.1, 124.4, 124.5) demande en plus l'authentification d'admin de l'OS ; « gérer les sous-systèmes » disparaît, c'est le rôle. L'ADR « rôles déclarés par une Feature » donne aux pages de la Feature l'accès à l'agent d'authentification de l'OS. | 140, 156, D32 |
| 161 | Réseau simulé en V1 | **A** : réseau Ethernet privé d'Os-x86 entre VM (une adresse par VM et par bouteille, sans NAT), sortie réelle par le relais ; aucune liaison avec le réseau simulé de Kazo en V1 ; passerelle HTTP et ADR « hôtes enregistrés par une Feature » en V2. | 116, C31 |
| 162 | Règle contenu / extension | **A, avec ta précision (tes mots)** : « sauf dans le cas que c'est l'application Terminal. Donc Os-x86 a une méthode pour déterminer le header à partir du chemin de fichier et appliquer la règle avec un fallback sur l'extension (que BoxedWine utilise aussi). Si c'est un terminal, terminal peut l'exécuter (si on a inclus la fonctionnalité de redirection des console app directement dans le terminal) sinon il l'envoie à BoxedWine. (cela prépare l'arrivée de la fonctionnalité de pouvoir exécuter une commande ou une application DOS plus tard, ou dans la v2) ». Lecture : la règle A (en-tête d'abord, extension ensuite, contradiction refusée) est un service d'Os-x86 (72.2) que BoxedWine et le Terminal appellent ; au terminal, un programme console peut être exécuté par le Terminal lui-même, par Os-x86, quand la redirection de console dans le terminal (154) est livrée ; sinon le Terminal l'envoie à BoxedWine. Ça précise 62 B et 111 pour le Terminal seulement ; le double-clic ne change pas. | 72.2, 111, 154, 62 |
| 163 | `boxedwine setup.exe` | **A** : comme `./setup.exe` (console dans le terminal, le terminal attend). | 72.8 |
| 164 | DOS graphique | **A** : affiché par BoxedWine comme un programme Windows ; sans BoxedWine, message et arrêt ; le mode texte reste dans le terminal. | 111, 154 |
| 165 | Plusieurs VM : quand demander, créer | **A** : aucune question sous la limite de mémoire (défaut : moitié de la mémoire annoncée par le navigateur, réglable par l'admin) ; au-delà, fenêtre de choix ; compatible = même genre et même image ; créer = depuis un profil (121) ou en copiant les réglages de la VM courante. | 66, 67 |
| 166 | « Application » pour l'arrêt d'une VM | **A** : programme lancé, shell interactif (119), console de bouteille (145) ; pas un processus de fond ni une fenêtre d'affichage seule. | 72.6 |
| 167 | Fermeture de la console | **A** : un programme graphique lancé depuis la console continue avec sa fenêtre BoxedWine ; un processus détaché sans fenêtre est terminé avec le shell. | 147 |
| 168 | Désinstaller | **A** : « Désinstaller » dans BoxedWine lance le désinstalleur dans la bouteille (droits de la bouteille) ; sinon, proposition de supprimer la bouteille. | 129 |
| 169 | Mise à jour indispensable (moteur) | **A** : Os-x86 démarre les images de la même version majeure ; sinon annonce avant l'installation, « mettre à jour l'image maintenant » ou « annuler », point de restauration automatique avant ; retour arrière par ce point et l'ancienne version d'Os-x86 si le Store la garde (à vérifier). | 127, D56 |
| 170 | Wine graphique : critère « trop complexe » | **A** : essai dédié d'une journée avec le Wine de WineHQ (i386) ; succès = fin de V1 ; sinon une seconde journée sur le code de Wine ; deux échecs = V1 se clôt sans lui, il ouvre V2. | 153, 150 |
| 171 | Vrai Windows invité | **A** : V2, avec un essai de faisabilité d'une journée en V1. | C18 |
| 172 | Identifiants | **A** : `org.kazoos.x86` (points d'entrée `.cli`, `.permission`, `.rules`, `.admin`), `org.kazoos.boxedwine` ; espaces de noms `OsX86.Interface`, `OsX86`, `BoxedWine`, `BoxedWine.Lib` ; NuGet `Os-x86.Interface` et `BoxedWine.Lib`. À revérifier avant publication : `org.kazoos.*` non réservé aux apps système (sinon `ca.kazo.*`). | D35 |

Série 12 (173 à 199), posée le 2026-11-11 dans [serie-12-envoyee.md](serie-12-envoyee.md) avec le [plan](plan-2026-11-11.md), répondue le même jour. Tes mots : « 185. ok pour les noms, mais bouteille est un nom français, on doit trouver quel nom anglais utiliser et je dois savoir quel nom sera utilisé. Pour le reste, je suis d'accord avec tes recommendations. » Toutes les recommandations sont donc acceptées explicitement ; 185 avec cette précision (question 200, ci-dessous).

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 173 | Partage de fichiers | **A** : programmes sur le disque de la VM, partage pour les documents, serveur 9p d'Os-x86 durci selon E8, un partage = un périphérique 9p. | 107, D22, E8 |
| 174 | Règles réseau | **A** : décision, limites et journal au relais côté Kazo (ADR « relais : identités et décisions d'une Feature ») ; nftables dans l'invité en défense ; `relay_url` fixé par la Feature ; UDP 53 et IPv6 bloqués par règle. | D20, D21, 161, E6 |
| 175 | Identité d'un programme mis à jour | **A** : chemin dans la bouteille, puis clé de signature, empreinte toujours, sélecteur en repli ; pas de chaîne X.509 en V1 ; E12 bis à faire. | D20, D41, E12 |
| 176 | Permissions | **A** : `x86.programs.launch` (normale) ; `x86.vm` (dangereuse, couvre tout) ; `x86.vm.use` (normale) ; `x86.vm.destroy` (dangereuse) ; jamais les VM d'un autre usager. | 141, D32 |
| 177 | Stockage | **A** : seuil 64 Mio pour IndexedDB ; `flush()` toutes les 5 s et à l'arrêt ; `persist()` après un geste ; éviction = avertissement et export ; espace affiché. | 149 |
| 178 | Navigateurs et mobile | **A** : pas d'exigence SharedArrayBuffer ; plancher Chrome/Edge 108, Firefox 111, Safari 16.4 ; mobile = VM de 256 Mio plein écran après E16 ; fonctions absentes affichées. | E10 |
| 179 | Fenêtre par fenêtre (mécanisme cible) | **A** : composite XComposite + XDamage, transport de pixels à revoir, 18 ajouts Kazo (ADR 23) ; plan détaillé à la réévaluation de fin de V1. | 112, E4 |
| 180 | Console et commandes internes | **A** : « exécuter dans la bouteille » non interactive, arguments structurés, durée bornée (10 min), journalisée ; console avec bouteille, droits réseau et avertissement affichés. | 145 |
| 181 | Réseau de BoxedWine | **A** : Os-x86 déclare `network.real.access` et télécharge ; BoxedWine n'a aucune permission réseau ; **D43 révisée** (BoxedWine ne déclare que les permissions de fichiers qu'il transmet). | D43, D61.7, 126 |
| 182 | Modèle de menace | **A** : ADR d'Os-x86 ; dans le périmètre : programmes invités hostiles, apps Kazo qui abusent de l'API, usagers standards ; hors périmètre : JavaScript de même origine, noyau invité compromis, admin malveillant. | D61.1 |
| 183 | Profils | **A** : JSON `.x86profile` défini par Os-x86, section « programmes » remplie par BoxedWine ; import/export par BoxedWine et `x86 profile` ; modèles prêts sous ce format. | 121 |
| 184 | Route du shell (mécanisme) | **A** : enfant du shell, nouvelle instance par lancement, flux du shell, le terminal attend ; BoxedWine pas à instance unique ; Ctrl+C ferme l'instance et Os-x86 arrête le programme tant que l'ADR 22 manque. | 162, 163 |
| 185 | Syntaxe de `x86` | **A, avec ta précision** : `x86 vm list/create/start/stop/delete/export/import/snapshot`, `x86 shell`, `x86 run`, `x86 detect`, `x86 rules`, `x86 profile`, `sudo x86 admin` ; une session `x86 shell` par VM en V1 ; `--force` sur `delete` seulement. **« Bouteille » doit avoir un nom anglais, à fixer (200).** | D60, 119, 145 |
| 186 | D21 après 108 | **A** : D21 réécrite : Os-x86 ∩ sous-système ∩ décision ∩ règles admin ∩ relais ; « l'appelant » n'y figure plus pour un programme lancé par une app ; l'expéditeur marqué sert au journal. | D21, 108 |
| 187 | Image | **A** : Alpine 3.24 x86 avec `linux-virt` ; Wine 11.0 d'Alpine si l'essai de 170 le fait marcher, sinon le Wine qui a réussi l'essai (épinglé, précise D35) ; Mono 10.4.1 et Gecko 2.47.4 ; v86 avec le correctif #1661 ; contrôle à chaque branche d'Alpine, reconstruction annuelle. | D35, E13 |
| 188 | Retour arrière, état sûr | **A** : point de restauration avant toute mise à jour ; « à la fermeture » attend la dernière application de la VM ; retour au point en cas d'échec ; notes de version si présentes. | 127, 169 |
| 189 | Jeux | **A** : fiche « fait partie d'un écosystème ; pas un jeu, mais les jeux font partie des logiciels exécutables » ; aucune mention de jeux dans Kazo-ca/Os. | C43 |
| 190 | Rangement par usager | **A** : par identifiant stable (GUID), jamais par nom. | 131 |
| 191 | Confirmations 124.2 et 124.3 | **A** : fenêtres d'Os-x86 ; BoxedWine ne fait que déclencher. | 124 |
| 192 | Marque anti-imitation | **A** : pastille de l'app qui affiche, dans la barre de titre et le bouton de la barre des tâches, dessinée par Kazo, non masquable ; bande 3 s en plein écran. | 117 |
| 193 | Choix de la bouteille | **A** : Os-x86 décide (identité, bouteille, permissions, sélecteur) ; BwLib exécute les commandes Wine dans la bouteille choisie. | D50 |
| 194 | VM partagée ou séparées | **A** : une VM par genre et par image pour toutes les bouteilles de l'usager ; VM séparées pour les VM complètes et sur demande (profil « VM dédiée ») ; limite d'isolation écrite. | §6, E6 |
| 195 | Shell Linux | **A** : par l'agent ; console série en repli pour une image sans agent (une session). | 119 |
| 196 | Image sans agent | **A** : capacités réduites et affichées ; paquet de l'agent fourni pour l'installer soi-même. | 128 |
| 197 | ADR | **A** : inventaire et ordre de la section 6 du plan tels quels. | C15 |
| 198 | Jalons | **A** : J0 (deux étapes) à J4 tels quels. | C45 |
| 199 | Réglages et dépôts | **A** : tableau de la section 9 et structure de la section 7 tels quels, lignes « proposé » comprises. | 120, C42 |

| 200 | Nom anglais de « bouteille » | **B (pas ma recommandation) : `prefix`**, le mot de Wine (`WINEPREFIX`) : `x86 shell <vm> --prefix <id>`, `x86 prefix list|delete|export`, types `Prefix`, `PrefixId` ; en français, « préfixe » remplace « bouteille » dans les nouveaux textes (interface française, spécification, ADR). Les documents déjà écrits gardent « bouteille » ; les deux mots désignent la même chose (glossaire du registre). | 185, D20 |

Question 200 posée le 2026-11-11 à la fin de [serie-12-envoyee.md](serie-12-envoyee.md), répondue le même jour (« 200 B : prefix »).

**Résultat de l'essai dédié de 170 A (2026-11-11, [../essais/e17-wine-dedie.md](../essais/e17-wine-dedie.md))** : critère atteint au jour 1 avec le Wine i386 de WineHQ (11.0, glibc) sur un invité Debian trixie i386 et le noyau `linux-virt` d'Alpine, dans v86 : Notepad 721x512, utilisable 23 à 32 s après la commande ; le Wine 11.0 d'Alpine garde ses fenêtres à 1x1 quelles que soient les options Xorg (bogue propre à cette build). Selon la règle de 170 A, **Windows graphique reste en fin de V1 (jalon J3)** ; le jour 2 n'a pas lieu. Le Wine de l'image (187) suit la clause « sinon le Wine qui a réussi l'essai » ; la base de l'image (Debian i386 plutôt qu'Alpine + chroot) est posée en [série 13](serie-13-envoyee.md), avec les propositions d'E3.

Série 13 (201 à 204), posée le 2026-11-11 dans [serie-13-envoyee.md](serie-13-envoyee.md), répondue le même jour. Tes mots : « Je suis d'accord avec tes recommendations. »

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 201 | Base de l'image Linux avec Wine | **A** : Debian trixie i386 + paquets WineHQ i386 (stable 11.0 épinglée), noyau `linux-virt` d'Alpine, init minimal, passe de dégraissage (objectif 250 Mio zstd, à vérifier) ; règle de suivi d'E13 (vérifier `binary-i386` à chaque Wine stable ; repli : Wine recompilé). Précise 187 A : Alpine ne sert plus que pour le noyau. | 187, D35, E13, E17 |
| 202 | Hébergement des images | **A** : R2 derrière un domaine de Kazo (CORS, `Range`, `Expose-Headers`, `immutable`), un fichier par morceau de 32 Mio ; GitHub Pages pour les petites images de test de la CI seulement ; origine d'hébergement à demander à Kazo ; à remesurer sur le vrai R2 avant le jalon 0. | C13, 126, E3 |
| 203 | Téléchargement et vérification | **A** : Web Worker JS livré par la Feature (Range ou fichiers de 32 Mio, `SubtleCrypto.digest` par morceau contre un manifeste SHA-256 vérifié par `integrity`, écriture directe dans l'OPFS, état de reprise dans l'OPFS, réessais à délai croissant, `cache: 'no-store'`) ; l'OPFS est le seul cache ; v86 lit l'image depuis l'OPFS ; le C# pilote et affiche la progression. À demander à Kazo : accord pour le worker JS et `no-store`. | 149, 177, E3 |
| 204 | Premier lancement Wine à froid | **A** : en V1, réduire le coût (62 à 79 s mesurés) sans état gabarit : image lue depuis l'OPFS (203) et préchargement des fichiers chauds de la bouteille par la recette d'image ; objectif mesuré au jalon J3 : premier programme utilisable moins de 60 s après l'invite ; sinon 152 est reposée pour V1. | 152, E17 |

**Résultat de l'essai E7 (2026-11-11, [../essais/e7-agent-processus.md](../essais/e7-agent-processus.md))** : critère atteint aux trois runs : A (Notepad de la bouteille b1) arrêté par l'agent en 18 à 20 ms d'aller-retour, processus disparu en 0,9 à 1,4 s, B et C (bouteille b2) et la VM intacts ; 22 contrôles R134.5 sur 22 tenus depuis une console d'usager, bombe de processus contenue ; un programme Wine n'écrit hors de son home que dans `/tmp/.wine-<uid>` (0700). La conséquence d'échec du plan (modèle simplifié « VM = un processus Kazo ») n'est pas déclenchée. Agent C statique de 108 Kio, 3,6 ms par aller-retour ; un cgroup v2 par lancement est l'unité d'arrêt (le pid et le pgid ne suffisent pas). Les propositions (protocole de l'agent, cgroup par lancement, modèle de processus Kazo pour l'ADR 17, console et 167 A, levée de la réserve « fichiers » de 102, disque interne, images sans agent, trous de durcissement de l'image) sont posées en série 14 avec celles d'E14.

**Résultat de l'essai E14 (2026-11-11, [../essais/e14-dos-terminal.md](../essais/e14-dos-terminal.md))** : critère atteint. Un programme DOS texte (FreeDOS 1.4 : `DIR`, `EDIT`, `CHOICE`, un `.COM` qui lit le clavier) et `cmd` de Wine (plus un `.exe` console qui lit stdin, écrit stdout et rend un code) sont utilisables dans une grille simulant le terminal de Kazo avec le jeu de touches réduit ; latence par rafraîchissement ≈ 20 ms en médiane et ≤ 36 ms pour Wine, ≈ 50 ms et ≤ 95 ms pour un DOS oisif, à 33 ms de sondage (100 ms rate le critère ; 16 ms n'apporte que 10 ms). Voie recommandée pour un programme console Wine : pty + port série ou virtio-console + émulateur VT dans le Terminal (plusieurs programmes à la fois, taille choisie, code de sortie sur le même canal, Ctrl+C natif, compatible avec le pilote DRM `bochs` d'E17 ; la console VGA tty1 ne l'est pas) ; pour DOS texte, la voie VGA. Signal 164 A : événement `screen-set-size` de v86 avec `bpp ≠ 0`, 60 à 110 ms après le passage en mode graphique. Sans Alt ni F1-F12, EDIT ne peut pas quitter par le menu : un accord Échap+lettre / Échap+chiffre, traduit par le Terminal, le rend possible. Les propositions (fréquence 33 ms, voie pty par l'agent, contenu de l'ADR 10, Ctrl+C au programme, reprise d'une VM DOS par BoxedWine, contributions v86) sont posées en série 14.

Série 14 (205 à 218), posée le 2026-11-11 dans [serie-14-envoyee.md](serie-14-envoyee.md) (propositions d'E7 et d'E14), répondue le même jour. Tes mots : « je suis d'accord avec tes recommendations. »

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 205 | Protocole de l'agent | **A** : JSON par ligne sur un port virtio-console réservé à root, réponses appariées par `id`, événements (`ready`, `exit`, `open-url`, `console-closed`), opérations `ping`, `list-processes`, `launch`, `kill`, `list-windows`, `mounts`, `exec-in-prefix`, `open-console`/`close-console` ; trois corrections : boucle non bloquante, fenêtres par connexion X permanente, plusieurs consoles multiplexées sur un second port. | D61.3, §1.1 ; E7 |
| 206 | Unité de lancement et d'arrêt | **A** : un lancement = un cgroup v2 ; arrêter = SIGTERM puis `cgroup.kill` après délai ; liste et fenêtres filtrées par cgroup ; Os-x86 expose l'identifiant du lancement, pas le pid. | ADR 17 ; E7 |
| 207 | Programmes invités dans le modèle de processus Kazo | **A** : la VM est le processus Kazo porteur des limites de l'ADR 0125 ; chaque lancement (cgroup) est un processus Kazo enfant, identité = bouteille, sans limites propres ; processus invités non exposés ; console = enfant ; enregistré à `launch`, retiré à `exit` ; rien par `StartChild`. | ADR 17, ADR 0125, #431 ; E7 |
| 208 | Fermer la console | **A** : ce qui est lancé depuis la console reste dans son cgroup ; à la fermeture, l'agent déplace dans un cgroup frère les processus qui ont une fenêtre, puis `cgroup.kill` du reste (167 A tel quel). | 147, 167 ; E7 |
| 209 | Réserve de 102 | **A** : réserve levée pour les fichiers (un usager invité par usager Kazo) ; ne porte plus que sur l'affichage partagé, traité en V2. | 102 ; E6, E7 |
| 210 | Durcissement de l'image | **A** : `chmod 755 /`, `/run` en `mode=0755`, `/dev/fb0` et `/dev/input/*` en 0600 dans la recette, et un test d'image en CI (inventaire des dossiers inscriptibles + 22 contrôles R134.5) à chaque image publiée. | E8, E7 |
| 211 | Disque interne | **A** : V2 comme prévu ; règle : « à l'arrêt » = lecture seule après un arrêt propre de la VM ; en marche = par l'agent uniquement ; jamais de lecture hôte sur une image montée. | §11 ; E7 |
| 212 | Images sans agent | **A** : VM = processus Kazo sans enfants ; écran, clavier, souris, disque, réseau ; console série offerte seulement si le profil de l'image déclare un port série ; dit à l'usager sur la page de la VM. | 195 ; E7 |
| 213 | Voie de la console Wine au Terminal | **A** : un pty par programme ouvert par l'agent, octets relayés par virtio-console, émulateur VT dans le Terminal (xterm.js ou réduit) ; code de sortie et Ctrl+C sur le même canal ; jamais un port série partagé. | 154 B, 162, 110 ; E14 |
| 214 | DOS texte au Terminal | **A** : voie VGA à 33 ms de sondage, comparaison par ligne, attributs rendus, `FDAPM APMDOS` dans l'image FreeDOS ; un onglet de terminal par VM ; réveil sur IRQ clavier proposé à v86 sans en dépendre. | 111, 154 B ; E14 |
| 215 | Contenu de l'ADR 10 | **A** : surface écran plein à largeur fixe (80 colonnes, 25 lignes ou plus) avec couleurs par cellule et curseur à ~30 Hz ; entrée touche par touche avec Ctrl+lettre ; accord Échap+lettre / Échap+chiffre traduit en Alt+lettre / F1-F12 ; Home, End, Suppr, Page si Kazo les a ; taille connue à l'ouverture ; redimensionnement à chaud en V2. | ADR 10, D27 ; E14 |
| 216 | Ctrl+C dans le Terminal | **A** : Ctrl+C va au programme invité ; tuer = Ctrl+Pause si Kazo l'a, sinon un bouton du Terminal ou `x86 kill`. | ADR 10 ; E14 |
| 217 | DOS graphique en session Terminal | **A** : signal = `screen-set-size` avec `bpp ≠ 0` ; BoxedWine reprend la VM DOS déjà lancée (même VM, canvas dans une fenêtre BoxedWine) et la rend au Terminal au retour en texte ; sans BoxedWine : message et arrêt (164 A). | 164 ; E14 |
| 218 | Dépendances à v86 | **A** : `virtio_console` plutôt que `uart1` pour l'agent et les consoles Linux ; pour DOS, curseur et attributs lus par les champs internes isolés dans un seul adaptateur ; API publique et réveil sur IRQ clavier proposés en amont ; pas de fork tant que ces champs existent. | C6, D3 ; E14 |

**Jalon 0 commencé le 2026-11-11** (« Commence le jalon 0 ») : ordre des chantiers, branches et numéros d'ADR Kazo (0243 à 0255) dans [jalon-0-execution.md](jalon-0-execution.md). Questions d'exécution 219 à 221 posées dans le chat et répondues le même jour (« je suis d'accord avec tes recommandations ») : **219 A** (série `feature/base` dans Kazo-ca/Os : les 13 ADR 0243 à 0255, une PR par ADR vers la base, puis leur code dans la même série), **220 A** (Kazo épinglé sur v3.2.1, passage à 3.3.0 à sa publication), **221 A** (`j0/base` part de `conception-os-x86` ; fusion dans `main` quand tu veux). Chantier 1 (squelette) livré : PR [Kazo-ca/Os-x86#2](https://github.com/Kazo-ca/Os-x86/pull/2) vers `j0/base`, CI verte, `x86 --version` vérifié dans le Terminal de Kazo ; fusionnée dans `j0/base` le 2026-11-11.

**Série 15 (222 à 250), le 2026-11-11** : les questions que les treize ADR du lot 1 posent au mainteneur de Kazo (PR [#436](https://github.com/Kazo-ca/Os/pull/436) à [#448](https://github.com/Kazo-ca/Os/pull/448) de Kazo-ca/Os vers `feature/base`, [#435](https://github.com/Kazo-ca/Os/pull/435) réserve les numéros ; rien n'est fusionné), reprises dans [serie-15-envoyee.md](serie-15-envoyee.md). Premier lot de réponses le même jour, trois numéros ; deuxième lot le même jour pour tout le reste (240 en D, A partout ailleurs) :

| # | Sujet | Décision | Carte |
|---|---|---|---|
| 225 | Qui marque une bibliothèque comme partagée (ADR 0244) | **A** avec précision : l'auteur de la bibliothèque, par une propriété MSBuild de son paquet ; **aucune restriction ni déclaration de plus pour l'app** qui l'utilise par référence de projet, DLL ou NuGet et l'inclut dans son paquet (`lib/`) comme toute bibliothèque. Le marquage sert seulement à charger une copie par majeure. | D10, #419 |
| 233 | Un administrateur porte-t-il tous les rôles d'office (ADR 0247) | **B** : oui, implicitement ; l'attribution explicite ne concerne que les usagers ordinaires. ADR 0247 à corriger. | 156, 160 |
| 245 | Le Terminal et `Os-x86.Interface` (ADR 0252) | **B** : le plan reste (section 6, point 10) : le Terminal dépend de `Os-x86.Interface` (null si absent) « pour pouvoir héberger éventuellement les sessions terminal » ; il devient **la seule application système à utiliser la Feature** et l'utilise comme BoxedWine, par `Ix86`. ADR 0252 à réécrire. | 53, 55, 154, 162 |
| 240 | Qui applique « en-tête d'abord » (ADR 0251) | **D** : « Os-x86 fournit une méthode pour déterminer le type d'application. C'est lui qui interprète le magic et l'extension. Ensuite le shell demande aux applications qui exposent des capacités d'exécution d'application si elles peuvent ouvrir l'application. [...] ajouter le support de MimeType (optionnel) et s'en servir aussi plus tard pour les protocoles (comme pour irc). » Lecture : Os-x86 détecte (type MIME) ; le shell demande aux apps à capacité d'exécution ; hôte du shell d'abord (251 A). ADR 0251 à réécrire ; précisions **252 à 255** posées. | 13, 62 B, 111, 162, 184 |
| 222 | Préfixe des identifiants (ADR 0243) | **A** : `ca.kazo.x86` et `ca.kazo.boxedwine` ; `org.kazoos.` reste aux apps intégrées du dépôt Kazo. Remplace 172 A. | 172 |
| 251 | Hôte de programmes du shell (ADR 0252) | **A** : `KazoShellOptions.ProgramHost`, fourni par le Terminal au démarrage du shell, consulté en premier. | 245 |
| 223, 224, 226 à 232, 234 à 239, 241 à 244, 246 à 250 | Toutes les autres questions des ADR 0243 à 0255 | **A** (« pour le reste je suis d'accord avec tes recommandations ») : recommandation de chaque question, déjà appliquée dans le texte des ADR. | — |

Les trois réponses sont appliquées dans Kazo-ca/Os le même jour (0244, 0247, 0252 corrigées ; 0245 et 0251 ajustées pour rester cohérentes ; détail et commits dans [serie-15-envoyee.md](serie-15-envoyee.md)). La réécriture de 0252 introduit un mécanisme nouveau, `KazoShellOptions.ProgramHost` (le Terminal fournit au shell un hôte de programmes consulté avant le handler binfmt) : question 251, répondue A dans le deuxième lot.

**Deuxième lot, le même jour** : 240 D change le mécanisme de l'ADR 0251 (plus de `Magic` lu par le shell ni d'extension « exécutable » : Os-x86 détecte le type du programme et le rend en type MIME ; le shell demande aux apps qui déclarent une capacité d'exécution ; types MIME facultatifs, réutilisés pour les protocoles, que Kazo prend déjà par `x-scheme-handler/<schème>`) ; quatre précisions posées, **252 à 255** ([serie-15-envoyee.md](serie-15-envoyee.md)), **répondues A partout le même jour** : 252 A détecteurs de format déclarés par les Features (`FormatDetectors`, remplace `BinaryFormats`), 253 A action `execute` des `FileHandlers` plus `CanExecuteAsync` (remplace `FileHandler.Executable`), 254 A association par défaut de l'usager puis « Ouvrir avec », 255 A types MIME de freedesktop ou de l'IANA et protocoles par `x-scheme-handler/<schème>`. 222 A change les identifiants du squelette (`ca.kazo.x86`, PR [#3](https://github.com/Kazo-ca/Os-x86/pull/3)). Tout le reste en A. La série 15 est close. **Le même jour, sur ton « Oui, fusionne », les 13 ADR sont fusionnées dans `feature/base`** ; 0251 réécrite et renommée `0251-shell-route-to-execute-capable-apps.md`. Deux précisions venues du code de Kazo (détail dans [serie-15-envoyee.md](serie-15-envoyee.md)) : refus au lancement plutôt que `CanExecuteAsync` (253 A), et, sans détecteur, le `MediaType` du premier `FileHandler` de l'extension plutôt qu'une table de Kazo (252 A).

Sujets ajoutés pendant l'appel, sans numéro (à garder tels quels) : conteneurs toujours actifs gérés par l'admin, partagés en V1, attribution personnelle en V2 ; **traduction automatique des chemins Kazo** des dossiers partagés vers leur point de montage dans l'invité (ex. `/home/<usager>` → `D:`), sans nouveau droit ; **architecture des bibliothèques** : BoxedWine (app, BW), Os-x86 (Feature, x86), Boxed-WineLib (BwLib, réutilisable), x86-Lib (réutilisable), contrats IBwLib et Ix86, « Os-X86.Interface implémente Ix86 », BwLib dépend de Ix86 seulement (précision demandée en série 10) ; **périphériques** : admins seuls pour les VM partagées, l'usager pour la VM qu'il a créée.

## Reste à débattre après la série 6 (réponses 62, 63 et 64)

Détail des recherches vérifiées du 2026-11-09 : [../recherche-2026-11-09/](../recherche-2026-11-09/) (en anglais ; `critique.md` liste les ambiguïtés qui ont servi à la série 3, `reponse-40/` celles de la série 5).

- **C36 à C39** : envoyées en série 6 (73 à 76). **C33** (création d'un sous-système) attend la réponse à 67 (63 A est acceptée).
- **Série 6 à revoir après 62 B :** les points « À trancher » des effets ci-dessus, puis les questions 65 à 76.
- **Points ouverts par l'analyse** : un deuxième front end Windows (une autre app que BoxedWine qui lance des programmes Windows : voit-elle les bouteilles et la bibliothèque de BoxedWine? voir 52, 61.12 et 65) ; l'export d'une seule bouteille (aujourd'hui, 34 n'exporte que le disque entier d'une VM).
- **Reste de la carte** : C6 (v86 officiel ou fork) ; C7 et C9 à confirmer seulement (4, 9, 17, 24 et 25 les couvrent) ; C13-C14 (images), C15 (ordre des ADR), C18 (Windows : ISO et licence), C19 (worker dans le modèle de processus) ; C21-C23 (stockage), avec deux points laissés ouverts : la copie-sur-écriture des bouteilles au niveau des fichiers ou des blocs (56, règle 3) et l'image avec Wine qui partage ou non la base de ton Linux (58, puce 5) ; C24 est couvert par 34 et 48 ; C25-C29 (fichiers), C30-C32 (réseau), C40-C41 (performance), C42-C44 (structure et tests), C45 (jalons). C10 (DOS) est tranchée par 28.
