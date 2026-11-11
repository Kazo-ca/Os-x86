# Série 9 — questions issues de tes réponses 101, 105 et 134 (2026-11-10)

Document à transmettre à ChatGPT tel quel. Il est autonome : il rappelle le contexte, donne les faits vérifiés, puis pose les questions à trancher, avec des options et une recommandation. Les questions 101 à 139 des séries 7 et 8 restent ouvertes (sauf 101, 105 et 134, répondues) ; ce lot ne les remplace pas, il en découle. Rien n'est accepté sans réponse explicite, par numéro.

## Contexte en dix lignes

- **Kazo OS** : système d'exploitation dans le navigateur (Blazor WebAssembly, .NET 10), dépôt Kazo-ca/Os. Rôles d'usager : `User`, `Administrator`, `System`. Les permissions des apps sont « normales » (accordées sans question) ou « dangereuses » (approuvées une fois par un admin pour toute la machine ; chaque usager peut refuser pour lui, ADR 0049). Les opérations d'administration exigent, en plus, une authentification d'admin dessinée par l'OS à chaque opération, mot de passe compris, même pour un admin.
- **Os-x86** : « Feature » facultative de Kazo (installée par un admin) qui fait tourner des machines virtuelles x86 32 bits (moteur v86) : Linux avec Wine pour les programmes Windows, FreeDOS, et des VM complètes. Il a une commande `x86`, des fenêtres de sécurité (permission réseau, page des règles) et une interface (`Os-x86.Interface`) que d'autres apps appellent. Il n'a aucune app graphique.
- **BoxedWine** : app Kazo ordinaire, interface d'Os-x86 : reçoit les `.exe` (double-clic, `./setup.exe` au terminal), affiche les programmes, règle les VM de l'usager.
- **Bouteille** : préfixe Wine isolé, avec son propre espace réseau, son adresse IP et son propre compte dans la VM ; en principe une par programme Windows lancé depuis Kazo (D20). Sous Wine, toutes les connexions réseau d'une bouteille passent par un même `wineserver` : deux programmes d'une même bouteille ne peuvent pas avoir deux décisions réseau différentes.
- **Décisions citées** : D20 (une bouteille par programme ; ce qu'elle installe et lance partage son identité ; nouvelle empreinte SHA-256 = nouvelle fenêtre de permission), D21 (le droit effectif est l'intersection des droits d'Os-x86, de l'appelant, du sous-système, de la décision de l'usager et du relais), D22 (fichiers par sous-système ; « un `.exe` voit son dossier et ses arguments » ; un compte invité par bouteille), D32 (deux permissions déclarées par Os-x86 : « lancer des programmes », normale ; « gérer les sous-systèmes », dangereuse ; « une app ne contrôle que les sous-systèmes de l'usager en session »), D35 (pas de root dans l'invité), D42 (commandes et fichiers de l'invité : « lancer des programmes » dans les bouteilles que l'app a créées ou que l'usager a choisies par elle ; « gérer les sous-systèmes » ailleurs), D50 (limite acceptée : BoxedWine peut lancer des commandes dans ses bouteilles), 63 A (BoxedWine règle les VM par une troisième permission dangereuse), 102-103 (ouvertes : une VM appartient-elle à un usager), 107 (ouverte : les programmes voient-ils ce que la VM voit, ou seulement leur dossier), 122 (ouverte : page d'admin d'Os-x86), 125 (ouverte : mode invité).

## Les réponses que tu as données le 2026-11-10

- **101** : les prototypes jetables de mesure commencent tout de suite (démarrage, stockage, affichage, son). Pas de code de production, pas de PR.
- **105** : un usager standard gère seulement ses propres VM ; un administrateur a le contrôle total. À vérifier : comment les permissions existantes de Kazo appliquent cette distinction, et la portée de « gérer les sous-systèmes » face à D32.
- **134** : console avancée en V1 ; si l'héritage fin des permissions est trop complexe, ce qui est lancé dans la console reçoit les droits déjà accordés à la bouteille. À vérifier : les garanties, et la différence avec les commandes internes de BoxedWine.

