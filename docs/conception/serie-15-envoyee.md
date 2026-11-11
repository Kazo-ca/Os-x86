# Série 15 — envoyée le 2026-11-11 : les questions des ADR de Kazo (222 à 250)

Les treize ADR du lot 1 (0243 à 0255, PR [#436](https://github.com/Kazo-ca/Os/pull/436) à [#448](https://github.com/Kazo-ca/Os/pull/448) de Kazo-ca/Os vers `feature/base` ; [#435](https://github.com/Kazo-ca/Os/pull/435) réserve les numéros) posent chacune une à trois questions au mainteneur de Kazo. Elles sont reprises ici dans la numérotation d'Os-x86, avec les options et la recommandation de chaque ADR ; la recommandation A est appliquée dans le texte des ADR en attendant la réponse. Même protocole : une lettre par numéro ; **rien n'est accepté sans ta réponse par numéro**.

Deux faits revérifiés sur Kazo `main` (6aef3e05), sans question : `persist()` a un wrapper mais aucun appelant ; le relais identifie un flux par compte, appareil et app, sans usager.

---

## ADR 0243, type d'app « Feature » (#436)

**222. Préfixe des identifiants.** Le code de Kazo ne réserve pas `org.kazoos.`, mais l'app IRC publiée par Kazo utilise `ca.kazo.irc`.
- A) `ca.kazo.x86` et `ca.kazo.boxedwine` ; `org.kazoos.` reste aux apps intégrées du dépôt Kazo ✨(Recommandé) — remplace 172 A
- B) `org.kazoos.x86` et `org.kazoos.boxedwine` (172 A tel quel)
- C) Réserver `org.kazoos.` aux apps système dans le validateur et prendre A
- D) Autre : (Spécifiez)

**223. Le consentement d'installation vaut-il approbation machine des permissions dangereuses de la Feature?**
- A) Oui : l'admin voit l'ensemble à l'installation et ne retape pas son mot de passe au premier lancement de `x86` ✨(Recommandé)
- B) Les deux : consentement à l'installation, puis fenêtre au premier lancement de chaque enfant à permission dangereuse
- C) Autre : (Spécifiez)

**224. Manifestes des enfants (commande, fenêtres, pages).**
- A) Un fichier par enfant dans le paquet, validé comme un manifeste ordinaire de son type ✨(Recommandé)
- B) Un seul manifeste avec un tableau `entryPoints` (schéma nouveau, validateur à réécrire)
- C) Autre : (Spécifiez)

## ADR 0244, bibliothèques partagées (#437)

**225. Qui marque une bibliothèque comme partagée?**
- A) L'auteur de la bibliothèque, par une propriété MSBuild de son paquet NuGet ; `Os-x86.Interface` le fait ✨(Recommandé)
- B) L'app, dans sa définition
- C) Toute bibliothèque de `lib/` est partagée par défaut
- D) Autre : (Spécifiez)

**226. Sans Feature installée, deux apps avec deux majeures différentes d'un contrat partagé.**
- A) Coexistent, chacune chargée pour ses apps ✨(Recommandé)
- B) La seconde installation est refusée
- C) Autre : (Spécifiez)

## ADR 0245, registre d'interfaces (#438)

**227. Forme de l'API côté app.**
- A) Façade statique `KazoFeatures` (comme `KazoApps`, `KazoUser`) sur une passerelle du contexte ✨(Recommandé)
- B) Passerelle injectée seulement, sans façade
- C) Autre : (Spécifiez)

**228. Contexte d'exécution de l'implémentation du service.**
- A) Celui de la Feature, l'identité de l'appelant passée pour le journal ; la Feature vérifie les permissions de l'appelant à chaque opération ✨(Recommandé)
- B) Celui de l'appelant, comme un composant
- C) Autre : (Spécifiez)

## ADR 0246, permissions déclarées (#439)

