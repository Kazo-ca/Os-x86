# Série 12 — envoyée le 2026-11-11

Les propositions du [plan](plan-2026-11-11.md) et du registre (section O) qui n'ont jamais été validées. Même protocole : une lettre par numéro, une recommandation ✨ par question, « Autre » toujours possible ; rien n'est accepté sans ta réponse par numéro. Les numéros suivent la série 11 (173 à 192). Rappels de vocabulaire : série 11.

---

### Fichiers et réseau (résultats d'essais)

**173. Règles de partage de fichiers (E8)**
> E8 : lire 2 500 petits fichiers prend 10,9 s par 9p contre 1,3 s sur le disque de la VM (15 fois) ; installer un programme de 3 000 fichiers prend 135 s sur 9p contre 101 s sur disque ; le serveur 9p de v86 accepte des noms réservés, `../evil`, des liens durs et des setuid, et une exception côté hôte bloque l'invité pour toujours.

- A) Les programmes s'installent sur le disque de la VM, jamais sur le partage ; le partage sert aux documents ; Os-x86 remplace le serveur 9p de v86 par le sien, durci selon les 30 exigences d'E8 (noms en liste blanche UTF-8 NFC ≤ 255 octets sans réservés Windows ni `..`, aucune résolution de chemin côté hôte, refus de mknod, fifo, liens durs, setuid, xattr, toute exception → erreur d'E/S bornée) ; un partage = un périphérique 9p ✨(Recommandé)
- B) Comme A, mais le partage peut aussi recevoir des programmes (avertissement sur la lenteur)
- C) Autre : (Spécifiez)

**174. Règles réseau (E6)**
> E6 : l'adresse source par bouteille (sans NAT) est une étiquette fiable jusqu'au relais ; deux décisions différentes ont été appliquées et journalisées ; le mode `inbrowser` dépend du seul `relay_url` de la page ; UDP 53 et IPv6 ne sont bloqués que de fait.

- A) La décision, les limites (4 flux, 300 s, 80/443) et le journal vivent au relais côté Kazo (source de vérité) ; nftables dans l'invité, keyé sur l'adresse de la bouteille, en défense en profondeur ; `relay_url` fixé par la Feature, jamais exposé à l'invité ; UDP 53 et IPv6 bloqués par règle explicite (pas de DNS dans la bouteille : `/etc/hosts` fourni par Os-x86) ✨(Recommandé)
- B) La décision vit dans l'invité (nftables) ; le relais ne fait que les limites
- C) Autre : (Spécifiez)

### Identité, permissions, stockage

**175. Identité d'un programme mis à jour (E12)**
> .NET sous WebAssembly ne vérifie pas les signatures Authenticode ; vérifier par la clé du signataire (sans chaîne) suffit à dire « même éditeur que la version autorisée » ; moins d'un vieux programme sur dix est signé.

- A) « Chemin dans la bouteille d'abord, clé de signature ensuite, empreinte toujours, sélecteur de bouteilles en repli » : un exécutable réécrit au même chemin de `drive_c` est le même programme (l'API n'écrit jamais d'exécutable, D50) ; même clé de signature = même programme ; sinon fenêtre et sélecteur (D41) ; pas de chaîne X.509 en V1 ; E12 bis prouve la vérification par `SubtleCrypto` ✨(Recommandé)
- B) Empreinte seule ; toute mise à jour redemande
- C) Autre : (Spécifiez)

**176. Noms et niveaux des permissions (141)**
- A) `x86.programs.launch` (normale : lancer et suivre des programmes, exécuter dans ses bouteilles) ; `x86.vm.manage` (dangereuse : régler, créer, importer ses VM et bouteilles) ; `x86.vm.use` (normale : démarrer, arrêter, ouvrir une console dans ses VM) ; `x86.vm.destroy` (dangereuse : supprimer, exporter) ; jamais les VM d'un autre usager ✨(Recommandé)
- B) Les mêmes, toutes dangereuses
- C) Autre : (Spécifiez)

**177. Détails du stockage (149)**
- A) Images de moins de 64 Mio admises dans IndexedDB ; `flush()` toutes les 5 s et à l'arrêt ; `persist()` demandé après le premier geste dans BoxedWine ou `x86` ; éviction détectée = avertissement et proposition d'export ; les disques comptent dans l'espace du navigateur, affiché dans BoxedWine ✨(Recommandé)
- B) Tout dans l'OPFS, aucun seuil
- C) Autre : (Spécifiez)

**178. Navigateurs et mobile (E10)**
- A) Aucune exigence de SharedArrayBuffer (worker + MessagePort) ; plancher Chrome/Edge 108, Firefox 111, Safari 16.4 ; sur mobile, une VM de 256 Mio plein écran avec clavier virtuel, point de restauration sur `visibilitychange`, pas de fenêtre par fenêtre ; chaque fonction absente est affichée comme « non prise en charge sur ce navigateur » ✨(Recommandé)
- B) Chrome et Edge de bureau seulement en V1
- C) Autre : (Spécifiez)

### Affichage et console

**179. Fenêtres en V1 (E4)**
- A) V1 = une fenêtre Kazo par programme (D26), vue de l'écran de la VM découpée par bureau virtuel Wine ; cible pour la fenêtre par fenêtre (V1.x ou V2 selon 112) = agent composite XComposite + XDamage, avec un transport de pixels à revoir (9p plafonne à 2,6 trames/s) et les 18 ajouts Kazo listés par E4 ✨(Recommandé)
- B) Viser directement la fenêtre par fenêtre en V1 (recule J3 et J4)
- C) Autre : (Spécifiez)

