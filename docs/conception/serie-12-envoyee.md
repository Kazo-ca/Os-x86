# Série 12 — envoyée le 2026-11-11

Les propositions du [plan](plan-2026-11-11.md) et du registre (section O) qui n'ont jamais été validées, relues et corrigées après une relecture indépendante (second jet). Même protocole : une lettre par numéro, deux choix réels au moins, une recommandation ✨ par question, « Autre » toujours possible ; rien n'est accepté sans ta réponse par numéro. Les numéros suivent la série 11 (173 à 199). Aucune question ne repose une décision déjà prise ; quand une option changerait une décision, c'est écrit dans l'option. Rappels de vocabulaire : série 11.

---

**173. Règles de partage de fichiers (E8)**
> E8 : lire 2 500 petits fichiers prend 10,9 s par 9p contre 1,34 s sur le disque ext4 de la VM (environ 8 fois ; 15 fois contre un tmpfs) ; l'extraction d'une archive de 3 000 fichiers prend 49 s sur 9p contre 43 s sur disque, et le cycle complet « installer, vérifier, copier, désinstaller » 135 s contre 101 s ; le serveur 9p de v86 accepte des noms réservés Windows, `../evil`, des liens durs et des setuid, et une exception côté hôte bloque l'invité pour toujours.

- A) Les programmes s'installent sur le disque de la VM, jamais sur le partage ; le partage sert aux documents ; Os-x86 remplace le serveur 9p de v86 par le sien, durci selon les exigences d'E8 (noms en liste blanche UTF-8 NFC ≤ 255 octets, sans réservés Windows ni `..`, aucune résolution de chemin côté hôte, refus de mknod, fifo, liens durs, setuid, xattr, toute exception → erreur d'E/S bornée) ; un partage = un périphérique 9p ✨(Recommandé)
  - Le partage reste lent sur les petits fichiers ; les documents n'en souffrent pas.
- B) Comme A, mais le partage peut aussi recevoir des programmes, avec un avertissement sur la lenteur
- C) Garder le serveur 9p de v86 tel quel et filtrer seulement les noms (moins de travail, moins de garanties)
- D) Autre : (Spécifiez)

**174. Règles réseau (E6)**
> E6 : l'adresse source par bouteille (sans NAT) est une étiquette fiable jusqu'au relais ; deux décisions différentes ont été appliquées et journalisées, au relais et dans l'invité ; le mode `inbrowser` de v86 dépend du seul `relay_url` de la page ; UDP 53 et IPv6 ne sont bloqués que de fait. L'option A demande un ADR Kazo (plan, ADR 13) : le relais de Kazo doit accepter une identité, une décision et des limites par flux venant d'une Feature.

- A) La décision, les limites (4 flux, 300 s, 80/443) et le journal vivent au relais côté Kazo (source de vérité) ; nftables dans l'invité, keyé sur l'adresse de la bouteille, en défense en profondeur ; `relay_url` fixé par la Feature, jamais exposé à l'invité ; UDP 53 et IPv6 bloqués par règle explicite (pas de DNS dans la bouteille : `/etc/hosts` fourni par Os-x86) ✨(Recommandé)
  - Un journal fiable, même si l'invité est compromis ; un ADR Kazo de plus avant le jalon 0.
- B) La décision et le journal vivent dans l'adaptateur réseau d'Os-x86 côté page (avant le relais), nftables dans l'invité en défense ; le relais ne fait que ses limites actuelles (aucun ADR Kazo)
  - Moins d'ADR ; le journal dépend du code de la page, pas du serveur.
- C) La décision vit dans l'invité seulement (nftables) ; le relais ne fait que ses limites
  - Le plus simple ; un invité compromis contourne la décision.
- D) Autre : (Spécifiez)

**175. Identité d'un programme mis à jour (E12)**
> .NET sous WebAssembly ne vérifie pas les signatures Authenticode ; vérifier par la clé du signataire (sans chaîne) suffit à dire « même éditeur que la version autorisée ». E12 estime (sans mesure) qu'un quart à un tiers des programmes sont signés, et moins d'un sur dix pour les titres de 1995 à 2010.