**229. Qui réserve le préfixe de permissions d'une Feature?**
- A) La première Feature installée qui le déclare ; une seconde est refusée ✨(Recommandé)
- B) Un registre du catalogue officiel du Store, vérifié à la publication
- C) Autre : (Spécifiez)

**230. Les permissions « sujet » de l'ADR 0040, dans la correction du semeur (issue #432).**
- A) Classées dangereuses, approuvées comme l'ADR 0040 l'exige : fin de l'octroi silencieux ✨(Recommandé)
- B) Classées normales (comportement actuel rendu explicite)
- C) Autre : (Spécifiez)

**231. Demande d'approbation d'une permission de Feature depuis une commande du terminal.**
- A) Toujours par la fenêtre sécurisée de l'OS ✨(Recommandé)
- B) Le mode texte du terminal (imitable par un programme invité)
- C) La fenêtre sécurisée pour toutes les permissions dangereuses, Feature ou non
- D) Autre : (Spécifiez)

## ADR 0247, rôles (#440)

**232. À qui un rôle de Feature peut-il être attribué?**
- A) À tout usager, par un administrateur ; l'opération sensible se ré-authentifie par l'usager ou par un admin selon ce que la Feature demande ✨(Recommandé)
- B) Aux administrateurs seulement
- C) Autre : (Spécifiez)

**233. Un administrateur porte-t-il tous les rôles d'office?**
- A) Non, il se l'attribue ✨(Recommandé)
- B) Oui, implicitement
- C) Autre : (Spécifiez)

## ADR 0248, dépendances (#441)

**234. Dépendance obligatoire introuvable pour `kazopkg install <dépôt GitHub>`.**
- A) Arrêt avec l'identifiant et la plage ✨(Recommandé)
- B) `kazopkg` cherche un dépôt du même éditeur par convention de nom
- C) Autre : (Spécifiez)

**235. Installation personnelle (`--user`) d'une app qui dépend d'une Feature.**
- A) Permise ; la Feature va au niveau système avec l'authentification d'un admin dans le même consentement ✨(Recommandé)
- B) Refusée
- C) Autre : (Spécifiez)

## ADR 0249, section « Fonctionnalités » (#442)

**236. Contexte d'une page de Feature rendue dans Paramètres.**
- A) Celui de l'enfant « page » (ses permissions, son processus) ✨(Recommandé)
- B) Celui de Paramètres (la Feature hériterait de `apps.manage`, `users.manage`, `system.power`)
- C) Autre : (Spécifiez)

**237. Emplacement du groupe.**
- A) Un groupe « Fonctionnalités » à part, après « Applications » ✨(Recommandé)
- B) Sous « Applications », à côté de « Composants de l'OS »
- C) Un groupe par Feature
- D) Autre : (Spécifiez)

## ADR 0250, persistance (#443)

**238. Qui déclenche `persist()`?**
- A) Une Feature sur un geste de l'usager, et le bouton de Paramètres ▸ Stockage ; jamais l'OS seul ✨(Recommandé)
- B) L'OS aussi, au premier geste de chaque session (invite Firefox chez tout le monde)
- C) Autre : (Spécifiez)

**239. Affichage du stockage.**
- A) Une page « Stockage » dans Système, à côté de « Mémoire » ✨(Recommandé)
- B) Une section de la page « Mémoire »
- C) Autre : (Spécifiez)

## ADR 0251, route du shell et binfmt (#444)

**240. Qui applique « en-tête d'abord »?**
- A) Le shell reconnaît seulement le `Magic` déclaré par la Feature et lance son handler ; la règle fine (PE, MZ pur, console ou graphique, délégation à BoxedWine) est dans le handler d'Os-x86 ✨(Recommandé)
- B) Le shell applique toute la règle à partir d'une table déclarée
- C) Autre : (Spécifiez)

**241. App associée à instance unique déjà ouverte, lancée par `./x.exe`.**
- A) Le shell lance une instance séparée (184 A) ✨(Recommandé)
- B) Refus « déjà ouverte »
- C) Autre : (Spécifiez)

