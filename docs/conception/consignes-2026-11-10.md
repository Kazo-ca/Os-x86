# Consignes à Claude pour construire le plan Os-x86

Je veux que tu reprennes la conception d’Os-x86 et de BoxedWine à partir du journal existant et de mes réponses récentes ci-dessous. Le travail demandé maintenant est de réconcilier les décisions, examiner les solutions et construire un plan complet. Ne commence pas à coder en supposant que j’ai accepté tes recommandations.

Ce texte complète et corrige le journal. Les décisions récentes explicitement décrites ici priment sur les formulations anciennes qu’elles remplacent. Les autres décisions déjà acceptées restent valables. Une préférence conditionnelle n’est pas une décision technique définitive, et une demande d’étude ne t’autorise pas à choisir seul.

## 1. Sources et méthode de travail

Travaille à partir du dépôt `Kazo-ca/Os-x86`, branche `conception-os-x86`, notamment :

- `docs/conception/reprise-chatgpt.md` ;
- `docs/conception/journal-decisions.md` ;
- `docs/conception/serie-5-envoyee.md` ;
- `docs/conception/serie-6-envoyee.md` ;
- `docs/recherche-2026-11-08/decisions.md` et les recherches du 9 novembre liées par le journal.

Dans ce texte, D1 à D64 désignent les décisions du journal, C1 à C45 les sujets de la carte initiale et 65 à 76 les questions de la série 6. Les réponses récentes proviennent de nos discussions du 10 novembre 2026, y compris la dernière réconciliation.

### Validation de tes recommandations

- Présente clairement ce qui est décidé, ce qui reste conditionnel, ce qui demande une étude et ce qui exige encore ma réponse.
- Quand un choix n’est pas tranché, compare les options, explique les conséquences et fais une recommandation. Attends ma validation avant de considérer ce choix comme retenu.
- La règle de nos séries reste valable : après ma réponse à un ensemble identifiable de questions numérotées, les recommandations des questions que je n’ai pas mentionnées sont acceptées, sauf exception explicite. Par exemple, si je réponds seulement à la question 4 d’une série de cinq, les recommandations 1, 2, 3 et 5 sont acceptées après cette réponse.
- Cette règle ne vaut jamais avant ma réponse. N’anticipe pas une acceptation et ne commence pas l’implémentation sur cette base.
- L’exception explicite de la série 6 reste importante : je n’avais pas accepté les recommandations après 64. Les échanges ultérieurs ont répondu à plusieurs sujets, mais n’ont pas validé rétroactivement toutes les sous-options de 65 à 76.
- Une réponse orale à une reformulation simplifiée vaut pour ce qui m’a réellement été présenté. Ne lui ajoute pas des limites, mécanismes ou sous-options restés dans le document sans avoir été discutés.
- Si une question bloque un volet, pose-la et attends pour ce volet. Tu peux poursuivre les travaux indépendants déjà autorisés. Pour le travail demandé maintenant, poursuis l’analyse et la préparation du plan, pas le code produit.
- Regroupe les questions qui portent sur le même problème. Donne un court contexte et une question claire à la fois. Ne me fais pas revalider les mêmes principes sous plusieurs numéros.
- Conserve les anciens numéros pour la traçabilité. Si tu proposes une nouvelle série, évite de réutiliser un ancien numéro avec une autre signification.

### Ce que tu dois vérifier

Les constats techniques du journal sont des recherches datées, souvent liées au commit Kazo `cd89ee0`. Vérifie leur actualité avant de les transformer en contraintes définitives. Distingue les faits lus dans le code, les résultats réellement mesurés, les déductions et les hypothèses.

Le fait que j’aie accepté un objectif ne prouve pas que le mécanisme envisagé fonctionne. Quand je demande des essais comparatifs, prévois de vrais essais exécutés et mesurés dans la phase autorisée, pas seulement une comparaison théorique. La préparation actuelle du plan ne doit pas se transformer en démarrage du code de production.

## 2. Vision et séparation des responsabilités

### Décisions confirmées

Os-x86 est une fonctionnalité optionnelle installable de Kazo OS, exposée comme une API et utilisable en ligne de commande. Elle permet l’exécution locale de systèmes et programmes invités dans le navigateur, sans serveur distant d’exécution.

Il faut distinguer cette fonctionnalité externe des applications système intégrées de Kazo. Être installée au niveau système ne lui donne pas automatiquement une réputation ou des droits supérieurs à ceux des autres extensions externes.

Le bac à sable serveur reste un autre projet, avec un autre but : ne pas transmettre l’exécutable au client. Il ne constitue pas un repli automatique pour faire fonctionner Windows moderne dans Os-x86.

La vision conserve les deux niveaux : une VM complète pour un système invité et l’exécution intégrée de programmes, notamment Windows via Wine dans une VM Linux. Nous ne reconstruisons pas un processeur en C# à partir de MsDos.Clone. Nous reprenons de MsDos.Clone son organisation et sa méthode : cœur séparé des adaptateurs, tests, étapes de développement et hôte de démonstration autonome.

### Os-x86

- Exécute les VM et les programmes invités, expose les contrats et conserve les configurations système nécessaires.
- Fournit les capacités de contrôle des VM : cycle de vie, disques, périphériques, points de restauration et autres opérations prévues par l’API.
- Applique les permissions et les protections, indépendamment de l’interface qui appelle l’API.
- Offre une vérification centralisée du type de programme aux applications clientes.
- Expose la commande `x86` pour les fonctions moteur et d’administration autorisées.
- Ne possède pas une application générale de gestion « Sous-systèmes » séparée.

### BoxedWine

- Reste une application ordinaire qui utilise la Feature Os-x86. Ce n’est pas une Feature et ce n’est pas l’émulateur GPL appelé Boxedwine.
- Reçoit les demandes de lancement Windows via les associations de fichiers et pilote l’environnement en utilisant Os-x86.
- Porte l’interface de gestion, les bibliothèques, les fichiers récents, les profils et configurations utilisateur.
- Son rôle n’est plus limité aux programmes Windows : il doit aussi lancer, gérer et configurer les environnements Linux et leurs applications, y compris graphiques. La direction retenue est que BoxedWine porte l’interface utilisateur, Os-x86 l’exécution.
- Conserve les configurations utilisateur partageables par profil. Os-x86 conserve les configurations système.

### Exception de sécurité confirmée

L’extension du rôle graphique de BoxedWine ne supprime pas l’exception de sécurité de D49. Os-x86 doit conserver ses protections internes, ses contrôles et les confirmations nécessaires, que l’appel provienne de BoxedWine, du terminal ou d’une autre application.

J’ai donné l’exemple d’une commande destructive ou d’une opération qui exige une élévation : l’interface ne suffit pas à protéger l’utilisateur. La bibliothèque sous-jacente doit refuser une opération non autorisée ou obtenir la confirmation appropriée. Un appelant ne doit pas pouvoir s’autoriser lui-même en contournant cette couche.

