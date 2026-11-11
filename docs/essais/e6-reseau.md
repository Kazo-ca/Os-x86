# Essai E6 — réseau et permissions par bouteille

Date : 2026-11-11. Code jetable dans `essais/e6-reseau/` (ligne E6 des études prévues, réponse 101). L'essai couvre : un espace réseau par bouteille dans un invité Alpine 3.24 x86 (32 bits) sous v86 ; la sortie par un relais WebSocket local qui termine lui-même Ethernet/IP/TCP et applique les limites façon Kazo (4 flux par identité, 300 s par flux, ports 80/443) ; deux programmes de deux bouteilles avec deux décisions différentes, appliquées et journalisées ; des contrôles de confinement depuis une bouteille ; la comparaison « une VM, plusieurs bouteilles » contre « VM séparées » ; les modes réseau de v86 ; le surcoût du chemin netns + nftables sur le débit.

Non couverts : Wine (les programmes de test sont `curl` et `nc`, à la place de programmes Windows), l'Internet réel et un vrai relais distant, les connexions entrantes (passerelle du §14), Firefox/Edge/Safari, le Web Worker, la sauvegarde d'état.

## But

Savoir ce que v86 laisse voir du trafic d'un invité, où une décision « par programme » s'applique avant d'ouvrir un flux réel, si l'étiquette « bouteille » survit jusqu'au relais, ce que coûtent deux bouteilles dans une VM contre deux VM, et si les garanties de durcissement de la section 134 de `verification-105-134.md` tiennent. Nourrit 116, C30-C32, §6 et §14 des consignes, D20, D21, D42, D50.

## Ce qui a été construit

Tout est dans `essais/e6-reseau/` (README de 10 lignes).