**242. Sans aucun gestionnaire pour `.exe` (BoxedWine absent) en V1.**
- A) Message « Aucune application n'ouvre les fichiers .exe » dès que le catalogue liste un format (lot 2) ; avant, « cannot execute binary file » ✨(Recommandé)
- B) Toujours « cannot execute binary file »
- C) Autre : (Spécifiez)

## ADR 0252, le Terminal affiche un programme invité (#445)

**243. Palette des attributs par cellule.**
- A) 16 couleurs ANSI, gras, souligné, inverse ; 256 couleurs en V2 ✨(Recommandé)
- B) 256 couleurs dès maintenant
- C) Autre : (Spécifiez)

**244. Tab comme touche nommée.**
- A) Ajouter `TerminalKey.Tab`, livrer les deux formes une version, puis `Tab` seul ✨(Recommandé)
- B) Garder le caractère tabulation seulement
- C) Autre : (Spécifiez)

**245. Changement de rédaction par rapport au plan (point 10 de la section 6).** L'ADR 0252 telle qu'écrite dit que le Terminal ne référence pas `Os-x86.Interface` ; c'est la commande `x86`, lancée comme handler binfmt (0251), qui utilise le contrat plein écran étendu. Cela garde 162 A (le Terminal appelle la détection et exécute lui-même un programme console) sans dépendance du Terminal vers Os-x86.
- A) Accepter, et corriger le plan ✨(Recommandé)
- B) Garder le plan : le Terminal dépend de `Os-x86.Interface` (null si absent), l'ADR 0252 est à réécrire
- C) Autre : (Spécifiez)

## ADR 0253, expéditeur marqué (#446)

**246. Expéditeur d'un raccourci `.desktop` et d'une action de notification.**
- A) `Desktop` avec l'app qui tenait la vue ; `Notification` avec l'app qui a posté ✨(Recommandé)
- B) `Desktop` et `Notification` sans identifiant d'app
- C) Autre : (Spécifiez)

## ADR 0254, événements par usager (#447)

**247. Emplacement du journal d'un usager.**
- A) `/var/log/users/<guid>/events.log`, 0600, protégé par l'audit de `/var` ✨(Recommandé)
- B) Dans le dossier personnel (synchronisé, mais effaçable par toute app)
- C) Autre : (Spécifiez)

**248. Capacité `events.write`.**
- A) Normale ✨(Recommandé)
- B) Pas de capacité du tout
- C) Autre : (Spécifiez)

## ADR 0255, relais (#448)

**249. Limites avec des identités (bouteilles).**
- A) Plafond de l'appareil 4 flux ; chaque identité 2 flux et 300 s par défaut ; réglables par l'admin du serveur ✨(Recommandé)
- B) Plafond de l'appareil à 8 quand des identités sont utilisées
- C) Pas de limite par identité
- D) Autre : (Spécifiez)

**250. Où vit la durée « Session » d'une décision?**
- A) Au relais, avec une date limite envoyée par la Feature (24 h de protection) ✨(Recommandé)
- B) Côté page seulement
- C) Autre : (Spécifiez)

---

## Réponses