- A) « Chemin dans la bouteille d'abord, clé de signature ensuite, empreinte toujours, sélecteur de bouteilles en repli » : un exécutable réécrit au même chemin de `drive_c` est le même programme (l'API n'écrit jamais d'exécutable, D50) ; même clé de signature = même programme ; sinon fenêtre et sélecteur (D41) ; pas de chaîne X.509 en V1 ; E12 bis prouve la vérification par `SubtleCrypto` ✨(Recommandé)
- B) Chemin et empreinte seulement, sans signature ; toute mise à jour hors chemin redemande
- C) Autre : (Spécifiez)

**176. Noms et niveaux des permissions (141 : une permission qui couvre tout, plus une par cas)**
> D32 : « lancer des programmes » est normale (décidé). 141 : une permission globale pour « ses propres VM », plus une permission par cas (courant ; suppression et export).

- A) `x86.programs.launch` (normale, D32 : lancer et suivre des programmes, exécuter dans ses bouteilles) ; `x86.vm` (dangereuse : couvre tout ce qui suit) ; `x86.vm.use` (normale : démarrer, arrêter, régler, créer, importer ses VM et bouteilles, ouvrir une console) ; `x86.vm.destroy` (dangereuse : supprimer, exporter) ; jamais les VM d'un autre usager ✨(Recommandé)
  - BoxedWine déclare `x86.programs.launch`, `x86.vm.use` et `x86.vm.destroy` ; une app qui ne fait que lancer n'a que la première.
- B) Les mêmes, avec `x86.vm.use` dangereuse aussi (une seule approbation admin couvre tout)
- C) Autre : (Spécifiez)

**177. Détails du stockage (149)**
- A) Images de moins de 64 Mio admises dans IndexedDB ; `flush()` toutes les 5 s et à l'arrêt ; `persist()` demandé après le premier geste dans BoxedWine ou `x86` ; éviction détectée = avertissement et proposition d'export ; les disques comptent dans l'espace du navigateur, affiché dans BoxedWine ✨(Recommandé)
- B) Tout dans l'OPFS, aucun seuil ; `flush()` à chaque écriture (plus sûr, plus lent)
- C) Autre : (Spécifiez)

**178. Navigateurs et mobile (E10)**
- A) Aucune exigence de SharedArrayBuffer (worker + MessagePort) ; plancher Chrome/Edge 108, Firefox 111, Safari 16.4 ; sur mobile, une VM de 256 Mio plein écran avec clavier virtuel, point de restauration sur `visibilitychange`, pas de fenêtre par fenêtre, après l'essai E16 ; chaque fonction absente est affichée comme « non prise en charge sur ce navigateur » ✨(Recommandé)
- B) Chrome et Edge de bureau seulement en V1 ; mobile et autres navigateurs en V2
- C) Autre : (Spécifiez)

**179. Mécanisme cible de la fenêtre par fenêtre (E4 ; 112 a décidé « commencer simple, réévaluer en fin de V1 »)**
> E4 : le composite (agent XComposite + XDamage dans l'invité) gère le recouvrement, 2,6 trames/s sous charge à cause de l'écriture des pixels sur 9p ; le découpage de l'écran monte à 11 trames/s mais ne gère pas le recouvrement ; 18 ajouts côté Kazo (ordre z, focus, transients, menus, fenêtres sans propriétaire, icône de repli, titres, curseur).

- A) Cible = composite, avec un transport de pixels à revoir (mémoire partagée du worker plutôt que 9p) et les 18 ajouts Kazo (ADR 23) ; plan détaillé à te faire valider à la réévaluation de fin de V1 ✨(Recommandé)
- B) Cible = découpage de l'écran sans chevauchement (fenêtres disposées côte à côte par Kazo), plus simple, moins fidèle
- C) Autre : (Spécifiez)