## Faits vérifiés dans le code de Kazo (commit cd89ee0, 2026-11-10)

1. Une app connaît l'identifiant stable (GUID) de l'usager en session, son rôle, et si elle tourne élevée par `sudo` (`IAppExecutionContext.UserId`, `UserAuthority`, `IsRootElevated`).
2. « Ses propres données » existe déjà comme modèle : `filesystem.user-home.*` ne couvre que le dossier de l'usager en session ; celui d'un autre usager tombe sous `filesystem.system.*`, dangereuse.
3. Toute opération d'administration de Kazo exige une permission dangereuse **et** une authentification d'admin par l'OS à chaque opération. Aucune passerelle ne se fie au rôle affiché de l'usager.
4. **Aucune app ni Feature ne peut déclencher cette fenêtre d'admin de l'OS pour une opération à elle** : la porte (`AdministratorGate`) est interne aux passerelles intégrées. Le seul chemin ouvert aux apps est une boîte de dialogue **de l'app** qui recueille le mot de passe elle-même, ce qui contredit l'écran sécurisé que nous voulons.
5. **Une commande de terminal peut être lancée par `sudo`** (ADR 0042) : elle reçoit alors `IsRootElevated`, vérifié par Kazo, avec la fenêtre de l'OS. `x86` est une commande : `sudo x86 …` est donc disponible sans nouvel ADR.
6. L'« autoriser » d'une permission dangereuse n'existe qu'au niveau machine ; le « refuser » existe par usager. C'est le mécanisme du veto de l'admin.
7. **Bogue qui pèse sur nos ADR** : à la connexion, Kazo accorde en silence toute permission déclarée par une app qui n'est pas dans **son** catalogue de permissions dangereuses ; tout identifiant inconnu est traité comme « non dangereux ». Une permission déclarée par Os-x86 (« gérer les sous-systèmes ») serait donc accordée sans question tant que l'ADR « permissions déclarées par un fournisseur » ne permet pas de déclarer une permission dangereuse reconnue par cette passerelle.
8. En mode invité, le compte `guest` est un **administrateur** sans mot de passe (ADR 0187 §2), qui n'existe que tant qu'aucun compte n'est enregistré ; l'élévation y est une confirmation d'un clic. Il n'y a donc jamais « les VM d'un autre usager » à protéger en mode invité.
9. **Fait Wine** : l'« héritage fin » (une décision réseau par programme à l'intérieur d'une bouteille) est impossible pour les programmes Windows sans modifier Wine (un seul `wineserver` par bouteille). La règle « droits de la bouteille pour tout ce qui s'exécute dedans » n'est pas un repli : c'est le seul modèle possible en V1 pour Windows.
10. **Mesure E2 (stockage, 2026-11-10, Windows 11, Chrome 156, Edge 157, Firefox 159)** : lire ou écrire un bloc de 4 Kio dans l'OPFS (`createSyncAccessHandle`, depuis un worker) prend 10 à 30 µs dans Chrome (60 à 180 µs dans Firefox) ; dans IndexedDB par morceaux de 256 Kio, 1,3 à 5,6 ms (3,3 à 12,7 ms dans Firefox), soit 55 à 185 fois plus. Écrire une image de 1 Gio : 1 s en OPFS, 6 à 26 s en IndexedDB. Quota annoncé 10 Gio partout (disque presque plein pendant la mesure ; à remesurer). `persist()` rend `false` sans geste de l'usager.

## Questions à trancher

### Suite de 105 (gestion des VM)

**140. Comment l'admin obtient-il le « contrôle total » en V1?**
> Fait 4 : Kazo ne peut pas donner à une app la fenêtre d'admin de l'OS. Fait 5 : `sudo x86 …` l'obtient déjà.