**Premier lot, le 2026-11-11** (trois numéros ; les 26 autres restent ouverts tant qu'ils ne sont pas validés par numéro ou en bloc) :

| # | Réponse | Lecture |
|---|---|---|
| 225 | **A**, avec une précision : « aucune restriction n'oblige de la fournir si elle est utilisée dans une app par référence de projet, DLL ou NuGet (si elle est incluse dans le paquet de l'application) » | Le marquage « partagée » est posé par l'auteur de la bibliothèque (propriété MSBuild du paquet) et n'impose rien de plus à l'app qui l'utilise : elle la référence comme elle veut (projet, DLL, NuGet) et l'embarque dans son paquet comme toute bibliothèque de `lib/`, sans déclaration ni restriction supplémentaire. Le marquage sert seulement à Kazo pour charger une seule copie par majeure. |
| 233 | **B** | Un administrateur porte tous les rôles de Feature implicitement ; l'attribution explicite ne concerne que les usagers ordinaires. ADR 0247 à corriger. |
| 245 | **B** : « pour pouvoir héberger éventuellement les sessions terminal. Il deviendra la seule application système à utiliser la Feature. Il l'utilise pareil que BoxedWine. » | Le plan reste tel quel (section 6, point 10) : le Terminal dépend de `Os-x86.Interface` (null si absent) et appelle Os-x86 comme BoxedWine le fait, par `Ix86` ; il est la seule app système de Kazo qui utilise la Feature. L'ADR 0252 est à réécrire : le contrat plein écran étendu sert au Terminal lui-même (et à tout handler), pas seulement à la commande `x86` lancée par binfmt. |

**Appliqué dans Kazo-ca/Os le 2026-11-11** (cinq commits sur les branches de chantier, rien de fusionné par moi ; [#435](https://github.com/Kazo-ca/Os/pull/435) a été fusionnée par le mainteneur) : 0244 ([#437](https://github.com/Kazo-ca/Os/pull/437), `185b6942`), 0247 ([#440](https://github.com/Kazo-ca/Os/pull/440), `3e1aba1d`), 0252 ([#445](https://github.com/Kazo-ca/Os/pull/445), `25b31936`, nouveau §1 : le Terminal résout `Ix86` par le registre, lit lui-même l'en-tête, exécute le programme console dans son onglet, peut héberger des onglets « session Os-x86 »), et pour la cohérence 0245 ([#438](https://github.com/Kazo-ca/Os/pull/438), `f77a300c` : toute app peut consommer une interface de Feature, seule la fourniture est réservée aux Features) et 0251 ([#444](https://github.com/Kazo-ca/Os/pull/444), `a79a2fb3` : la route du shell demande d'abord à l'hôte du shell, puis au handler binfmt).

**Deuxième lot, le 2026-11-11** (tout le reste). Réponse reçue : « 240. Os-x86 fournit une méthode pour déterminer le type d'application. C'est lui qui interprète le magic et l'extension. Ensuite le shell demande aux applications qui exposent des capacités d'exécution d'application si elles peuvent ouvrir l'application. Je crois qu'on devrait ajouter le support de MimeType (optionnel) et s'en servir aussi plus tard pour les protocoles (comme pour irc). Pour le reste je suis d'accord avec tes recommandations. »

| # | Réponse | Lecture |
|---|---|---|
| 240 | **D** (ci-dessus) | Remplace le mécanisme de l'ADR 0251 : plus de `Magic` lu par le shell ni de `FileHandler.Executable`. (1) **Os-x86 détecte** : une méthode d'Os-x86 lit l'en-tête et l'extension et rend le type du programme, en type MIME ; (2) le shell demande aux apps qui déclarent une **capacité d'exécution** si elles peuvent ouvrir ce programme et lance celle qui accepte ; (3) les types MIME sont facultatifs dans les déclarations et serviront aux protocoles (`irc://` ; Kazo a déjà `x-scheme-handler/<schème>`, ADR 0096 et 0121). L'hôte du shell (251 A) reste consulté en premier. Quatre précisions à régler : **252 à 255** ci-dessous. ADR 0251 à réécrire ; 0243 §5 (binfmt réservé aux Features) et 0252 à ajuster. |
| 222 | **A** | `ca.kazo.x86` et `ca.kazo.boxedwine` ; remplace 172 A ; identifiants du squelette changés dans Os-x86 (branche `j0/1b-ids`). |
| 251 | **A** | `KazoShellOptions.ProgramHost` : l'hôte du shell (le Terminal) est consulté avant la détection et les apps à capacité d'exécution. |
| 223, 224, 226 à 232, 234 à 239, 241 à 244, 246 à 250 | **A** | La recommandation de chaque question, déjà appliquée dans le texte des ADR. |

## Question nouvelle issue de la réécriture de 0252

**251. Comment le Terminal prend-il le fichier avant le handler binfmt, sans que le shell connaisse une app?** Faits vérifiés dans Kazo : le shell connaît déjà son app hôte (`ShellSession.cs:90`), ses options de démarrage existent (`IKazoShell.cs:114-137`), le Terminal est une app système (`Kazo.Os.Ecosystem.csproj:53`).
- A) `KazoShellOptions.ProgramHost` : une interface `IKazoShellProgramHost` (`TryRunAsync`) que le Terminal fournit au démarrage du shell ; la route 2 de 0251 demande d'abord à l'hôte, puis au handler binfmt ; hors du Terminal (`./x.exe` depuis un autre hôte de shell), le handler binfmt reste la route ✨(Recommandé)
- B) Le shell cherche l'app Terminal par son identifiant et l'appelle directement (le shell connaît une app)
- C) Aucun point d'accroche : le handler binfmt (`x86`) reçoit toujours le fichier et, lancé depuis le Terminal, affiche dans l'onglet (rédaction précédente de 0252, écartée par 245 B)
- D) Autre : (Spécifiez)