Précise comment les confirmations se présentent selon le point d’entrée. Ne remplace pas les fenêtres de confiance par une question libre que le programme invité pourrait imiter dans le terminal. La règle déjà acceptée « aucun consentement tapé dans le terminal » et l’absence d’opération API permettant à une application de s’accorder elle-même une permission restent à préserver.

### Répartition détaillée à proposer

Prépare une série ciblée pour répartir précisément : réglages système, réglages de VM, profils utilisateur, partage des profils, opérations administrateur et permissions correspondantes. Le principe général est décidé ; l’inventaire exact ne l’est pas.

## 3. Feature et intégration dans Kazo OS

Les décisions structurelles D7, D10, D12, D15, D30 à D39 restent le socle, sous réserve des changements explicites de rôle ci-dessus.

- Os-x86 est une Feature installée au niveau système par un administrateur.
- Une Feature peut fournir une interface, déclarer des permissions de fournisseur, enregistrer un binfmt et dépendre d’une autre Feature.
- Elle possède une seule identité pour l’installation, la mise à jour, le retrait, le Store et la présentation des permissions. Chaque point d’entrée possède son identifiant enfant, son type et son processus visible séparément.
- L’ensemble des permissions est déclaré par la Feature, puis chaque point d’entrée déclare son sous-ensemble, contrôlé selon son type.
- Prévoir la section « Fonctionnalités » dans les Paramètres. Le consentement administrateur à l’installation couvre les permissions dangereuses déclarées par la Feature, sans abolir les contrôles propres à chaque utilisateur et opération.
- Le NuGet de contrats se nomme `Os-x86.Interface`. Il est distinct de l’implémentation, inclus dans les applications clientes qui en ont besoin. Référencer la fonctionnalité signifie aussi une dépendance de manifeste, facultative ou obligatoire, vers Os-x86.
- La résolution doit permettre `GetServiceAsync<T>()`, retourner null si aucun fournisseur utilisable n’est installé et ne jamais conserver ce null en cache. `IsAvailable<T>()` est synchrone ; un état détaillé explique l’indisponibilité ; une permission manquante n’est pas une absence silencieuse du fournisseur.
- Une dépendance facultative hors plage doit se comporter comme absente plutôt que faire disparaître silencieusement l’application cliente.
- Une seule copie compatible des contrats est chargée par page, choisie au premier besoin. Une version mineure ajoute des membres. Une ancienne copie déjà chargée peut nécessiter un redémarrage pour utiliser la Feature nouvellement installée.
- L’installation doit rendre la fonctionnalité utilisable immédiatement dans les cas compatibles. Ne promettre un redémarrage systématique ni son absence systématique : il dépend notamment du code déjà chargé.
- Les identifiants d’application, de points d’entrée et les espaces de noms exacts restent à fixer avant publication. Ne pas employer le nom avec tiret comme espace de noms C#.
- Le socle initial retient un fournisseur utilisable par contrat ; les extensions pour plusieurs moteurs sont à réexaminer au choix du moteur x64. Prévoir la réservation du nom NuGet avant publication.

### Installation et dépendances

Installer BoxedWine doit proposer d’installer Os-x86 si nécessaire, avec l’accord d’un administrateur. En cas de refus, l’installation de BoxedWine s’arrête avec un message clair, au lieu de réussir puis de rendre l’application introuvable.

Le Store et kazopkg doivent résoudre les dépendances, avec une expérience de consentement cohérente. Une application peut demander l’installation d’une dépendance facultative ou déclarer une dépendance obligatoire. Le Store doit pouvoir s’ouvrir sur le paquet concerné.

Retirer Os-x86 avertit de l’impact et désactive les applications qui en dépendent. La désinstallation de BoxedWine ou d’Os-x86 conserve les données, sauf opération distincte de suppression explicitement choisie.

Les permissions historiques « lancer des programmes » et « gérer les sous-systèmes », ainsi que celle introduite pour configurer les VM Windows, doivent être réconciliées avec le rôle désormais plus large de BoxedWine. Ne les étends pas silencieusement à toutes les opérations.

L’accord administrateur commun à l’installation de Kazo pour certaines capacités de BoxedWine a été accepté sous condition : il ne doit jamais donner à un utilisateur standard un moyen de contourner ses droits ou de modifier des paramètres réservés à l’administrateur. Vérifie les contrôles à chaque opération sensible. La liste de ces opérations doit faire partie de ta série ciblée.

## 4. Moteurs et périmètre de livraison

### Base retenue

- Partir de v86 officiel pour le 32-bit, dans un Web Worker. Le C# assure l’intégration, pas la boucle d’émulation sur le thread de l’interface.
- Garder une abstraction qui permette un moteur x64 ultérieur. Le choix de ce moteur attend un banc d’essai.
- Ne pas annoncer dès maintenant un fork de v86 comme choix définitif. Identifie d’abord les écarts réellement nécessaires.
- Wine tourne dans le Linux invité d’Os-x86. Nous n’utilisons pas le moteur du projet Boxedwine.
- FreeDOS est la voie prévue pour DOS ; Wine ne doit pas être supposé couvrir les programmes DOS purs.

### Résultat attendu à la fin de V1

La fin de V1 doit comprendre :

1. Os-x86 comme fonctionnalité optionnelle avec son API et l’application de gestion/lancement.
2. Linux utilisable en ligne de commande et pour des applications graphiques.
3. FreeDOS.
4. Des applications Windows graphiques via Wine.

L’ordre de développement peut rester progressif. En revanche, l’ancien repli de D25 « VM et DOS seulement si Wine échoue » ne suffit plus pour déclarer V1 terminée. Si les mesures montrent que l’objectif Windows graphique est irréaliste dans les conditions retenues, expose le résultat et propose un changement à faire valider.

### Éléments conditionnels ou ultérieurs