- A) En V1, par `sudo x86 …` seulement (fenêtre de l'OS, même pour un admin). Aucune app n'agit sur les VM d'un autre usager : D32 tient mot pour mot, « gérer les sous-systèmes » n'est pas déclarée tant que l'ADR des permissions de fournisseur ne donne pas aux Features l'agent d'authentification de l'OS. La page d'admin de 122 vient plus tard. ✨(Recommandé)
  - Moins d'ADR avant le premier jalon ; le contrôle total existe dès le début, en ligne de commande.
- B) Dès le premier jalon, l'ADR « permissions déclarées par un fournisseur » donne aux Features l'accès à l'agent d'authentification de l'OS. Une app qui a « gérer les sous-systèmes » peut alors agir sur toutes les VM, avec une authentification d'admin par l'OS à chaque opération. D32 se précise : « sauf avec “gérer les sous-systèmes” et une authentification d'admin de l'OS pour chaque opération ».
  - BoxedWine peut offrir l'administration ; l'ADR du premier jalon grossit.
- C) Autre : (Spécifiez)

**141. La permission des apps pour « ses propres VM » : nom et niveau**
> Fait 2 : Kazo distingue déjà « ses données » et « celles des autres ». 63 A prévoyait une troisième permission dangereuse « configurer les VM de programmes Windows ».

- A) Une seule permission « gérer ses environnements », **dangereuse** (approuvée une fois par un admin pour la machine, refusable par chaque usager), qui couvre régler, démarrer, arrêter, supprimer les VM de l'usager en session, pour les genres de VM que BoxedWine gère (Linux, Windows, DOS ; VM complètes selon 104). Jamais les VM d'un autre usager, quel que soit le rôle de l'usager. ✨(Recommandé)
- B) Deux permissions : « gérer ses environnements » normale pour régler, démarrer, arrêter ; dangereuse pour supprimer et exporter.
- C) Autre : (Spécifiez)

**142. Mode invité**
> Fait 8 : l'invité de Kazo est un admin sans mot de passe, seul sur la machine. La recommandation 125 A (« toutes les opérations d'admin refusées en mode invité ») va contre ce modèle.

- A) Retirer 125 A : en mode invité, l'invité fait tout ce qu'un admin fait, après la confirmation d'un clic (celle de Kazo). ✨(Recommandé)
- B) Garder 125 A : Os-x86 refuse ses opérations d'admin à l'invité même si Kazo l'autoriserait.
- C) Autre : (Spécifiez)

**143. Permissions dangereuses déclarées par une Feature**
> Fait 7 : sans correction, « gérer ses environnements » et « gérer les sous-systèmes » seraient accordées en silence à la connexion.

- A) L'ADR « permissions déclarées par un fournisseur » (déjà prévu avant le premier jalon) permet de déclarer une permission **dangereuse**, traitée comme celles du catalogue de Kazo (approbation admin, refus par usager, jamais accordée en silence). Le signalement de 135 (bogue de l'ADR 0040) en est le même fond. ✨(Recommandé)
- B) Os-x86 n'utilise que des permissions du catalogue de Kazo (par exemple `process.manage`) tant que l'ADR n'existe pas.
- C) Autre : (Spécifiez)

### Suite de 134 (console avancée)

**144. Écrire « droits de la bouteille » comme la règle de V1**
> Fait 9.

- A) Oui : tout ce qui s'exécute dans une bouteille (programme lancé par un programme, commande interne de BoxedWine, console) a les droits de la bouteille. L'héritage fin ne revient qu'avec un mécanisme par exécutable pour les programmes Linux, en V2 au plus tôt. ✨(Recommandé)
- B) Garder « héritage fin » comme objectif V1 pour les programmes Linux lancés dans une bouteille, « droits de la bouteille » pour Windows seulement.
- C) Autre : (Spécifiez)

**145. Deux opérations distinctes dans l'interface d'Os-x86**
> La console doit se distinguer des commandes internes de BoxedWine.