Répondue **A** dans le deuxième lot.

## Précisions sur 240 D (252 à 255), posées le 2026-11-11

Faits vérifiés dans Kazo avant de les poser : un gestionnaire de fichiers se déclare déjà avec un type MIME facultatif, des extensions, des actions et une priorité (`FileHandlerManifest(MediaType, Extensions, Actions, Priority)`, `AppManifest.cs:224-228`) ; un schème d'URI se prend déjà par un gestionnaire `x-scheme-handler/<schème>` (ADR 0096 et 0121 ; `xdg-open irc://…` cherche le gestionnaire du schème `irc`) ; `xdg-mime query filetype` existe ; le résolveur d'associations compare l'extension ou le type MIME donné par l'appelant, sans jamais lire le contenu (`FileAssociationResolver.cs:386-393`). « Ajouter le support de MimeType » revient donc à **s'en servir dans la route du shell** (détection par le contenu, puis gestionnaires par type MIME), pas à l'inventer.

**252. Comment le shell atteint-il la détection d'Os-x86 sans connaître l'app?**
- A) Une déclaration réservée aux Features, `FeatureDefinition.FormatDetectors` (remplace `BinaryFormats` de 0251) : la Feature nomme les types MIME qu'elle sait reconnaître et un point d'entrée `DetectAsync(en-tête de 64 octets, extension)` qui rend le type MIME et le détail (PE console, PE graphique, NE, MZ pur, ELF, lot DOS) ou « pas à moi » ; le shell interroge les détecteurs installés et activés dans l'ordre d'installation et s'arrête au premier qui répond ; sans détecteur, la table extension → type MIME de Kazo (`xdg-mime query filetype`) ✨(Recommandé)
- B) Le shell résout `Ix86` par le registre (ADR 0245) et appelle sa détection : le shell connaît Os-x86
- C) Pas de détecteur séparé : chaque app candidate lit elle-même l'en-tête quand le shell lui demande si elle peut ouvrir le fichier
- D) Autre : (Spécifiez)

**253. Quelle forme prend la « capacité d'exécution » d'une app?**
- A) Une action `execute` des `FileHandlers` existants (type MIME et extensions), plus un appel `CanExecuteAsync(descripteur du programme)` sur l'app pour confirmer ou refuser (PE 64 bits, programme graphique quand l'hôte est le Terminal) ; si Kazo ne peut pas appeler une app installée sans la lancer, le refus se fait au lancement (code 126, candidat suivant) ; remplace `FileHandler.Executable` et `BinaryFormats` de 0251 ; BoxedWine déclare `execute` pour PE et NE, la commande `x86` d'Os-x86 pour MZ pur et ELF ✨(Recommandé)
- B) Déclaratif seulement : la liste des types MIME suffit, aucun appel ; une app qui ne peut pas rend 126 après son lancement
- C) Une déclaration à part dans le manifeste (`programLaunchers`), séparée des associations de fichiers
- D) Autre : (Spécifiez)