- Un vrai Windows invité, avec média et licence fournis par l’utilisateur, est souhaité en V1 si cela n’impose pas de changements significatifs. Sinon, report en V2 accepté. Cette condition est distincte de l’exigence Wine graphique en fin de V1.
- Le x64 suit les paliers déjà décidés : Linux x86-64 après comparaison des moteurs, puis Windows x64 expérimental selon les résultats. Les programmes Windows 64-bit via Wine dans Linux x64 sont distincts d’un système Windows x64 complet.
- Windows 7, 10 et 11 x64 restent une recherche progressive, sans date promise ni obligation de bloquer le 32-bit.
- L’intégration avancée du terminal et les adaptateurs console sont orientés V2. Ne réintroduis pas tous les mécanismes de D27 comme exigences V1 déjà acceptées.
- Le retour du véritable code de sortie est souhaité en V1 si possible et obligatoire en V2.
- La veille/reprise d’une VM comme fonction utilisateur reste une possibilité V2, surtout pour accélérer l’ouverture. Ce report ne supprime ni les points de restauration disque ni les instantanés utiles à une sauvegarde cohérente.
- Quotas administrateur de stockage et avertissements associés sont reportés en V2.
- Presse-papiers riche et images sont reportés en V2 ; texte simple en V1.
- Exploration du disque interne de la VM depuis Kazo : fortement souhaitée en V1, report V2 accepté si trop complexe.
- Import d’une bouteille exportée dans un autre environnement : souhaité, non prioritaire, report V2 accepté si cela demande de gros changements. Cela ne reporte pas automatiquement l’export individuel déjà demandé.
- Console avancée dans une bouteille et héritage des permissions : acceptés, avec report V2 possible si trop complexe ; voir la section sécurité pour les garanties V1 à proposer.

## 5. Lancement des fichiers et terminal

### Route Windows confirmée

Un programme Windows lancé depuis le terminal Kazo, par exemple `./setup.exe`, passe par l’association de fichier du shell vers BoxedWine. Cela confirme D62 B. Le double-clic et les demandes habituelles d’ouverture passent également par l’application associée.

Os-x86 n’enregistre pas d’associations d’extensions. Ne réintroduis pas la proposition rejetée selon laquelle le terminal enverrait directement tous les programmes Windows à Os-x86 sans BoxedWine.

L’acceptation d’une console avancée dans une bouteille existante ne change pas automatiquement cette route normale de lancement.

### Détection et autres formats

- La reconnaissance DOS/Windows, console/graphique est proposée comme service central d’Os-x86 aux clients qui en ont besoin.
- L’extension `.com` sert à identifier les programmes DOS, et le double-clic sur un `.com` passe par BoxedWine, qui confie l’exécution à Os-x86.
- En cas de contradiction entre contenu et extension, ma préférence est le contenu détecté, sous réserve de vérifier les contre-indications et les exceptions. Présente une recommandation avant de figer toute la règle.
- La distinction historique MZ, PE, NE, ELF et DOS doit être vérifiée et articulée avec les associations du shell. Ne confonds pas « identifier le format » et « choisir l’application qui reçoit l’ouverture ».
- Les détails `.bat`, `.cmd`, `.msi`, fichiers sans extension, scripts et erreurs de format doivent être spécifiés sans considérer toutes les anciennes sous-options comme acceptées.
- La voie ELF vers Linux et la voie DOS au terminal doivent respecter ce cadrage. Pour DOS, une exécution par Os-x86 rendue directement dans le terminal est souhaitée si sa complexité reste raisonnable, en lien avec l’étude des adaptateurs.
- Un `.exe` ne doit pas échouer uniquement parce qu’il n’a pas le bit d’exécution Unix. Le traitement du shell doit éviter de lire un binaire comme un script et de traiter un `.bat` comme un script hsh.

### Documents ordinaires depuis le terminal

Pour un document lancé depuis le terminal Kazo, utiliser l’association d’extension déjà fournie par les applications ou choisie par l’utilisateur, sans demander systématiquement de choisir une application.

À l’intérieur d’une VM, l’ouverture suit l’environnement invité. Depuis une application Kazo, l’utilisateur s’attend à une fenêtre Kazo. Précise le cas où aucune application n’est associée et les cas ambigus, sans remplacer cette décision par un refus général d’ouvrir les documents.

Tout changement du gestionnaire par défaut doit rester soumis à confirmation de l’utilisateur selon D61.10 ; suivre une association déjà choisie n’est pas la modifier.

### Expérience console

- En V1, une console dans une fenêtre séparée est acceptable. Fermer sa fenêtre arrête le programme concerné.
- Une fenêtre séparée reste acceptable même lorsque les adaptateurs futurs seront disponibles. Le terminal courant n’a pas été imposé comme résultat obligatoire à terme.
- Le véritable code de sortie est souhaité en V1 si faisable. Sinon, un retour à l’invite suffit pour V1. En V2, le véritable code d’exécution doit être disponible.
- Ne confonds pas un code de fermeture de fenêtre avec le résultat du programme invité. Si le pont actuel ne donne que 0, 1 ou 130, nomme clairement cette limite.
- Précise les flux d’entrée/sortie, Ctrl+C, la fermeture du terminal, les processus enfants et le comportement d’une instance BoxedWine déjà ouverte. Les détails de mise en œuvre ne sont pas tous décidés.
- Aucun consentement de sécurité ne se saisit dans la console du programme.

### Linux et noms des commandes

Pouvoir démarrer/arrêter une VM et ouvrir une session de commandes Linux depuis le terminal Kazo est souhaité. L’expérience de `docker exec` peut servir d’inspiration, tout en gardant la distinction entre conteneur et VM.

Les noms retenus sont `x86` et `boxedwine`, sans alias `wine` côté Kazo. La syntaxe complète, les sous-commandes, le nombre de sessions et les voies de repli sans agent restent à proposer. `boxedwine` peut correspondre au nom court d’une application fenêtrée plutôt qu’à une vraie commande : vérifie les conflits de résolution du shell au lieu de supposer qu’il bénéficie de la priorité des commandes de Feature.

Quand un gestionnaire manque, le terminal et les Fichiers doivent proposer une installation via les formats déclarés dans le Store, sans nom de produit codé en dur dans Kazo.

## 6. Bouteilles et permissions

### Identité du programme

Le même programme doit retrouver la même bouteille lorsque l’environnement est compatible, indépendamment du lanceur Kazo. L’application qui demande l’exécution ne choisit pas la bouteille, ne gère pas son contenu et n’obtient pas un droit de navigation dans les autres bouteilles.

L’origine du lancement ne doit pas, à elle seule, changer la bouteille ni les permissions choisies pour ce programme. D50 doit donc être corrigée lorsqu’elle fait de l’app créatrice un critère imposant une autre bouteille.

La sélection et la réutilisation relèvent de l’environnement d’exécution. Propose la répartition précise entre Os-x86 et la logique Wine de BoxedWine, en préservant cette séparation vis-à-vis de l’application appelante.

L’empreinte, le chemin, les mises à jour, correctifs, greffons, installateurs et critères de compatibilité doivent être traités. Ne transforme pas « même programme » en simple égalité de nom de fichier. Les avertissements quand on rejoint un environnement déjà autorisé doivent être cohérents avec les permissions qu’il transmet.

Le journal retient une identité mémorisée par utilisateur, sous-système et bouteille pour Windows ; pour Linux, utilisateur, sous-système, chemin et empreinte. Un changement d’empreinte doit déclencher le réexamen prévu des autorisations. Réconcilie ce comportement avec les mises à jour et les enfants, au lieu de réutiliser silencieusement une autorisation devenue inadaptée. La fenêtre présente l’origine identifiable du programme, notamment son chemin et son empreinte.