- A) « Exécuter dans la bouteille » : non interactive, arguments structurés (pas de ligne de shell), durée bornée, sortie rendue à l'app, journalisée, permission « lancer des programmes » dans les bouteilles de l'app. « Ouvrir la console » : sur geste de l'usager, dans une fenêtre d'Os-x86 ou un onglet du Terminal de Kazo, **inaccessible à l'app** (elle ne peut ni y écrire ni en lire), compte comme une application qui garde la VM en marche. La fenêtre de la console affiche la bouteille, ses droits réseau et l'avertissement « tout ce que tu lances ici a les droits de la bouteille ». Aucune opération n'accorde de permission. ✨(Recommandé)
- B) Une seule opération « exécuter une commande » avec une option interactive, ouverte aussi aux apps.
- C) Autre : (Spécifiez)

**146. 107 doit être tranchée avant d'implémenter la console**
> La garantie « la console voit ce que la bouteille voit » n'a de sens que si l'accès aux fichiers est défini par VM. Avec D22 tel quel (« un `.exe` voit son dossier et ses arguments »), la console verrait plus qu'un programme.

- A) 107 A : accès aux fichiers par VM, avec les droits déjà accordés par Kazo (lecture seule reste lecture seule) ; tous les programmes d'une VM, console comprise, voient la même chose. Ça remplace D22 pour les fichiers. ✨(Recommandé)
- B) 107 B : une permission « fichiers » par programme, demandée au premier accès hors du dossier du programme ; la console demande à chaque dossier.
- C) Autre : (Spécifiez)

**147. Fermeture de la console**

- A) Fermer la console termine le shell ; un programme graphique lancé depuis la console continue dans la bouteille et apparaît comme un programme de la bouteille. ✨(Recommandé)
- B) Fermer la console arrête aussi tout ce qu'elle a lancé.
- C) Autre : (Spécifiez)

**148. Programme déjà présent dans une bouteille, lancé par Kazo (raccourci de la bibliothèque, `./x.exe`)**
> Deux cas à distinguer (tes consignes §6) : une app Kazo demande d'ouvrir un programme identifié ; un programme invité crée ses enfants.

- A) Un lancement **par Kazo** suit la route normale : empreinte connue (enregistrée par la bibliothèque à l'installation) → même bouteille, pas de fenêtre ; empreinte inconnue → fenêtre de permission et choix de bouteille par Os-x86. Un lancement **par un programme** dans l'invité ne passe par rien : droits de la bouteille. La console est le second cas, avec l'usager comme « parent ». ✨(Recommandé)
- B) Tout programme déjà présent dans une bouteille est lancé avec les droits de la bouteille, même depuis Kazo, sans fenêtre.
- C) Autre : (Spécifiez)

### Suite de l'essai E2 (stockage)

**149. Où ranger les images disque**
> Fait 10.

- A) Images disque dans l'OPFS (`createSyncAccessHandle` depuis le worker de la VM), `flush()` périodique et à l'arrêt plutôt qu'à chaque bloc ; IndexedDB et les Fichiers de Kazo seulement pour les métadonnées, les états et les fichiers de l'usager ; `persist()` demandé après un geste de l'usager ; l'éviction par le navigateur traitée comme un risque réel (avertissement, export). Les parties copie-sur-écriture et base partagée de E2 restent à mesurer. ✨(Recommandé)
- B) IndexedDB malgré tout (une seule technologie de stockage avec Kazo), au prix de 50 à 185 fois plus par bloc.
- C) Autre : (Spécifiez)

## Rappel : ce qui reste ouvert des séries 7 et 8

102-104 (VM par usager ou commune ; BoxedWine gère-t-il les VM complètes), 106 (fenêtres simples d'Os-x86 sans BoxedWine), 107 (reposée en 146), 108-111 (origine du lancement, console dans le terminal, code de sortie, DOS au terminal), 112-113 (fenêtre par fenêtre ; état « déjà démarré »), 114-116 (synchro, export, réseau simulé), 117-119 (marque anti-imitation, jalons, `x86 shell`), 120-124 (réglages, profils, page d'admin, consentement admin, liste des opérations), 125 (reposée en 142), 126-133 (téléchargement, mise à jour indispensable, import, retrait, bibliothèque, suppression de compte, capture, son), 135-139 (issues Kazo, presse-papiers réel, journal, CI, liens ouverts par l'invité).