**254. Plusieurs apps répondent oui?**
- A) L'association par défaut de l'usager pour ce type MIME et l'action `execute` (ADR 0121 : Paramètres ▸ Associations, `xdg-mime default`), sinon le candidat unique, sinon la fenêtre « Ouvrir avec » de l'OS (jamais une question au terminal, D61.5) ✨(Recommandé)
- B) La `Priority` de la déclaration, la plus haute gagne, sans choix de l'usager
- C) La fenêtre « Ouvrir avec » à chaque lancement
- D) Autre : (Spécifiez)

**255. Types MIME des programmes et protocoles.**
- A) L'ADR fixe les types MIME que le détecteur rend, pris de la base freedesktop ou de l'IANA (`application/vnd.microsoft.portable-executable` pour PE, `application/x-dosexec` pour MZ pur, `application/x-executable` pour ELF, `application/x-bat` pour `.bat` et `.cmd`, `application/x-msi`) ; les protocoles passent par ce qui existe (`x-scheme-handler/<schème>` dans `FileHandlers`, `xdg-open`), rien de plus pour IRC ; un programme invité qui ouvre une URL (`open-url` de l'agent, 205 A) passe par `xdg-open` ✨(Recommandé)
- B) Un champ `protocols` à part dans le manifeste, en plus des `FileHandlers`
- C) Autre : (Spécifiez)

**Répondues le 2026-11-11 : A partout** (« Je suis d'accord avec tes recommandations »). Mécanisme complet de la route `./chemin` : (1) le fichier existe ; (2) l'hôte du shell (`KazoShellOptions.ProgramHost`, 251 A) ; (3) détecteurs de format déclarés par les Features (`FormatDetectors`, 252 A) → type MIME, sinon la table extension → MIME de Kazo ; (4) apps qui déclarent l'action `execute` pour ce type (253 A), confirmées par `CanExecuteAsync` quand Kazo peut appeler l'app sans la lancer, sinon refus au lancement ; plusieurs candidats → association par défaut de l'usager, candidat unique, ou fenêtre « Ouvrir avec » (254 A) ; (5) sinon, comme aujourd'hui (bit d'exécution, texte, `#!`, script). Types MIME de freedesktop ou de l'IANA, protocoles par `x-scheme-handler/<schème>` (255 A). Transmis à la session ADR pour la réécriture de 0251 et l'ajustement de 0243 §5 et 0252.

**Appliqué et fusionné le 2026-11-11** : les 13 ADR sont dans `feature/base` de Kazo-ca/Os ([#436](https://github.com/Kazo-ca/Os/pull/436) à [#448](https://github.com/Kazo-ca/Os/pull/448), fusionnées par moi sur ton « Oui, fusionne »). L'ADR 0251 est réécrite sur 240 D et 252 à 255 A, renommée `0251-shell-route-to-execute-capable-apps.md` (« route du shell vers les apps à capacité d'exécution ») ; 0243 §5 parle de détecteurs de format, 0252 suit la nouvelle route. Deux écarts par rapport au libellé de 253 A et 252 A, dictés par le code de Kazo : pas de `CanExecuteAsync` (Kazo ne peut pas appeler une app installée sans la lancer ; une app candidate refuse au lancement, `AppLaunchResult.Refused`, et le shell passe au candidat suivant, sinon 126) ; pas de table extension → type MIME dans Kazo (`xdg-mime query filetype` rend le `MediaType` du premier `FileHandler` qui déclare l'extension, sinon `application/octet-stream`), donc sans détecteur, c'est ce `MediaType` qui sert.