### Programmes enfants et console avancée

Une application doit pouvoir en lancer une autre dans le même système invité sans démarrer obligatoirement une nouvelle VM. Nous n’avons pas imposé pour cela un mécanisme précis de binfmt Linux.

J’accepte une console avancée dans la bouteille existante. Les applications lancées depuis une application doivent hériter des permissions accordées à cette application de départ, y compris dans ce scénario de console.

Si la console avancée et la mise en œuvre de cet héritage sont trop complexes, un report en V2 est possible. Précise la portée exacte de ce report et les garanties réellement applicables en V1, puis fais-moi valider les limites. Cette réserve ne reporte pas automatiquement le principe général permettant à une application d’en lancer une autre dans la même VM. Un report de fonctionnalité ne signifie pas que des programmes peuvent obtenir des droits sans contrôle.

Il faut distinguer deux cas dans le modèle : une application Kazo demande d’ouvrir un programme déjà identifié ; un programme invité crée lui-même des processus enfants dans son environnement. Réconcilie cette distinction avec D21, D42, D43 et D52, au lieu de généraliser l’une des deux règles à tous les appels.

### Garanties de sécurité déjà retenues

- Os-x86 applique les autorisations et garde ses protections, quelle que soit l’interface.
- L’API n’offre pas une opération permettant à un client de s’accorder une permission. Elle peut exposer l’état autorisé, la révocation et l’ouverture de la page de règles, selon les droits.
- L’administrateur peut imposer un veto aux décisions d’un utilisateur standard.
- Les décisions de permissions sont séparées par utilisateur et conservées dans le stockage réservé à Os-x86, selon D61.1. La bibliothèque de BoxedWine est également séparée par utilisateur. Ne les remets pas dans un simple fichier de configuration modifiable par toute application ayant accès au dossier personnel.
- Cette organisation du stockage ne doit pas être présentée comme une isolation inviolable face à du JavaScript malveillant partageant l’origine de Kazo. La limite identifiée dans le journal doit être vérifiée et expliquée.
- Dans les environnements gérés, l’agent peut avoir les privilèges nécessaires ; les programmes utilisateurs et les commandes de l’API ne deviennent pas root. La règle historique prévoit des arguments structurés plutôt qu’une ligne de shell arbitraire.
- Le sudo d’un Linux personnel reste une option propre à ce sous-système, avec les conséquences sur les garanties à expliquer. Il ne donne pas des droits administrateur sur Kazo.
- Les demandes de permissions n’expirent pas selon un délai d’interface imposé par Os-x86. Cela ne garantit pas qu’une connexion ou un programme invité attendra éternellement.
- La fermeture de session arrête les applications en V1 et refuse les demandes encore en attente. L’exécution sans session attend un travail ultérieur de type « linger » ; ne la promets pas en V1.
- Pour ce futur mode sans session, conserver la règle de refus en l’absence du propriétaire et d’information à sa prochaine connexion, avec accès aux règles pour préparer une autorisation ultérieure. La présence d’un autre utilisateur connecté ne vaut pas présence du propriétaire.
- Les notifications de demande utilisent un identifiant opaque, pas une commande ou des données sensibles dans le lien.

### Permissions réseau et modèle à comparer

Je veux éviter une avalanche de confirmations tout en protégeant Kazo et les autres environnements. Il faut pouvoir autoriser tout l’accès réseau d’une application ou d’une VM, garder des journaux et conserver les limites imposées par l’administrateur.

Les décisions déjà prévues comprennent autoriser/refuser, des durées adaptées, la possibilité de viser un hôte et un port, la mémorisation et la révocation. Le mécanisme doit être compréhensible et ne pas multiplier inutilement les demandes.

La préférence pour une VM par système invité reste conditionnelle à l’identification fiable de l’application qui demande une connexion et à l’application de permissions par application et par port.

Compare explicitement :

1. Plusieurs programmes dans une VM avec attribution et contrôle fiables par application.
2. Des VM séparées, éventuellement regroupées par règles réseau compatibles, lorsque l’isolation est nécessaire.

Évalue faisabilité, possibilités de contournement, mémoire, démarrage et expérience utilisateur. Plusieurs VM actives peuvent être nécessaires dans la seconde option, mais ce n’est pas une solution déjà choisie. Recommande et attends ma validation.

Les limites connues d’une VM partageant affichage, clavier ou presse-papiers doivent être exposées. Ne présente pas une isolation réseau comme une isolation totale de programmes hostiles. Ne supprime pas non plus arbitrairement toutes les protections au motif que j’accepte certains usages partagés.

## 7. Cycle de vie des VM et utilisateurs

### Plusieurs VM et choix d’environnement

Plusieurs VM peuvent fonctionner simultanément si les ressources le permettent. La préférence pour une VM par système invité n’est pas une limite globale « une seule VM » et n’est pas encore une décision de découpage définitive.

L’utilisateur doit voir les VM actives et pouvoir comprendre les choix possibles : arrêter une autre VM, utiliser un environnement compatible ou créer une autre VM. Le moment exact où il faut poser un choix, les critères de compatibilité et les limites de ressources doivent être proposés à partir des essais, sans reprendre automatiquement les sept sous-points de 66 A.

La commande et l’API utilisent les mêmes données et les mêmes VM pour une demande équivalente. Des points d’entrée différents ne créent pas artificiellement deux environnements différents.

### Arrêt et conservation

- Une VM démarrée pour des applications s’arrête dès que toutes ses applications sont fermées, sans délai d’inactivité ajouté.
- Fermer une application ne doit pas arrêter la VM si une autre application l’utilise encore.
- Une VM démarrée manuellement reste active jusqu’à son arrêt manuel. Ne la ferme pas simplement parce qu’aucune application n’y est actuellement visible.
- L’arrêt d’une VM n’est pas la suppression de son disque. Les données et le disque sont conservés par défaut, sauf contre-indication expliquée.
- Conserver ou recréer l’environnement d’exécution reste conditionnel : le garder si la création est lente et la conservation peu coûteuse ; le recréer si la création est rapide et la conservation coûteuse. Mesure les coûts et précise ce qui est conservé dans chaque cas.
- Ne déduis pas de ce choix une autorisation d’effacer les programmes et données à chaque fermeture.
- L’ancienne proposition consistant à « garder manuellement ouverte » une VM démarrée automatiquement n’a pas été retenue comme fonction supplémentaire.

### Un utilisateur à la fois par VM

Le principe maintenant confirmé est qu’une même VM n’est utilisée en exécution que par un utilisateur à la fois. Cela ne signifie pas qu’un utilisateur ne peut avoir qu’une VM, ni que plusieurs VM ne peuvent pas fonctionner.