**180. Détails de la console et des commandes internes (R134, 145)**
- A) « Exécuter dans la bouteille » : non interactive, arguments structurés, durée bornée (défaut 10 min), entrée fermée, sortie rendue à l'app, journalisée ; « ouvrir la console » : la fenêtre affiche la bouteille, ses droits réseau et « tout ce que tu lances ici a les droits de la bouteille » ✨(Recommandé)
- B) Comme A, sans durée bornée (l'app arrête elle-même la commande)
- C) Autre : (Spécifiez)

**181. Réseau de BoxedWine et téléchargement des images (126, D43, D61.7)**
> D61.7 : BoxedWine sans réseau pour lui-même. D43 : BoxedWine déclare `network.real.access`. 108 : aucun plafond fondé sur l'app qui lance. Si BoxedWine n'a pas de réseau propre et que son `network.real.access` ne plafonne rien, cette déclaration ne sert plus à rien.

- A) Os-x86 (la Feature) déclare `network.real.access` et télécharge les images ; BoxedWine ne déclare aucune permission réseau et ne fait que proposer l'image et afficher la progression ; **D43 est révisée** : BoxedWine ne déclare que les permissions de fichiers qu'il transmet ✨(Recommandé)
- B) BoxedWine garde `network.real.access` (D43) et télécharge lui-même les images (ça change D61.7)
- C) Autre : (Spécifiez)

**182. Modèle de menace (ADR d'Os-x86)**
> D49, 124.6 : Os-x86 se protège d'une app qui abuse de l'API, quel que soit l'appelant. D61.1 : le stockage n'est pas une isolation face à du JavaScript de la même origine.

- A) Dans le périmètre : programmes invités hostiles, apps Kazo qui abusent de l'API ou se font passer pour l'usager, usagers standards face aux réglages de l'admin ; hors périmètre, dit explicitement : JavaScript malveillant de la même origine (ADR 0182), noyau invité compromis face au moteur, admin malveillant ✨(Recommandé)
- B) Périmètre réduit aux programmes invités hostiles ; les apps Kazo sont tenues pour honnêtes
- C) Autre : (Spécifiez)

**183. Format des profils (121)**
> 121 : un profil = réglages de la VM et liste de programmes, sans disque, bouteille ni autorisation. `x86 profile` (185) exige que le format vive dans Os-x86.

- A) Fichier JSON `.x86profile` défini par Os-x86 (genre de VM, image minimale, mémoire, réseau, son, clavier, options Wine) avec une section « programmes » que BoxedWine remplit et lit ; import et export par BoxedWine et `x86 profile` ; les modèles prêts de §8 sont livrés sous ce format ✨(Recommandé)
- B) Deux fichiers : un profil de VM dans Os-x86 et une liste de programmes dans BoxedWine
- C) Autre : (Spécifiez)

**184. Mécanisme de la route du shell (162 : règle décidée ; 163 : `boxedwine x.exe` = `./x.exe`)**
> Fait : le shell lance aujourd'hui une fenêtre par son nom comme enfant, avec ses flux, et attend sa fermeture ; une app à instance unique déjà ouverte rend 0 tout de suite sans flux ; Ctrl+C tue la fenêtre.

- A) Le fichier lancé va à l'app associée comme une fenêtre lancée par son nom : enfant du shell, nouvelle instance par lancement, flux du shell, le terminal attend la fermeture ; BoxedWine n'est pas à instance unique ; Ctrl+C ferme l'instance et Os-x86 arrête le programme (D61.4) tant que l'ADR 22 manque ✨(Recommandé)
- B) Instance unique de BoxedWine qui reçoit les lancements successifs ; le terminal rend 0 tout de suite (contredit 163)
- C) Autre : (Spécifiez)

**185. Syntaxe de `x86` (D60 : nom décidé ; 119, 124.3, 145, 162)**
- A) `x86 vm list|create|start|stop|delete|export|import|snapshot <vm>` ; `x86 shell <vm> [--bouteille <id>]` ; `x86 run <vm> -- <commande>` ; `x86 detect <fichier>` ; `x86 rules` (ouvre la page des règles) ; `x86 profile export|import` ; `sudo x86 admin …` ; une seule session `x86 shell` par VM en V1 ; `--force` seulement sur `delete` ✨(Recommandé)
- B) Style `docker` : `x86 ps`, `x86 exec`, `x86 rm` (familier, mais mélange conteneur et VM, consignes §5)
- C) Autre : (Spécifiez)