- `Dockerfile` : `FROM --platform=linux/386 alpine:3.24`, paquets `iproute2 nftables iptables curl busybox-extras shadow util-linux-misc procps-ng` en plus de la base OpenRC d'E1 ; autologin root sur `ttyS0` ; les bits setuid/setgid sont retirés de l'image (le `find -perm -4000` final ne renvoie plus rien) ; ni `sudo` ni `doas` ; config du noyau copiée dans `/root/kernel-config` ; `mkinitfs -F "base virtio 9p"`. Image exportée en arborescence 9p : **1 711 fichiers, 51,3 Mio en zstd** (E1 avec Wine : 186,6 Mio). Une seule image Docker (132 Mo) ; le tar est effacé après conversion.
- `guest/bottle-net.sh` : `up` monte `eth0` (virtio-net) par DHCP, pose `ip_forward=1` et `user.max_user_namespaces=0`, puis pour chaque bouteille N : `ip netns add bN`, paire veth (`veth-bN` 10.0.N.1/24 côté racine, `eth0` 10.0.N.2/24 dans bN, route par défaut via 10.0.N.1), usager `wine-bN` (uid 100N), home en tmpfs `nosuid,nodev,mode=0700`, dossier `/tmp/.wine-100N` 0700 (tient lieu du dossier du `wineserver`). **Routage pur, pas de NAT** : l'adresse source 10.0.N.2 est l'étiquette que la page et le relais lisent dans chaque trame. nftables (espace racine) : chaîne `forward` politique `drop`, b1 → tcp 80 seulement (plus le retour, règles sans état), b2 → rien ; chaîne `output` : sur `eth0`, un processus non root de l'espace racine n'émet rien (`meta skuid != 0 drop`). `run N cmd` exécute `cmd` dans bN comme `wine-bN` (`ip netns exec bN su wine-bN`).
- `guest/probe.sh` : inventaire (config noyau, modules, applets, binaires setuid, droits de `/dev/ttyS0`).
- `guest/verify-confinement.sh` : les contrôles listés plus bas, lancés comme `wine-b2` dans b2.
- `relay.mjs` : relais WebSocket local (Node + `ws`). v86, avec l'adaptateur « relay » simple, envoie des **trames Ethernet brutes** sur la WebSocket ; le relais répond ARP, DHCP, DNS statique et ICMP, et termine lui-même le TCP (poignée de main, fenêtre, retransmission) vers un vrai socket Node sur `127.0.0.1`. **C'est là qu'on applique la décision par programme, avant d'ouvrir le flux réel** : l'identité est l'adresse IP source de la trame (10.0.1.2 = b1, 10.0.2.2 = b2). Limites : ports 80/443, 4 flux simultanés par identité, 300 s par flux (ajustable à chaud pour l'essai). Toute cible est redirigée vers `127.0.0.1` (port 80 → 8080, 443 → 8443). Décisions de départ : b1 autorisé au port 80, b2 refusé partout.
- `target.mjs` : cible HTTP locale (127.0.0.1:8080 et :8443), compteur de requêtes, `/blob?mb=20` pour le débit.
- `server.mjs` : serveur statique (COOP/COEP, Range) comme E1, port 8096.
- `boot.html` : page v86, `net_device: { type: "virtio", relay_url: "ws://localhost:9099/" }`. **Le mode `inbrowser` n'est jamais passé.** `window.E6` expose les marques, l'info série et le nom de l'adaptateur réseau.
- `run.mjs` : pilote Playwright (Chrome installé, headless). Boot, `probe`, `bottle-net up`, décisions, limites, confinement, débit, comparaison 1 VM / 2 VM. Écrit `results-<n>.json`.

Configuration v86 : `memory_size` 512 Mio, VGA 2 Mio, racine 9p, `console=ttyS0 tsc=reliable mitigations=off`, NIC virtio-net.

## Protocole

Pour chaque run, `run.mjs` enchaîne, dans un contexte Playwright neuf : (1) boot jusqu'au prompt série ; relevé du nom de l'adaptateur réseau de v86 ; (2) `probe.sh` ; (3) `bottle-net.sh up` ; (4) **décisions** : `curl http://web.test/` depuis b1 (port 80 autorisé) et depuis b2 (refusé) ; lecture des journaux du relais et des compteurs de la cible ; (5) **limites** : 5 flux `nc` parallèles depuis b1 (plafond 4) ; `curl` b1 vers 443 et vers 8443 (nft ouvert pour b1 le temps du test, pour que les paquets atteignent le relais) ; coupure de durée avec le relais réglé à 5 s ; (6) **confinement** : `verify-confinement.sh` dans b2, plus un `curl` depuis un usager non root de l'espace racine ; (7) **débit** : `curl /blob?mb=20` par b1 (chemin netns) et en direct par root sur `eth0` (sans netns), deux fois chacun ; (8) **ressources** : mémoire invité et processus du navigateur pour une VM à deux bouteilles, puis deuxième page v86 (VM séparée) et mémoire des deux.

Les noms `web.test` sont dans `/etc/hosts` (→ 203.0.113.9) : pas de DNS, donc UDP 53 reste bloqué par nft. Trois runs complets (`results-1..3.json`). L'hôte, le navigateur et le cache sont ceux d'E1.

## Machine, navigateur, cache

- Windows 11 Pro 26200, AMD Ryzen 7 3700X (v86 n'utilise qu'un cœur), 64 Go de RAM.
- Chrome 156.0.8078.80 (Chrome installé, piloté par Playwright, headless).
- Serveur, relais et cible sur `localhost`/`127.0.0.1` : les débits ne contiennent pas la latence d'un vrai réseau, mais tout le TCP de la VM passe réellement par le relais userspace.
- v86 0.5.473 (npm).

## Résultats

Temps en secondes ; débit en Mio/s (2^20 o/s). Trois runs ; quand ils concordent, une seule valeur est donnée, sinon la fourchette.

### Décisions appliquées (deux bouteilles, deux décisions)

| Mesure | b1 (décision : port 80 autorisé) | b2 (décision : refus total) |
|---|---|---|
| `curl http://web.test/` | **HTTP 200** (rc 0) aux 3 runs | **échec** : HTTP 000, rc 7 (connexion impossible) |
| Vu par le relais | 1 flux ouvert, identité `10.0.1.2` étiquetée `b1`, port 80 | aucun flux (nft coupe la trame dans l'invité, avant le relais) |
| Vu par la cible | la requête de b1 est comptée sur 8080 | aucune requête de b2 |

Les deux décisions différentes sont donc appliquées et attribuées à la bonne bouteille : le relais journalise `open … identity=10.0.1.2 label=b1 dport=80`. b2 est arrêté **deux fois** : d'abord par nftables dans l'invité (politique `drop` de sa chaîne forward), et de toute façon par la décision du relais (`b2` : aucun port autorisé) si on ouvrait nft. L'étiquette « bouteille » survit jusqu'au relais parce que l'espace de noms donne à chaque bouteille une adresse source distincte et qu'on ne fait pas de NAT.

### Limites du relais

| Test | Attendu | Résultat (3 runs) |
|---|---|---|
| 5 flux `nc` simultanés depuis b1 | 4 ouverts, 5e refusé | **4 ouverts, 1 refusé** « plafond 4 flux atteint » |
| `curl` b1 → port 443 (nft ouvert) | refus par la décision b1 (HTTP seulement) | **refusé** côté relais : « decision b1 : port 443 refuse » ; curl rc 7 |
| `curl` b1 → port 8443 (nft ouvert) | refus (hors 80/443) | **refusé** côté relais : « port 8443 interdit (seuls 80/443) » ; curl rc 7 |
| Flux oisif, relais réglé à 5 s | coupé vers 5 s | **coupé à 5 s** : le relais journalise `timeout sec=5` puis `close why=max-sec dur_s=5` |

La coupure de durée est prouvée par le journal du relais (`dur_s=5` sur le flux `10.0.1.2:38249>203.0.113.9:80`). Note : la mesure côté invité (`nc` tenu ouvert par un `sleep 60`) a parfois affiché 60 s au lieu de 5 s selon la façon dont busybox `nc` réagit au RST en lecture bloquée ; la preuve retenue est le journal du relais, qui ferme bien le flux à 5 s. Les 300 s par défaut n'ont pas été attendus en entier ; le mécanisme est le même timer.

### Contrôles de confinement et garanties (lancés comme `wine-b2` dans b2)

Chaque ligne indique si le confinement a tenu (résultat attendu) et quelle garantie de la section 134 de `verification-105-134.md` est concernée. Les 8 contrôles tiennent aux 3 runs.

| Contrôle | Résultat observé | Garantie (R134) |
|---|---|---|
| `unshare -n` (nouvel espace réseau) | refusé : « unshare failed: Operation not permitted » (user namespaces désactivés) | garantie 1 : un processus ne change pas d'espace |
| `nsenter` vers l'espace racine (PID 1) | refusé : « reassociate to namespaces failed: Operation not permitted » (pas de CAP_SYS_ADMIN) | garantie 1 |
| lire le dossier `wineserver` d'une autre bouteille (`/tmp/.wine-1001`) | refusé : « Permission denied » (0700, autre uid) | garantie 2 |
| lire le home d'une autre bouteille (`/home/wine-b1`) | refusé : « Permission denied » | garantie 2 |
| lire la console série `/dev/ttyS0` (canal de l'agent) | refusé : « Permission denied » (`crw------- root`) | garantie 2 (canal agent réservé à root) |
| socket brut / `ping` (usurpation, sniff) | refusé : « ping: permission denied (are you root?) » | garantie 1/2 (pas de CAP_NET_RAW) |
| binaire setuid à exploiter | aucun : `find -perm -4000` ne renvoie rien | garantie 2 (setuid retirés) |
| home monté `nosuid` | confirmé : `tmpfs … nosuid,nodev … mode=700,uid=1002` | garantie 2 (`nosuid`) |
| programme « hors bouteille » (usager non root, espace racine) vers le réseau | refusé : HTTP 000 (chaîne `output` nft) | §6 : un programme sans décision n'a pas de réseau |

DNS (UDP 53) : jamais autorisé par nft pour les bouteilles ; l'essai passe par `/etc/hosts`, donc l'exfiltration par DNS est bloquée de fait (aucune règle ne laisse sortir l'UDP 53). IPv6 : pas de pile IPv6 dans le relais (le mode `fetch`/`wisp`/relay de v86 ne traite pas IPv6), donc l'invité ne sort pas en IPv6 par cette voie.

### Comparaison : une VM à deux bouteilles contre deux VM séparées

Mémoire privée des processus du navigateur (somme), `memory_size` 512 Mio par VM.

| Mesure | 1 VM (2 bouteilles) | 2 VM séparées (2 pages v86) |
|---|---|---|
| Mémoire privée navigateur | 1 443-1 468 Mio | 2 388-2 401 Mio |
| Processus navigateur | 7 | 8 |
| Démarrage jusqu'au prompt | 58-68 s | 1re ~58-68 s, 2e 62-66 s |
| Invité : mémoire utilisée | 29 Mio (les deux bouteilles, après `up`) | ~29 Mio par VM |

Lecture : une seule VM héberge les deux bouteilles pour **~1,45 Gio** côté navigateur et 29 Mio côté invité ; deux VM séparées coûtent **~2,4 Gio** (≈ +1 Gio, soit le `memory_size` d'une VM en plus) et un processus de plus, et chaque VM reboote (58-68 s) sur le cœur unique émulé. Extrapolation (à valider, non mesurée) : 4 programmes = 4 bouteilles dans 1 VM resteraient autour de 1,5 Gio navigateur + ~60-120 Mio invité ; 4 VM séparées approcheraient 4 × ~1,2 Gio et quatre reboots, avec contention CPU (v86 est mono-cœur par page). La VM partagée gagne nettement en mémoire et en démarrage ; son prix est l'isolation plus faible (affichage, presse-papiers, clavier partagés, cf. §6), que cet essai ne lève pas.

### Débit (curl de 20 Mo via le relais), netns contre direct

| Chemin | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| b1 via netns + nft (source 10.0.1.2) | 4,9 / 7,4 | 4,7 / 6,5 | 5,3 / 6,4 |
| root direct sur `eth0` (sans netns) | 6,7 / 9,6 | 5,3 / 7,3 | 6,2 / 7,5 |

(deux valeurs par cellule = les deux passes du run, en Mio/s). Le transfert de 20 Mo (20 971 520 octets) arrive complet à chaque fois par le relais userspace : la pile TCP maison (fenêtre + retransmission) tient la charge. Le chemin netns + nft est plus lent que le direct d'environ 15-30 %, un surcoût attendu (saut veth, forwarding, filtrage sans état). Ordre de grandeur : 5-9 Mio/s à travers v86 sur `localhost`, donc le goulot est l'émulation CPU, pas le relais.

### Mode `inbrowser`

L'adaptateur réseau actif est `pb` (le relais WebSocket simple) aux 3 runs, jamais l'adaptateur `inbrowser`. Lecture de la source `libv86.js` : le choix se fait sur `net_device.relay_url` — `"fetch"` crée l'adaptateur `fetch` (requêtes `fetch()` depuis la page, qui contourneraient un relais), `"inbrowser"` crée un adaptateur sur `BroadcastChannel` (ponts entre onglets), `wisp://` l'adaptateur wisp, sinon une URL `ws(s)://` crée le relais simple. Le mode est choisi **par la configuration de la page uniquement** ; l'invité n'a aucun moyen de changer `relay_url`. En ne passant que l'URL WebSocket, la page bloque `inbrowser`/`fetch` : tout le trafic passe par le relais, où la décision et les limites s'appliquent.

## Ce qui a raté ou a été contourné

- **Fins de ligne CRLF.** Les scripts de l'invité, écrits sous Windows, portaient des `\r` : `sh /root/bottle-net.sh` échouait (`not found`) une fois dans l'image. Corrigé en forçant LF avant le build ; un run complet a été perdu à cause de ça.
- **`chpasswd` de `shadow`** refuse un mot de passe vide (busybox l'acceptait dans E1) : le build échouait. Remplacé par un `sed` qui vide le champ dans `/etc/shadow`.
- **`/dev/null` à 0660 root.** Sans udev, `mdev` crée `/dev/null` en `crw-rw---- root`, donc un usager non root ne peut pas y écrire ; `curl -o /dev/null` échouait dans les bouteilles. `bottle-net up` fait `chmod 666 /dev/null`. `/root` étant 0700, `verify-confinement.sh` est recopié dans `/tmp` pour être lisible par `wine-b2`.
- **Mesure de coupure côté invité incohérente** : voir la note du tableau des limites ; la preuve retenue est le journal du relais.
- **Pile TCP maison, jetable** : le relais implémente un TCP minimal (ACK cumulatif, fenêtre annoncée, retransmission simple). Il a transféré 20 Mo de façon fiable sur `localhost`, mais n'a pas été éprouvé sur perte/latence réelles ni sur des charges variées. Ce n'est pas une pile de production.

## Limites

- Une seule machine, un seul navigateur, headless, `localhost`. Trois runs.
- Pas de Wine : les « programmes » sont `curl`/`nc`. Une vraie décision « par programme Windows » suppose une bouteille par programme (D20), ce que l'essai modélise par une bouteille par espace de noms, pas par un vrai `wineserver`.
- Pas d'Internet réel ni de vrai relais distant ; cibles sur `127.0.0.1`. Le relais `relay.widgetry.org` public n'a pas été contacté.
- Connexions entrantes (un service de la bouteille visible depuis Kazo, §14) : des règles nft existent mais ne sont pas testées (le relais n'initie pas de connexion vers l'invité).
- La pile TCP du relais est une maquette ; les chiffres de débit valent comme ordre de grandeur, pas comme mesure d'un vrai relais.
- L'isolation « VM partagée » ne couvre que le réseau et les usagers ; affichage, presse-papiers et clavier partagés ne sont pas traités.

## Chiffres bruts

`essais/e6-reseau/results-1.json`, `results-2.json`, `results-3.json` (journaux du relais, de la cible, sorties série ; ignorés par git). Image et journaux de build dans `essais/e6-reseau/images/` (ignorés par git).

## Ce que ça veut dire pour Os-x86

Chaque point est une proposition à valider par toi, pas une décision.

1. **Un espace de noms réseau par bouteille, avec adresse source distincte et sans NAT, suffit à étiqueter de façon fiable le trafic jusqu'au point de sortie.** La décision « par programme » (ici par bouteille) s'applique au relais avant d'ouvrir le flux réel, et le journal attribue chaque flux à la bonne bouteille. Ça soutient D20 (une bouteille par programme, réseau isolé, IP propre) et le modèle du §6 « plusieurs programmes dans une VM avec attribution fiable par application ». À valider.
2. **Deux décisions différentes, appliquées et prouvées** (b1 → port 80, b2 → refus), avec double barrière : nftables dans l'invité (keyé sur l'adresse de la bouteille) et la décision au relais. L'intersection de couches de D21 se retrouve ici (invité ∩ relais). À valider : l'implémentation réelle devra décider où vit la décision (le relais côté Kazo est le bon endroit pour les limites et le journal ; nft dans l'invité est une défense en profondeur, pas la source de vérité).
3. **Les limites du §14 sont tenables au relais** : 4 flux par identité, ports 80/443, coupure de durée. Le relais voit les trames Ethernet, donc peut compter les flux, filtrer les ports et horodater — c'est le bon endroit pour « autoriser tout l'accès d'une application ou d'une VM, garder des journaux, conserver les limites de l'admin ». À valider sur un vrai relais (pas une maquette userspace).
4. **La comparaison chiffrée penche pour une VM partagée quand l'attribution est fiable** : ~1,45 Gio contre ~2,4 Gio pour deux programmes, et un seul démarrage. Comme l'attribution par bouteille est justement fiable (point 1), l'option « VM par système invité, plusieurs bouteilles » du §6 est soutenue ; les VM séparées se justifient quand l'isolation d'affichage/presse-papiers/clavier est requise, pas pour le réseau seul. À valider ; l'extrapolation à N=4 n'est pas mesurée.
5. **Le mode `inbrowser`/`fetch` de v86 est bloqué en ne passant qu'une URL WebSocket** ; l'invité ne peut pas le réactiver. Donc le réseau réel passe forcément par le relais de Kazo. À valider : fixer ce choix dans la configuration de la Feature et ne jamais exposer `relay_url` à l'invité.
6. **Surcoût netns + nft de l'ordre de 15-30 %** sur le débit, négligeable devant le coût CPU de l'émulation (5-9 Mio/s sur `localhost`). L'isolation par bouteille ne se paie pas cher en débit. À valider sur un vrai réseau.

### Garanties de la section 134 de `verification-105-134.md`

- **Prouvées dans cette maquette** (b2, usager non root) : espace réseau par bouteille et impossibilité d'en changer (garantie 1 : `unshare -n` et `nsenter` refusés, `user.max_user_namespaces=0`) ; pas d'accès aux autres bouteilles (garantie 2 : dossiers 0700 d'un autre uid refusés, aucun setuid, home `nosuid`) ; canal de l'agent réservé à root (garantie 2 : `/dev/ttyS0` illisible par un non-root) ; un programme sans décision n'a pas de réseau (§6 : usager non root de l'espace racine bloqué).
- **Soutenues mais pas encore totales** : la décision ne dépasse jamais la bouteille (garantie 1) est prouvée pour la sortie TCP et pour l'absence de socket brut ; l'UDP 53 et l'IPv6 sont bloqués de fait (pas de DNS, pas de pile IPv6 au relais), mais ça tient à la configuration, pas à une règle explicite — à écrire comme règle. L'absence d'un vrai `wineserver` fait que « deux programmes Windows d'une même bouteille partagent la décision » (le fait Wine de 134) n'est pas testé ici ; l'essai teste des usagers Linux séparés, plus favorables.
- **Non testées** : la console avancée (R134.5 demandait les tentatives de sortie depuis une console — ici c'est un shell non root dans la bouteille, ce qui en est le modèle le plus proche ; une vraie console reste à éprouver en E7) ; l'accès inverse/entrant (§14) ; le canal de l'agent root-only côté écriture (seule la lecture de `ttyS0` a été testée).

Aucune garantie n'est **réfutée** par cet essai.