Les utilisateurs ont des comptes distincts dans l’invité, notamment Windows ou Linux. Leurs dossiers personnels sont reliés à leurs fichiers Kazo et restent séparés.

Le partage d’images disponibles peut être visible sans révéler la liste des applications qu’un autre utilisateur a installées ou exécutées. Bibliothèque, fichiers récents, profils et données personnelles restent propres à chaque utilisateur.

La limitation à un utilisateur actif ne suffit pas à démontrer la confidentialité lors de l’utilisation suivante : des applications peuvent enregistrer des réglages ou données sur le disque invité. Vérifie cette séparation avant de déclarer le problème réglé. Les bouteilles personnelles et l’absence d’installation Windows « pour tous » en V1 ne doivent pas disparaître silencieusement.

Examine aussi les écritures concurrentes : plusieurs onglets, plusieurs instances, réutilisation d’une bouteille et synchronisation entre appareils. Ne considère jamais que deux VM peuvent écrire sans risque dans le même préfixe Wine ou disque actif.

## 8. Images et environnements invités

- Proposer des modèles prêts à utiliser, notamment Linux avec Wine et FreeDOS, et une configuration plus manuelle. Le détail des réglages et limites reste à proposer.
- Permettre à l’utilisateur d’importer une image Linux avec l’autorisation d’un administrateur. Les contrôles de compatibilité et les capacités disponibles sans agent doivent être explicites.
- Les images volumineuses ne doivent pas alourdir le noyau ou le téléchargement de base de Kazo. Leur emplacement de stockage n’est pas la même décision que le serveur qui les distribue.
- Les images peuvent être conservées dans l’espace Kazo ou des emplacements montés appropriés, avec une stratégie de cache local à étudier. Pour de petites images, un téléchargement à la demande peut être raisonnable ; les tailles et temps doivent guider le choix.
- Demander avant le téléchargement initial d’une image nécessaire, avec une possibilité « ne plus demander ». Précise la portée exacte de ce choix et comment le réactiver. Il ne vaut pas consentement aux conflits de synchronisation ou à toutes les mises à jour futures.
- Une image déjà disponible doit permettre le fonctionnement hors ligne lorsque les autres ressources nécessaires sont présentes.
- Vérifier l’intégrité d’une image téléchargée avant de l’utiliser.
- L’image Wine inclut Wine Mono et Wine Gecko, selon D61.6, afin d’éviter des téléchargements et demandes réseau imprévus à la création de chaque bouteille.
- Le journal prévoyait Wine 11.0 épinglé. Vérifie compatibilité et actualité, sans changer silencieusement la version retenue.
- BoxedWine ne s’octroie pas un accès réseau propre pour d’autres usages en V1. Réconcilie cette règle avec le composant responsable de distribuer et télécharger les images.

### Base commune et différences

Je préfère partager une base Linux/Wine lorsque cela apporte un avantage réel sans changements excessifs. Une couche de différences peut éviter de dupliquer une image entière. Une image séparée peut être préférable si sa taille, son chargement ou le coût de reconstruction le justifient.

Compare réellement les temps de chargement, tailles, coûts de création et de mise à jour. La copie-sur-écriture au niveau des fichiers ou des blocs n’est pas tranchée. Ne présente pas l’une comme acquise uniquement parce qu’un ancien scénario de mise à jour la supposait.

### Distribution à préciser

Présente hébergement, catalogue, versions, cache, intégrité, accès hors ligne, CORS, limites de paquets et obligations de sources. Les chiffres et limites relevés dans le journal doivent être revérifiés. Une solution techniquement possible dans un prototype ne suffit pas si le navigateur ne peut pas télécharger les images dans la configuration réelle de Kazo.

## 9. Stockage et synchronisation

Je veux combiner un stockage adapté aux accès des VM, éventuellement avec un cache en mémoire, et un enregistrement dans l’espace Kazo permettant la synchronisation avec le serveur lorsqu’elle est activée.

L’idée d’un disque de base commun et de différences est une piste retenue pour comparaison. La disposition concrète OPFS, fichiers Kazo, caches, blocs ou couches reste à concevoir. Ne confonds pas le stockage des décisions de permissions déjà choisi avec le stockage complet des disques invités, encore à étudier.

Le plan doit couvrir la cohérence d’un disque actif, l’écriture durable, la reprise après incident, les interruptions, le quota navigateur et le risque d’éviction. Le report V2 des quotas administrateur ne retire pas le besoin de signaler un échec de stockage ni de protéger les données.

### Règle définitive pour les conflits

En cas de conflit de synchronisation, toujours demander à l’utilisateur. La version la plus récente ne doit jamais écraser automatiquement l’autre.

Afficher les versions et leurs dates de modification, indiquer laquelle est la plus récente et la proposer. L’utilisateur peut choisir l’ancienne, par exemple si elle contient un travail important et que la nouvelle ne contient que quelques changements secondaires.

Il n’y a pas d’option « appliquer automatiquement à l’avenir » décidée pour ces conflits. La formulation antérieure « le plus récent l’emporte » doit être remplacée par cette décision.

Précise comment traiter les horloges incorrectes, dates égales, appareils hors ligne, modifications simultanées et unités de conflit, notamment si le système synchronise une image ou des différences plutôt que des fichiers séparés. Propose les mécanismes sans retirer le choix de l’utilisateur ni écraser avant sa décision.

## 10. Sauvegardes et mises à jour

### Sauvegardes

- Lors d’une sauvegarde demandée par l’utilisateur, les VM sont incluses et cochées par défaut dans la fenêtre d’exportation. L’utilisateur peut les exclure.
- Pouvoir exporter une seule bouteille, avec son environnement et ses réglages, sans exporter tout le disque VM.
- Pouvoir importer ailleurs une bouteille exportée est souhaité, mais non prioritaire et reportable en V2 si les changements sont importants.
- Une sauvegarde faite pendant qu’une VM fonctionne doit être cohérente. Compare les mécanismes, dont les instantanés, plutôt que copier aveuglément un disque en cours d’écriture.
- Les points de restauration disque durables restent distincts des états de veille jetables et de la future fonction utilisateur veille/reprise.
- L’inclusion de l’image de base dans une sauvegarde n’est pas décidée systématiquement. Elle dépend notamment de sa taille. Compare sauvegarde autonome avec base incluse, base récupérée séparément et choix offert à l’utilisateur, avec le risque de ne plus pouvoir télécharger une ancienne base.

### Mises à jour

Une nouvelle image doit être proposée à l’utilisateur. Il doit pouvoir différer son installation et savoir si un redémarrage sera nécessaire.

Le choix souhaité est « installer maintenant » ou « lorsque l’application sera fermée ». L’utilisateur qui lance une application veut pouvoir l’utiliser tout de suite, sans être obligé d’attendre une mise à jour facultative. Il peut au contraire choisir de redémarrer et mettre à jour avant un usage prolongé.