**186. D21 et D43 après 108**
> 108 : pas de nouvelle demande, pas de plafond fondé sur l'app qui lance. D21 disait « intersection des droits d'Os-x86, de l'appelant, du sous-système, de la décision et du relais ». D43 : voir 181.

- A) D21 est réécrite : droit effectif = Os-x86 ∩ sous-système ∩ décision de l'usager ∩ règles de l'admin ∩ relais ; « l'appelant » n'y figure plus pour un programme lancé par une app (108) ; l'expéditeur marqué (ADR 11) sert au journal seulement ✨(Recommandé)
- B) Garder « l'appelant » dans D21 pour les seules apps qui lancent par l'API sans être BoxedWine (ça contredit 108 pour ces apps)
- C) Autre : (Spécifiez)

**187. Image : distribution et versions (après l'essai Wine dédié de 170)**
> D35 : Wine 11.0 épinglé. E13 : Alpine x86 vivant, Wine 11.0 figé par un mainteneur unique ; WineHQ i386 (glibc) 11.0 et 11.21 ; Debian i386 sans noyau. L'essai de 170 dira si le Wine d'Alpine ou celui de WineHQ corrige le bogue 1x1.

- A) Base Alpine 3.24 x86 avec `linux-virt` ; Wine 11.0 d'Alpine si l'essai de 170 le fait marcher, sinon le Wine qui a réussi l'essai (chroot glibc, version 11.x épinglée, ce qui précise D35) ; Mono 10.4.1 et Gecko 2.47.4 de WineHQ ; v86 0.5.47x avec le correctif #1661 appliqué localement tant qu'il n'est pas fusionné ; contrôle de la chaîne 32 bits à chaque branche d'Alpine (deux fois par an) et reconstruction au moins annuelle ✨(Recommandé)
- B) Invité Debian i386 complet avec le Wine de WineHQ, sans Alpine (image plus grosse, pas de noyau Debian i386 : noyau à construire)
- C) Autre : (Spécifiez)

**188. Retour arrière et « état sûr » (127, 169)**
- A) Point de restauration automatique avant toute mise à jour d'image ; « installer à la fermeture » attend que la dernière application de la VM se ferme (166), sans jamais arrêter une autre application ; en cas d'échec, la VM revient au point et l'usager est averti ; notes de version affichées si présentes ✨(Recommandé)
- B) Comme A, mais « installer à la fermeture » arrête la VM dès que l'application qui a demandé se ferme, même si une autre tourne (plus simple, contredit §10)
- C) Autre : (Spécifiez)

**189. Jeux et fiche du dépôt (§18)**
- A) Fiche d'Os-x86 et de BoxedWine : « fait partie d'un écosystème ; ce n'est pas un jeu, mais les jeux font partie des logiciels qu'il peut exécuter » ; aucune mention de jeux dans les ADR et PR du dépôt Kazo (ADR 0135) ✨(Recommandé)
- B) Aucune mention de jeux nulle part, comme l'ancienne carte
- C) Autre : (Spécifiez)

**190. Rangement des VM et des décisions par identifiant d'usager (R105.5, 131)**
> Un compte supprimé puis recréé avec le même nom reçoit un nouvel identifiant dans Kazo (ADR 0169).

- A) Par identifiant stable (GUID), jamais par nom de connexion ; un compte recréé ne retrouve pas les VM de l'ancien (elles suivent 131) ✨(Recommandé)
- B) Par nom de connexion, pour qu'un compte recréé retrouve ses VM (risque : un autre usager prend le nom)
- C) Autre : (Spécifiez)