**180. Détails de la console et des commandes internes (R134)**
- A) « Exécuter dans la bouteille » : non interactive, arguments structurés, durée bornée (défaut 10 min), entrée fermée, sortie rendue à l'app, journalisée ; « ouvrir la console » : la fenêtre affiche la bouteille, ses droits réseau et « tout ce que tu lances ici a les droits de la bouteille » ✨(Recommandé)
- B) Autre : (Spécifiez)

### Images et mises à jour

**181. Qui télécharge les images (126, D61.7, D56)**
- A) Os-x86 télécharge et vérifie (avec sa permission réseau de Feature) ; BoxedWine ne fait que proposer et afficher la progression ; BoxedWine n'a aucun accès réseau propre en V1 ✨(Recommandé)
- B) BoxedWine télécharge avec `network.real.access`
- C) Autre : (Spécifiez)

### Sécurité, conventions

**182. Modèle de menace et jeux**
- A) Un ADR « modèle de menace » dans Os-x86 (périmètre : programmes invités hostiles et apps Kazo honnêtes ; hors périmètre : JavaScript malveillant de la même origine, noyau invité compromis), cité par la spécification ; fiche du dépôt : « Os-x86 fait partie d'un écosystème ; ce n'est pas un jeu, mais les jeux font partie des logiciels qu'il peut exécuter » ✨(Recommandé)
- B) Autre : (Spécifiez)

### Interface et commandes

**183. Format des profils (121)**
- A) Fichier JSON `.x86profile` (genre de VM, image minimale, mémoire, réseau, son, clavier, options Wine, programmes à ajouter à la bibliothèque), sans secret ni décision ; import et export par BoxedWine et `x86 profile export|import` ; les modèles prêts de §8 sont livrés sous ce format ✨(Recommandé)
- B) Autre : (Spécifiez)

**184. Mécanisme de la route du shell**
- A) Le fichier lancé va à l'app associée comme une fenêtre lancée par son nom : enfant du shell, **nouvelle instance par lancement**, flux du shell, le terminal attend la fermeture ; BoxedWine n'est pas à instance unique ; Ctrl+C ferme l'instance (Os-x86 arrête le programme, D61.4) tant que l'ADR 19 manque ✨(Recommandé)
- B) Instance unique de BoxedWine qui reçoit les lancements successifs (le terminal rend 0 tout de suite)
- C) Autre : (Spécifiez)

**185. Syntaxe de `x86`**
- A) `x86 vm list|create|start|stop|delete|export|import|snapshot` ; `x86 shell <vm> [--bouteille <id>]` ; `x86 run <vm> -- <commande>` ; `x86 detect <fichier>` ; `x86 rules` (ouvre la page des règles) ; `x86 profile export|import` ; `sudo x86 admin …` ; une seule session `x86 shell` par VM en V1 ; `--force` seulement sur `delete` ✨(Recommandé)
- B) Autre : (Spécifiez)

### Permissions, versions, mises à jour

**186. 108 face à D21 et D43**
> 108 : pas de nouvelle demande, pas de plafond fondé sur le lanceur. D21 : le droit effectif est une intersection avec l'appelant. D43 : BoxedWine déclare `network.real.access`.

- A) Le plafond de D21 reste l'intersection avec les permissions **déclarées** par l'app lanceuse (ce n'est pas un plafond « fondé sur le lanceur » : l'origine ne change pas la décision du programme) ; une app sans `network.real.access` ne peut pas lancer un programme qui en a besoin, et le refus est affiché sans redemander ✨(Recommandé)
- B) Aucun plafond par l'app lanceuse : seule la décision du programme compte (ça change D21 et D43)
- C) Autre : (Spécifiez)

**187. Versions de l'image**
- A) Alpine 3.24 x86, `linux-virt`, Wine 11.0 d'Alpine, Mono 10.4.1 et Gecko 2.47.4 de WineHQ, v86 0.5.47x avec le correctif #1661 appliqué localement tant qu'il n'est pas fusionné ; surveillance trimestrielle de la fin du x86 chez Alpine (repli : WineHQ i386 ou construction maison, E13) ✨(Recommandé)
- B) Attendre Alpine 3.25 et Wine 11.x
- C) Autre : (Spécifiez)

**188. Retour arrière et « état sûr » (127, 169)**
- A) Point de restauration automatique avant toute mise à jour d'image ; « installer à la fermeture » attend que la dernière application de la VM se ferme (166), sans jamais arrêter une autre application ; en cas d'échec, la VM revient au point et l'usager est averti ; notes de version affichées si présentes ✨(Recommandé)
- B) Autre : (Spécifiez)

### Plan

**189. L'inventaire et l'ordre des ADR (plan, section 6)**
- A) Tels quels ✨(Recommandé)
- B) Autre : (Spécifiez)

**190. Les jalons J0 à J5 (plan, section 8)**
- A) Tels quels ✨(Recommandé)
- B) Autre : (Spécifiez)

**191. Le tableau de répartition des réglages (plan, section 9)**
- A) Tel quel, y compris les lignes « proposé » ✨(Recommandé)
- B) Autre : (Spécifiez)

**192. La structure des dépôts (plan, section 7)**
- A) Telle quelle ✨(Recommandé)
- B) Autre : (Spécifiez)

---

On retient quoi pour 173 à 192? Réponds par numéro ; une ligne vide n'accepte rien.