Ce choix ne décide pas d’une migration à chaud et n’autorise pas à arrêter d’autres applications partageant la VM. Précise comment attendre un état sûr et expliquer les conséquences aux utilisateurs concernés.

En cas d’échec, il doit pouvoir retrouver un état utilisable de la VM et de ses données. Propose un mécanisme de retour arrière à faire valider, avec ses limites. Ne promets pas qu’un simple retour de version d’image préservera nécessairement une bouteille déjà migrée.

Des notes de version expliquant changements, impact sur les fichiers et redémarrage sont préférables, mais pas une exigence bloquante de V1. L’annonce du redémarrage nécessaire reste une exigence distincte.

Réconcilie D56 avec ces réponses : versions minimales de l’interface et de l’image, mise à jour des bouteilles, états incompatibles après changement de moteur, retour vers une version trop ancienne et préservation des données. L’ancien refus de lancer si une nouvelle image est indispensable doit être distingué d’une simple mise à jour disponible. Si une version réellement incompatible rend impossible l’ouverture sans mise à jour, explique le cas et fais valider le comportement au lieu de contredire silencieusement le choix « utiliser maintenant ».

## 11. Fichiers partagés et disque interne

### Accès depuis les invités en V1

Les programmes invités doivent pouvoir accéder :

- au dossier personnel de l’utilisateur dans Kazo ;
- aux dossiers locaux que l’utilisateur a autorisés au navigateur ;
- aux dossiers distants déjà montés dans Kazo.

Conserver les droits de lecture/écriture déjà accordés. Un montage ne doit pas créer un accès plus large. La présentation doit être compréhensible, comme un lecteur réseau ou un dossier partagé selon l’invité. Pour un vrai Windows invité retenu dans le périmètre, viser le même résultat.

Si un dossier monté devient inaccessible, afficher une erreur claire. Ne pas laisser croire qu’il est vide ou que les fichiers ont disparu.

### Accès inverse depuis Kazo

Je souhaite fortement que l’explorateur Kazo puisse ouvrir le disque interne d’une VM en V1, mais j’accepte un report V2 si c’est trop complexe. Il faut proposer la forme, les permissions et le comportement pendant les écritures de la VM.

Ce sujet est différent de l’accès par la VM à un dossier Kazo partagé. Ne les fusionne pas et ne considère pas le second comme preuve que le premier est résolu.

Une proposition supplémentaire de marquer les fichiers ou lecteurs par une icône indiquant leur provenance VM a été laissée de côté. Ne l’ajoute pas comme exigence V1.

Les outils ou un agent invités nécessaires sont acceptables. Leur rôle, leur installation, leurs droits, les invités qu’ils couvrent et les solutions sans agent restent à détailler.

## 12. Processus et fenêtres

### Processus

Les programmes invités doivent être visibles dans le modèle de processus de Kazo. Si les applications A et B tournent dans la même VM, l’utilisateur doit pouvoir cibler et arrêter A sans arrêter B ni toute la VM.

Un agent invité peut remonter ces informations et exécuter les demandes nécessaires. Précise l’identification, les processus enfants, les droits d’arrêt, les pertes de connexion et les capacités selon le système invité. Un « lancement = processus Kazo » doit être réconcilié avec les programmes enfants et les applications multifenêtres.

### Fenêtres

Le résultat attendu est désormais une fenêtre Kazo distincte pour chaque véritable fenêtre Windows, pas seulement un grand bureau invité par programme.

Les changements de titre, d’icône et d’état doivent être reflétés : maximiser, minimiser, restaurer et autres événements pertinents. Le recours aux handles et messages Windows est une piste à vérifier dans Wine, pas une implémentation choisie. Vérifie les API exactes ; ne transforme pas un exemple oral d’API en contrainte technique erronée.

Les applications graphiques Linux doivent également être affichées dans des fenêtres Kazo, y compris lorsqu’elles sont lancées depuis le terminal, avec l’interface portée par BoxedWine.

Prévoir une icône générique adaptée si aucune icône exploitable n’est trouvée. Ne suppose pas que tout programme Windows ou Linux fournit une icône valide.

Une fenêtre secondaire fermée ne doit pas tuer toute l’application. Conserve les relations de fenêtres, dialogues et modalités nécessaires. En revanche, fermer la fenêtre console arrête son programme ; une fermeture forcée ou un plantage du composant propriétaire doit être géré sans tuer les autres applications de la VM.

Une fenêtre devenue hors écran doit être ramenée automatiquement dans la zone visible.

### Échéance à préciser sans rouvrir l’objectif

L’objectif fenêtre par fenêtre est confirmé. La date exacte de cette granularité par rapport à la fin de V1 n’a pas été explicitement tranchée lors de la dernière réconciliation. L’ancien D26 prévoyait une fenêtre Kazo par programme en V1 et un mode intégré plus tard. Signale cet écart et demande seulement le calendrier nécessaire, sans me redemander si je veux des fenêtres distinctes.

## 13. Bibliothèque, icônes et raccourcis

- L’utilisateur ajoute lui-même les applications installées. Pas de détection automatique ni de proposition automatique après installation comme obligation V1.
- Un tutoriel au premier lancement est demandé, pas seulement suggéré. Il explique comment ajouter soi-même les programmes.
- L’entrée au menu Démarrer est obligatoire pour un programme ajouté ; le raccourci de bureau est facultatif.
- BoxedWine ajoute lui-même sa petite marque en bas à droite de l’icône du raccourci ou de l’entrée, comme prévu par D57. Ne remplace pas cela par la recommandation non acceptée de 70 A où Kazo dessinait la marque.
- Kazo doit permettre les icônes adaptées aux fenêtres et raccourcis, avec les changements génériques nécessaires au SDK et à la barre des tâches.
- Bibliothèque, fichiers récents et réglages restent propres à chaque utilisateur.
- Les chemins, `.lnk`, icônes, noms et arguments issus de l’invité sont des données non fiables. Propose une lecture sûre et une identité de lancement claire.

Ne suppose pas acceptés les détails non présentés de 70/71 : limite de 64 entrées, modèle exact d’identifiants opaques, règles de suppression et nettoyage de tous les raccourcis, mécanisme d’extraction des icônes ou configuration de winemenubuilder.

Le comportement exact de « retirer une entrée de bibliothèque » reste à préciser : retrait des raccourcis correspondants, désinstallation du programme et suppression de bouteille sont des actions différentes. La conservation des données à la désinstallation de BoxedWine est déjà décidée et ne doit pas être révoquée par ce détail.

## 14. Réseau simulé et réseau réel

Pour le réseau simulé, chaque VM doit apparaître comme un hôte, et Kazo doit avoir sa propre adresse IP sur ce réseau local simulé. L’idée d’un commutateur interne, comme dans Hyper-V ou Docker, est une piste si elle n’ajoute pas trop de complexité.