**191. Qui affiche les confirmations de 124.2 (arrêt avec travail non enregistré) et 124.3 (suppression)**
- A) Des fenêtres d'Os-x86, comme la fenêtre de permission (une app ne confirme jamais à la place de l'usager, D49) ; BoxedWine ne fait que déclencher ✨(Recommandé)
- B) Des fenêtres de BoxedWine, Os-x86 vérifie seulement les droits
- C) Autre : (Spécifiez)

**192. Forme de la marque anti-imitation (117)**
- A) Une pastille de l'app qui affiche (BoxedWine ou Os-x86) dans la barre de titre et sur le bouton de la barre des tâches, dessinée par Kazo, non masquable par les options de cadre ; en plein écran, une bande discrète en haut, affichée 3 s à chaque entrée ✨(Recommandé)
- B) Pastille dans la barre des tâches seulement ; rien dans la fenêtre ni en plein écran
- C) Autre : (Spécifiez)

**193. Choix de la bouteille : répartition entre Os-x86 et BwLib (D50 corrigée, consignes §6)**
> Décidé : même programme, même bouteille, quelle que soit l'app ; l'app ne choisit pas la bouteille. Reste à dire qui, d'Os-x86 ou de la logique Wine de BoxedWine, décide.

- A) Os-x86 décide (identité du programme selon 175, bouteille, permissions, sélecteur) et expose « lancer » ; BwLib ne fait qu'exécuter des commandes Wine dans la bouteille choisie et lire ce qu'elle contient ; un autre front end Windows que BoxedWine obtient le même résultat ✨(Recommandé)
- B) BwLib décide (il connaît Wine) et demande à Os-x86 une bouteille par identifiant ; Os-x86 vérifie seulement les droits (un autre front end pourrait choisir autrement)
- C) Autre : (Spécifiez)

**194. Une VM partagée par genre contre des VM séparées (E6, consignes §6)**
> E6 : une VM à deux bouteilles ≈ 1,45 Gio côté navigateur contre ≈ 2,4 Gio pour deux VM, un seul démarrage ; l'attribution par bouteille est fiable ; mais affichage, clavier et presse-papiers sont partagés dans une VM commune.

- A) Une VM par genre et par image (Programmes Windows, Linux, DOS) pour toutes les bouteilles de l'usager ; VM séparées seulement pour les VM complètes et sur demande explicite (profil « VM dédiée ») ; la limite d'isolation est écrite dans la spécification ✨(Recommandé)
- B) Une VM par programme (isolation maximale ; mémoire et démarrage multipliés)
- C) Autre : (Spécifiez)

**195. Shell Linux dans le terminal : par l'agent, ou par la console série (119)**
- A) Par l'agent (plusieurs sessions, redimensionnement, couleurs) ; console série en repli pour une image apportée sans agent, une seule session, capacités affichées ✨(Recommandé)
- B) Console série seulement en V1 (plus simple, une session par VM)
- C) Autre : (Spécifiez)

**196. Image ou disque personnel importé sans agent (128)**
- A) Capacités réduites et affichées : écran, clavier, souris, disque, réseau selon les permissions de la VM ; ni partage de dossiers, ni liste de processus, ni fenêtre par programme ; l'usager peut installer l'agent lui-même (paquet fourni) pour tout retrouver ✨(Recommandé)
- B) Refuser l'import d'une image sans agent en V1
- C) Autre : (Spécifiez)

**197. L'inventaire et l'ordre des ADR (plan, section 6)**
- A) Tels quels ✨(Recommandé)
- B) Avec des changements : (nomme les ADR à déplacer, retirer ou ajouter)

**198. Les jalons J0 à J4 (plan, section 8 ; J0 = le jalon 0 de 150 B, en deux étapes)**
- A) Tels quels ✨(Recommandé)
- B) Avec des changements : (nomme-les)

**199. Le tableau de répartition des réglages (section 9) et la structure des dépôts (section 7)**
- A) Tels quels, y compris les lignes « proposé » ✨(Recommandé)
- B) Avec des changements : (nomme les lignes ou dossiers)

---

On retient quoi pour 173 à 199? Réponds par numéro ; une ligne vide n'accepte rien.