Les services qui écoutent sur le réseau simulé doivent pouvoir être visibles dans une logique de réseau local. Distingue toutefois une écoute sur localhost d’une écoute sur une adresse réseau. Ne conclus pas que tous les ports deviennent accessibles partout.

Expérimente les comportements et propose les mécanismes. L’implémentation d’un pare-feu n’est pas imposée d’avance. Le réseau simulé ne demande pas les permissions du réseau réel.

L’accès au réseau réel reste soumis aux permissions Kazo/Os-x86 et aux limites du relais lorsqu’il est disponible. Le fonctionnement local d’Os-x86 ne doit pas dépendre d’un serveur, mais cela ne signifie pas que l’Internet réel peut être fourni sans l’infrastructure nécessaire.

Vérifie les protocoles, ports, connexions simultanées, connexions entrantes, identité des programmes et limites du navigateur/relais. Regroupe cette analyse avec celle des permissions, au lieu de la disperser en décisions contradictoires.

## 15. Presse-papiers, son et entrées

### Presse-papiers

Texte simple en V1, dans les deux sens entre Kazo et l’invité lorsque la fenêtre de cet invité est active. Images et formats riches en V2.

Je n’ai pas demandé une isolation supplémentaire du presse-papiers entre les applications déjà autorisées. Cela ne vaut pas accord pour partager entre utilisateurs ni pour synchroniser sans contrôle toutes les VM en arrière-plan.

Le transport, la taille limite et l’articulation avec les permissions existantes restent à proposer. L’intégration avec le presse-papiers de l’ordinateur réel est distincte du partage Kazo/VM : les contraintes de geste utilisateur, permissions navigateur et éventuelle nouvelle tentative doivent être vérifiées. Son échéance V1 exacte n’a pas été tranchée séparément.

### Son

Je souhaite entendre le son des VM dans Kazo dans la mesure où c’est faisable. Le mécanisme, le mélange des flux, le volume, le bouton muet et le coût doivent être étudiés.

Ne considère pas comme validé qu’un bouton muet dans une fenêtre coupe toute sa VM : cette granularité n’a pas été explicitement choisie. Compare les possibilités et indique les limites réelles de v86, sans promettre une carte HDA si le moteur ne la fournit pas.

### Souris et clavier

Les applications doivent pouvoir capturer la souris, avec un moyen de libérer la capture. La configuration peut être par application ou par VM ; son niveau exact reste à préciser. Échap a été cité comme exemple, pas comme touche universellement garantie.

Pendant la capture, l’application qui a pris le contrôle a la priorité et les raccourcis Kazo ne sont pas utilisés. Il faut conserver un moyen configurable de libérer la capture.

Vérifie les limites du navigateur, du plein écran, du verrouillage du clavier et des dispositions de touches. La priorité sur les raccourcis Kazo ne signifie pas que le navigateur permettra de détourner tous ses propres raccourcis.

## 16. Journal d’événements

Les événements importants, par exemple mise à jour, permission accordée ou conflit de fichiers résolu, doivent être disponibles dans le journal d’événements de Kazo OS.

Précise quelles informations sont enregistrées, comment elles se rattachent à une VM ou à un programme et qui peut les consulter selon les permissions existantes. Ne déduis pas de cette réponse un accès universel aux détails de tous les utilisateurs, ni un journal uniquement réservé aux administrateurs.

## 17. Performance, navigateurs et essais

### Objectifs

- Une machine avec 8 Go de RAM est le minimum visé. Des machines avec 16, 32 ou 64 Go sont des cibles probables, sans garantie automatique de plusieurs VM sur 8 Go.
- Un démarrage inférieur à 10 secondes est très bon ; inférieur à 30 secondes est acceptable. Au-delà de 10 secondes, examiner les possibilités d’optimisation.
- Définir précisément ce qui est mesuré : premier téléchargement, démarrage à froid, reprise à chaud, création de bouteille, lancement du programme et délai avant utilisation réelle.
- Prévoir les critères et les points d’arrêt à l’avance, puis les réviser explicitement si les mesures révèlent une incohérence. Ne pas fixer un chiffre sans préciser le matériel, le navigateur et l’état du cache.
- Certaines fonctions peuvent être réservées à Chrome/Edge si nécessaire. Afficher clairement qu’une fonction précise n’est pas prise en charge sur le navigateur concerné. Cela ne signifie pas abandonner globalement Firefox ou Safari.

### Essais à préparer

La maquette préalable doit établir si les objectifs essentiels sont réalistes : Linux, FreeDOS, Wine console et graphique, affichage, mémoire, lancement froid/chaud et, si utile, coût de sauvegarde/reprise.

Prévoir des comparaisons exécutées pour la base commune ou séparée, les couches de différences et les mécanismes de stockage. Les résultats doivent être mesurés, reproductibles et accompagnés des limites.

Inclure aussi les scénarios d’échec : permissions refusées, annulation, manque de mémoire ou stockage, montage disparu, perte de connexion, changement d’image, interruption de mise à jour, sauvegarde de VM active, conflit de synchronisation, fermeture d’une fenêtre secondaire, arrêt d’une application parmi plusieurs et changement d’utilisateur.

### Tests et GitHub Actions

Les tests font partie de V1. Les suites longues propres à Os-x86 et BoxedWine ne doivent pas s’exécuter systématiquement pour chaque changement sans rapport dans Kazo OS.

Prévois des déclenchements sélectifs selon les dépôts, composants et dépendances concernés. Les modifications amont de Kazo qui peuvent affecter Os-x86 ou BoxedWine doivent déclencher les tests pertinents. Les GitHub Actions doivent suivre la même logique, pas seulement les tests lancés à la main.

La sélection précise des tests, la structure de la solution et les dépendances de CI sont à proposer. Le fait de limiter les suites inutiles n’autorise pas à omettre les vérifications d’une modification réellement liée.

## 18. Licences et conventions

- Os-x86 reste MIT.
- BoxedWine garde son nom et doit passer sous MIT conformément à la décision déjà prise. Cela ne signifie pas intégrer du code GPL du projet Boxedwine dans l’application.
- Prévoir, au moment de l’implémentation autorisée, la correction de la description historique « HackerOs » de BoxedWine et de la note de Kazo qui le lie encore à la GPL-2. Aucun de ces changements n’est présenté comme déjà effectué.
- Kazo OS conserve sa licence actuelle. Chaque application déclare sa licence propre. Les risques de compatibilité relevés dans les recherches doivent être documentés ; ne présente pas un choix d’architecture comme avis juridique définitif.
- Les composants copyleft, moteurs, images et correctifs ont leurs propres obligations. Prévoir la séparation et l’offre de sources nécessaires pour ce qui est distribué.
- Les médias et licences Windows sont fournis par l’utilisateur ; ne prévoir aucune redistribution non autorisée.
- Les ADR précèdent le code concerné. Documentation en français avec miroir anglais, suivi de l’API, changelog et `features.md` à tenir à jour à chaque PR selon les conventions confirmées.
- La préparation du plan ne donne aucune autorisation de fusionner ou publier.

Concernant les jeux, le positionnement est le suivant : nous développons un écosystème, et l’application développée n’est pas un jeu. Cela ne doit pas exclure les jeux parmi les logiciels utilisables. Ne recopie pas comme décision l’interdiction générale de toute mention de jeux figurant dans l’ancienne carte. Signale la divergence avec l’ancienne référence à l’ADR 0135 et propose la formulation appropriée pour le dépôt et sa fiche, sans modifier les règles du dépôt Kazo OS de ta propre initiative.

## 19. Réconciliation explicite du journal

Je veux une réconciliation traçable, pas deux listes contradictoires qui continuent à coexister.

1. **D7, D13, D40, D49, D53, D55, D58 et D62 :** conserver la route Windows shell/association vers BoxedWine. Les ambitions d’intégration terminal et de code de sortie sont maintenant séparées entre V1 et V2. L’ancienne phrase « tout faire avec Os-x86 seul » ne rétablit pas la route directe Windows rejetée.
2. **D11, D24, D49, D54 et D63 :** pas d’app Sous-systèmes générale dans Os-x86 ; BoxedWine gère aussi Linux et son interface. Ne garde pas la restriction « réglages Linux/DOS exclusivement par x86 » comme décision actuelle. L’exception de sécurité Os-x86 est expressément conservée.
3. **D20, D41 et D50 :** même programme, même bouteille compatible, indépendamment de l’app qui le lance ; aucun accès global aux autres bouteilles. Réviser le critère d’app créatrice et préciser mises à jour et compatibilité.
4. **D14, D21, D22, D42, D43 et D52 :** réconcilier permissions choisies du programme, origine du lancement, droits de l’utilisateur, veto admin, héritage des enfants et protections du backend. Ne supprimer ni les plafonds nécessaires ni l’indépendance fonctionnelle du programme sans expliciter le conflit.
5. **D25 :** l’ancien repli VM/DOS seul ne remplit pas l’objectif final V1 comprenant Wine graphique.
6. **D26 et D55 :** objectif distinct pour chaque fenêtre Windows confirmé ; calendrier précis encore à clarifier. Ne conserver ni une simple fenêtre par programme comme résultat final ni un report arbitraire de tout le multifenêtre.
7. **D27 et D28 :** console native/fenêtre séparée acceptable, intégration avancée reportable ; DOS dans le terminal souhaité sous réserve de complexité ; vrai code de sortie obligatoire en V2.
8. **D34, D48 et D56 :** distinguer restauration disque, sauvegarde cohérente, états mémoire dépendants du moteur et veille utilisateur V2.
9. **D35 et D59, questions 66/67/72.6 :** plusieurs VM possibles, une VM par système seulement conditionnelle, arrêt immédiat des VM d’applications quand elles ne servent plus, exception pour démarrage manuel, conservation des données par défaut.
10. **D56 :** mises à jour différables, choix maintenant/à la fermeture et retour à un état utilisable après échec. Distinguer mise à jour disponible et version minimale réellement indispensable.
11. **D57 et questions 70/71 :** marque faite par BoxedWine, ajout manuel, tutoriel obligatoire. Ne réintroduire ni détection automatique ni marque dessinée par Kazo comme décision acquise.
12. **D61.4 et 72.15 :** arrêt du programme lors de la fermeture console ou du composant propriétaire, sans tuer l’application quand seule une fenêtre secondaire se ferme, ni les autres applications de la VM.
13. **C21 à C23 :** stockage optimisé et synchronisation à concevoir ; sauvegardes incluant les VM par défaut ; toujours demander lors d’un conflit, sans écrasement automatique.
14. **C43 :** conventions de documentation/API/changelog/features confirmées ; positionnement écosystème et non exclusion des jeux.

Les recommandations originales après 64 ne sont pas une liste de décisions par défaut. Les éléments déjà acceptés auparavant, comme D61.1, D61.2, D61.3, D61.5, D61.6 et D61.10, ne redeviennent pas ouverts simplement parce qu’ils apparaissaient à nouveau dans la série 6. En cas de contradiction réelle avec une réponse récente, nomme-la et propose la réconciliation.

## 20. Travail attendu maintenant

Produis un plan complet, exploitable pour la suite, comprenant :

1. Une synthèse de l’architecture et des responsabilités réellement retenues.
2. Un registre réconcilié des décisions avec références aux numéros existants, changements récents, conditions et points encore ouverts.
3. Un périmètre V1 explicite, les éléments V2, et les éléments conditionnels dont le classement dépend d’une mesure ou de ma validation.
4. La liste des études et maquettes nécessaires, avec protocoles, résultats attendus, critères de réussite et conséquences possibles d’un échec.
5. Une comparaison des options encore ouvertes, ta recommandation, ses limites et la question précise à me soumettre.
6. L’inventaire et l’ordre des ADR ou changements génériques nécessaires dans Kazo : Feature et contrats, dépendances, permissions, shell/associations et binfmt, processus invités, fenêtres/icônes/raccourcis, stockage/fichiers, entrées, réseau et autres éléments réellement nécessaires.
7. La structure proposée des dépôts/projets, sans remettre le moteur Boxedwine ou l’app Sous-systèmes abandonnés dans le plan.
8. Des étapes de livraison avec dépendances, tests et démonstrations vérifiables. Ne marque pas une étape terminée parce que sa documentation existe si le résultat attendu n’a pas été prouvé.
9. Une série ciblée sur la répartition des réglages, profils et opérations administrateur, comme demandé.
10. Une liste courte des vraies décisions restant à me demander, sans refaire toutes les questions déjà répondues.

Parmi les points encore à préciser : calendrier exact du multifenêtre ; garanties V1 si console avancée/héritage sont reportés ; modèle final des droits et de l’isolation réseau ; détail des réglages et opérations admin ; granularité du son ; intégration au presse-papiers réel ; retrait des entrées de bibliothèque ; portée du consentement de téléchargement ; cas d’une mise à jour indispensable ; stockage, migrations et protection des données.

Ces points ne sont pas tous des blocages de tout le plan. Prépare ce qui peut l’être et montre clairement les dépendances. Je veux pouvoir valider tes recommandations en comprenant leur impact, sans découvrir plus tard qu’une proposition encore ouverte a été traitée comme une décision.

Ne commence pas le code de production avant l’approbation du plan et des choix bloquants. Pour cette étape, livre l’analyse et le plan, avec les questions restantes clairement identifiées.
