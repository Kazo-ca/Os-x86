> [English version](../en/network.md)

# Le réseau privé, les préfixes et le relais

Jalon 0, chantier 12 (`j0/12-network`). Ce chantier branche la carte réseau de chaque VM sur un
commutateur Ethernet privé tenu par Os-x86 côté page, donne une adresse à chaque VM et à chaque
préfixe, met chaque préfixe dans son propre espace réseau dans l'invité, et fait sortir les
connexions TCP par une passerelle qui décide, tient les limites et écrit le journal avant de
passer le flux à un relais ([ordre du jalon](../conception/jalon-0-execution.md)). Décisions :
161 A (réseau privé entre VM, une adresse par VM et par préfixe, sans NAT, aucune liaison avec le
réseau simulé de Kazo), 174 A (décision, limites et journal au relais, nftables dans l'invité en
défense, `relay_url` fixé, UDP 53 et IPv6 bloqués par règle), 176 A (permissions), D20, D21, D22,
146 (isolation entre préfixes d'une VM). Essai de référence : [E6](../essais/e6-reseau.md).

Le scénario du jalon 0 tient avec le relais de test : une connexion d'un préfixe vers une
destination non décidée ouvre la fenêtre de permission (une abstraction, voir plus bas) ;
« Autoriser » passe, « Refuser » bloque, le journal enregistre les deux.

## Où sont les choses

| Chemin | Contenu |
| --- | --- |
| `src/OsX86/Network/` | `Addresses.cs` (`Ipv4Address`, `MacAddress`), `Packets.cs` (Ethernet, ARP, IPv4, TCP, UDP, ICMP, sommes de contrôle), `AddressPlan.cs` (plan d'adresses, `FlowIdentity`), `PrivateSwitch.cs` (commutateur, `INetworkPort`, raisons d'abandon), `NetworkGateway.cs` et `TcpFlow.cs` (passerelle, fin des TCP de l'invité), `NetworkRules.cs` (destinations, règles, arbitrage, limites), `NetworkRuleBook.cs` (règles et journal d'un usager), `NetworkPolicy.cs` (règles puis fenêtre), `NetworkPermission.cs` (abstraction de la fenêtre), `Relay.cs` (abstraction du relais, relais de test), `NetworkDocuments.cs` (formats versionnés, `INetworkStore`), `NetworkHub.cs` (`NetworkHub`, `UserNetwork`, `VmNetworkAttachment`) |
| `src/OsX86/Engine/` | `INetworkEngine` (trames de la carte), `EngineStartOptions.Network`, messages `net` du protocole du worker |
| `src/OsX86.Feature/wwwroot/engine/` | `v86-worker.js` : la carte virtio-net de v86 sans `relay_url`, ses trames par lots dans les messages `net` ; `disk.js` : la charge utile d'une image ouverte en lecture seule partagée |
| `src/OsX86/Vm/VmInstance.Network.cs` | la VM sur le réseau : carte, adresse, espace réseau d'un préfixe, port du commutateur |
| `src/OsX86/Agent/AgentClient.Network.cs` | l'opération `net` du protocole 3 de l'agent |
| `src/OsX86.Feature/X86Command.Rules.cs` | `x86 rules list`, `allow`, `deny`, `ask`, `remove`, `log` |
| `src/OsX86.Feature/X86Files.cs` | `KazoNetworkStore` : règles et journal dans `~/.config/ca.kazo.x86/network/` |
| `images/agent/src/net.c` | l'opération `net` de l'agent ; `launch.c` entre dans l'espace réseau du préfixe |
| `images/linux-base/net-admin.sh`, `net/vm.nft`, `net/prefix.nft` | le côté invité : `eth0`, un espace réseau par préfixe, les règles nftables |
| `images/linux-base/probes/osx86-netcheck.c` | la sonde réseau des tests `guest` |
| `tests/OsX86.Tests/Network/` | tests `unit` et le test `guest` `NetworkLinuxBaseGuestTests` |

## Vue d'ensemble

```
invité (VM a)                          page (Os-x86)                                  sortie
┌────────────────────────────┐ trames  ┌──────────────────────────────────┐
│ préfixe p1 (netns osx86-p1)│ « net » │ commutateur de l'usager          │
│   10.86.2.2 ─ vx0 ─┐       │ ┌─────┐ │  port VM a ─┐                    │
│ préfixe p2         ├ racine┼>│ v86 │>│  port VM b ─┼─> passerelle       │──> IRelayConnector
│   10.86.2.6 ─ vx1 ─┘ eth0  │ └─────┘ │             │   10.86.0.1        │    (relais de Kazo,
│               10.86.0.2    │ worker  │             │   décision,        │     ADR 0255 ; relais
│ nftables en défense        │         │             │   limites, journal │     de test en local)
└────────────────────────────┘         └──────────────────────────────────┘
```

## La carte réseau et le commutateur (161 A)

- **La carte.** Quand le gestionnaire de VM a un réseau (`VmManager.Network`) et que la VM n'est
  pas une VM DOS, le moteur démarre avec `EngineStartOptions.Network` : le worker donne à v86 une
  carte virtio-net **sans `relay_url`**. v86 ne crée alors aucun adaptateur (ni `fetch`, ni
  `inbrowser`, ni wisp, ni WebSocket) : les trames passent seulement par son bus (`net0-send`,
  `net0-receive`), que le worker relaie à la page dans des messages `net` (base64, par lots à la
  fin de la tâche en cours, 64 trames au plus, 1514 octets au plus par trame). L'invité ne joint
  que le commutateur ; le mode réseau est choisi par la page seule (E6).
- **Le commutateur.** Un commutateur par usager (`UserNetwork`), créé au premier usage : deux
  usagers n'ont jamais de trame en commun. Chaque VM en marche est un port ; la passerelle
  d'Os-x86 en est un autre. Le commutateur vérifie chaque trame contre le plan d'adresses : une VM
  n'émet que depuis son adresse ou celles de ses préfixes, et n'a qu'une adresse Ethernet, la
  première que sa carte utilise ; une adresse Ethernet appartient à une VM tant qu'elle est
  branchée, si bien qu'une VM ne peut ni prendre l'adresse d'une autre ni la couper du réseau. Entre deux VM ne passe que le trafic d'une VM à
  une autre VM (adresses de VM des deux côtés) : un préfixe ne joint jamais une autre VM (146).
  Une requête ARP entre VM ne va qu'à la VM demandée. IPv6, les autres types Ethernet, les trames
  de diffusion IPv4 et les destinations inconnues sont abandonnés et comptés (`UserNetwork.Drops`).
- **Le port de la VM.** `EngineNetworkPort` met en file les trames pour l'invité et les poste au
  moteur par lots, dans l'ordre, hors du verrou du commutateur ; les trames de l'invité vont au
  commutateur sous le verrou du réseau de l'usager.
- **Deux VM de la même image.** Elles tournent ensemble depuis ce chantier : `disk.js` ouvre la
  charge utile de l'image (`image.bin`, jamais écrite par un moteur) en mode `read-only`, que
  plusieurs workers peuvent tenir à la fois ; le premier essai `guest` a montré que l'ouverture
  exclusive refusait la seconde VM (`layer-locked`). Un navigateur sans ce mode ignore l'option et
  garde l'ouverture exclusive ; un écrivain du worker de stockage trouve toujours le fichier pris.

## Le plan d'adresses

Un même plan pour chaque usager (leurs commutateurs ne se voient pas) :

| Adresse | Qui |
| --- | --- |
| `10.86.0.1` | la passerelle d'Os-x86 (sortie par le relais), adresse Ethernet `52:54:00:86:00:01` |
| `10.86.0.S/24` | la VM de l'emplacement S (2 à 254), sur son `eth0` |
| `10.86.S.4P+1/30` | côté VM du lien `vxP` du préfixe d'emplacement P (0 à 63) dans la VM S |
| `10.86.S.4P+2/30` | l'adresse du préfixe lui-même, dans son espace réseau : son **identité** |

Les emplacements sont donnés à l'exécution : celui d'une VM à son démarrage (le plus bas libre),
celui d'un préfixe à son premier usage dans une marche ; ils sont libérés à l'arrêt de la VM ou à
la suppression du préfixe. Une adresse nomme une VM ou un préfixe le temps d'une marche ; ce qui
dure, c'est l'identité qu'elle désigne (la VM par son GUID, le préfixe par son nom dans la VM), et
c'est elle que portent les règles et le journal. Ni NAT ni DHCP : l'agent configure `eth0` et
chaque espace réseau avec les numéros que lui donne Os-x86.

**Choix : attribution à l'exécution plutôt qu'adresses rangées dans le registre.** Rien à migrer
dans le format du registre, pas de collision entre deux marches, et l'identité d'un flux reste
celle du préfixe même si son adresse change d'une marche à l'autre.

## Un espace réseau par préfixe (E6, 146)

- **Opération de l'agent plutôt qu'extension de `prefix-copy`** (le choix laissé au chantier) :
  l'adresse d'un préfixe change d'une marche à l'autre et les espaces réseau vivent dans `/run`
  (perdus à l'arrêt) ; ils ne peuvent donc pas être faits une fois pour toutes à la création du
  préfixe. Le protocole de l'agent passe à la **version 3** avec une opération `net` :
  - `{"op":"net","action":"vm-up","vmSlot":S}` : `eth0` à `10.86.0.S/24`, route par défaut par
    `10.86.0.1`, `ip_forward`, IPv6 coupé, règles de la VM (`vm.nft`) ;
  - `{"op":"net","action":"prefix-up","prefix":P,"vmSlot":S,"prefixSlot":N}` : l'espace réseau
    `osx86-P` (`ip netns`), la paire veth `vxN` / `eth0`, l'adresse `10.86.S.4N+2/30`, la route
    par défaut par le lien, IPv6 coupé, règles du préfixe (`prefix.nft`) ;
  - `{"op":"net","action":"prefix-down","prefix":P}` : l'espace réseau retiré.

  Comme `prefix-ensure`, l'agent ne lance jamais de ligne de shell : il exécute le script fixe de
  l'image `/usr/lib/osx86/net-admin` (root, à root) avec une action qu'il connaît, un nom de
  préfixe et des nombres qu'il a vérifiés ; codes 0 (fait), 10 (rien à faire), autre (échec, sa
  dernière ligne dit pourquoi). Réponse `{"ok":true,"changed":…,"ms":…}` ; erreurs
  `no-net-support` (image sans `net-admin`, comme l'image Alpine de test), `net-failed`,
  `invalid-request`, `invalid-prefix`. Le client accepte toujours les agents de versions 1 et 2 :
  ils n'ont simplement pas de réseau.
- **Quand.** Os-x86 appelle `vm-up` dès que l'agent répond au démarrage, et `prefix-up` à la
  première utilisation d'un préfixe dans la marche (avec `prefix-ensure`) ; `prefix-admin delete`
  retire l'espace réseau avec l'usager du préfixe. Un échec laisse la VM ou le préfixe sans réseau,
  signalé en arrière-plan, sans empêcher le démarrage ni le programme.
- **Entrer dans l'espace.** Tout ce que l'agent lance dans un préfixe (`launch`, `exec-in-prefix`,
  consoles) entre dans `/run/netns/osx86-<préfixe>` par `setns` pendant que l'enfant est encore
  root, avant de devenir l'usager du préfixe ; un échec refuse le lancement. Un préfixe sans
  espace réseau reste dans celui de la VM, où les règles ne laissent aucun usager sortir. La
  session Wine d'un préfixe (`wineserver`, qui ouvre les sockets des programmes, D20) est lancée
  de la même façon : elle est dans l'espace réseau du préfixe.
- **Les règles de l'invité (nftables, en défense, 174 A).** `vm.nft` dans l'espace de la VM :
  chaîne `forward` à politique `drop`, retour des connexions établies accepté, un préfixe (`vx*`)
  ne sort qu'en TCP vers une adresse hors du réseau privé ; vers une adresse du réseau privé
  (autre préfixe, autre VM, passerelle) il reçoit un `reset` ; chaîne `input` : un préfixe
  n'atteint aucun service de la VM (seules les réponses aux connexions de la VM passent) ; UDP 53
  et IPv6 refusés par règle explicite, quel que soit l'émetteur ; chaîne `output` : hors de `lo`,
  seul root émet (un usager hors de tout préfixe n'a aucun réseau, E6 : ni vers `eth0`, ni vers un
  préfixe par son lien ; un préfixe dont l'espace réseau n'a pas pu être fait est dans ce cas). `prefix.nft` dans
  l'espace du préfixe : IPv6 et UDP 53 refusés dès la source. L'usager d'un préfixe n'a aucune
  capacité : il ne change pas ces règles et ne quitte pas son espace
  (`user.max_user_namespaces=0`, pas de `CAP_SYS_ADMIN`).
- **Image.** `linux-base` gagne `iproute2` et `nftables` (image d'outils Debian), les modules
  `veth`, `nf_tables`, `nf_conntrack`, `nft_ct`, `nft_reject`, `nft_reject_inet`, le script
  `net-admin`, les deux fichiers de règles et la sonde statique `osx86-netcheck` (connexion,
  GET HTTP avec débit, écoute, datagramme UDP, essai IPv6). Pack de 76 Mo (73 Mo avant) ; les 22
  contrôles R134.5 tiennent. L'image Wine les reçoit de l'étape `base`, et son étape `slim`
  réinstalle `net-admin` et les règles comme elle le fait pour l'init et l'agent. La version des
  images reste 0.1.0 (aucune n'est encore publiée).

## La passerelle et le relais (174 A, ADR Kazo 0255)

- **Fin du TCP à la passerelle.** Le relais de Kazo parle en flux (`ProxyStreamRequest(Host,
  Port, AppId)`), pas en segments : la passerelle termine elle-même le TCP de l'invité (comme le
  relais de l'essai E6), avec une pile volontairement minimale : pas de mise à l'échelle de
  fenêtre, pas de SACK ni d'horodatage offerts (l'invité n'en utilise donc pas), segments hors
  ordre jetés (l'invité les renvoie), renvoi depuis le premier octet non acquitté après un délai
  (400 ms doublé jusqu'à 8 s), sonde de fenêtre nulle, 256 Kio gardés pour l'invité avant de
  cesser de lire le relais, fenêtre offerte réduite de ce que le relais n'a pas encore pris. ARP
  et ping de `10.86.0.1` sont servis ; rien d'autre n'est servi à cette adresse.
- **Identité par flux.** L'adresse source dit qui parle : `vm:<guid>` pour la VM elle-même (D22),
  `prefix:<guid>:<nom>` pour un préfixe (D20 : une décision par préfixe). C'est l'étiquette que
  l'ADR 0255 fait porter au relais (`identity=`, 128 caractères au plus de `[A-Za-z0-9._:-]`).
  Une adresse qui ne nomme personne dans la marche est abandonnée (`unknown-source`).
- **Ordre d'une connexion.** SYN de l'invité → port permis ? → flux de l'identité et de l'usager
  sous les plafonds ? → décision (règles, puis fenêtre ; les SYN renvoyés pendant l'attente sont
  ignorés) → relais (`IRelayConnector.ConnectAsync` avec l'identité) → SYN-ACK. Tout refus répond
  un `reset` à l'invité (la connexion échoue tout de suite) et s'écrit au journal ; un flux
  refusé n'atteint jamais le relais.
- **UDP et le reste.** UDP (DNS compris) ne sort jamais : abandonné et journalisé (`udp/53 (dns)`
  pour le DNS). Tout refus qui précède un flux (UDP, port, limites, règle de refus, absence de
  décision) n'est écrit qu'une fois par minute et par identité, destination et raison : un
  programme qui réessaie en boucle compte pour une tentative et ne chasse pas les vraies entrées
  du journal. ICMP vers l'extérieur,
  fragments et IPv6 sont abandonnés et comptés. Au jalon 0, une destination est une adresse
  IPv4 (voir les limites).
- **Les relais.** `IRelayConnector` a trois implémentations :
  - `UnavailableRelay`, celle de la Feature aujourd'hui : chaque flux autorisé échoue avec
    `relay-unavailable` (le journal le dit) ;
  - `LocalTestRelay`, pour les tests et l'hôte de développement (`VmHostEnvironment.Relay`) :
    chaque destination qu'il connaît est un service répondu **en mémoire** (son service HTTP
    répond à tout GET, et à `GET /blob?mb=N` par N Mio pour les mesures) ; toute autre
    destination est `unreachable`. Il n'ouvre aucune socket ni aucune requête : une VM derrière
    lui n'a aucun Internet réel ;
  - le relais de Kazo, à écrire quand l'ADR 0255 sera codée (voir plus bas).

## Décisions et règles

- **Une règle** appartient à un usager (D61.1) et vise une identité : un préfixe d'une VM, ou la
  VM elle-même. Elle porte une destination (`any`, un hôte, `hôte:port` ou `*:port`) et une
  action : **autoriser**, **refuser** ou **demander** (ouvrir la fenêtre à chaque fois).
- **Arbitrage.** La règle la plus précise gagne (hôte et port, puis hôte, puis port, puis `any`) ;
  à précision égale, une règle de la session l'emporte sur une règle écrite, puis refuser sur
  demander, et demander sur autoriser. « Autoriser » ne lève jamais les limites (D14).
- **La fenêtre de permission** est derrière `INetworkPermissionPrompt` : la première connexion
  qu'aucune règle ne décide l'appelle, avec l'usager, l'identité, le nom de la VM, l'hôte et le
  port. Réponse : autoriser ou refuser ; pour cette connexion, pour la session (règle gardée en
  mémoire, jamais écrite) ou toujours (règle écrite) ; pour cet hôte et ce port, ou pour toute
  destination. Une réponse « toujours » remplace les règles de même portée (on ne redemande pas
  après « toujours ») ; une réponse « session » l'emporte sur la règle écrite de même portée le
  temps de la session sans jamais l'effacer. Plusieurs connexions qui attendent la même décision
  partagent une seule fenêtre, qui n'appartient à aucune : une connexion qui s'en va (sa VM
  s'arrête) la laisse ouverte pour les autres, et la question est oubliée dès qu'elle a sa
  réponse, si bien qu'une réponse « cette connexion » ne sert jamais à une connexion suivante.
  Une connexion qui n'a pas de décision au bout de 120 s (fenêtre sans réponse, relais muet) est
  refusée (`undecided`) et libère sa place dans les limites ; une fenêtre ou un relais en échec
  refusent aussi, sans jamais laisser la connexion en attente. Implémentations : `UnavailablePermissionPrompt` (pas de fenêtre :
  refus pour cette fois, raison `no-prompt`, rien n'est retenu), celle de la Feature tant que
  l'ADR 0246 n'est pas dans une version publiée de Kazo ; `InjectedPermissionPrompt` (réponses
  données d'avance, demandes gardées) pour les tests et une ligne de commande qui décide avant de
  lancer. Jamais de consentement tapé dans un terminal (D49) : en ligne de commande, l'usager pose
  ses règles avec `x86 rules`, qui est sa propre commande, pas une réponse à une fenêtre qu'un
  programme invité pourrait imiter.
- **Effacement.** Supprimer un préfixe supprime ses règles et termine ses flux ; supprimer une VM
  supprime toutes ses règles : un préfixe ou une VM recréés sous le même nom n'héritent de rien.

## Les limites

`NetworkLimits`, valeurs de l'essai E6 et du relais de Kazo, tenues par la passerelle tant que le
relais de Kazo ne les tient pas par identité :

| Limite | Valeur | Refus au journal |
| --- | --- | --- |
| ports TCP joignables | 80 et 443 | `port` |
| flux à la fois par identité | 4 | `flows-identity` |
| flux à la fois pour l'usager (plafond de l'appareil au relais) | 4 | `flows-user` |
| inactivité avant coupure | 300 s | `idle` (fermeture) |
| attente d'une décision | 120 s | `undecided` |

L'ADR 0255 (réponse 1 A) donnera au relais 2 flux par identité par défaut sous un plafond de 4 par
appareil ; les valeurs d'Os-x86 se règlent par `NetworkLimits` et suivront celles du relais.

## Le journal

Une entrée par décision et par fin de flux (174 A ; ADR 0255 §3 : par identité) : heure, VM,
préfixe (ou la VM), hôte, port, événement (`Opened`, `Closed`, `Refused`), raison, règle qui a
décidé, et pour une fermeture les octets envoyés et reçus et la durée. Raisons : `rule`,
`prompt`, `no-prompt`, `undecided`, `port`, `flows-identity`, `flows-user`, `relay` (détail :
`identity-denied`, `identity-undecided`, `unreachable`, `relay-unavailable`), `udp`, `idle`,
`ended`, `reset`, `vm-stopped`. Écrit au fil de l'eau, dans l'ordre, par une file par usager
(bornée à 4096 entrées en attente : un invité qui inonderait la file perd des entrées, jamais la
mémoire de l'hôte) ;
gardé 7 jours et 1000 entrées au plus (les bornes de la route d'audit de l'ADR 0255). Le journal
ne contient aucun nom d'usager : il est rangé par usager.

## Formats versionnés (plan, section 13)

Rangés par la commande dans le dossier privé de l'app, à côté du registre des VM, un fichier par
identifiant stable d'usager : `~/.config/ca.kazo.x86/network/<guid>.rules.json` et
`<guid>.journal.json`. Un format inconnu est refusé et rien n'est écrit (169) ; un document d'un
autre usager, une règle mal formée ou en double sont refusés.

```json
{"format": 1, "user": "0000…0001", "nextId": 3,
 "rules": [{"id": 1, "vmId": "…", "prefix": "p1", "destination": {"host": "203.0.113.9", "port": 80},
            "action": "Allow", "created": "2026-11-16T10:00:00+00:00"}]}
```

```json
{"format": 1, "user": "0000…0001",
 "entries": [{"time": "…", "vm": "…", "prefix": "p1", "host": "203.0.113.9", "port": 80,
              "event": "Closed", "reason": "ended", "bytesOut": 48, "bytesIn": 110, "durationMs": 39}]}
```

## `x86 rules`

```
x86 rules list [--vm <vm>] [--prefix <p>|--machine]
x86 rules allow|deny|ask <destination> [--vm <vm>] [--prefix <p>|--machine]
x86 rules remove <id>
x86 rules log [--vm <vm>] [--prefix <p>|--machine] [--last <n>]
```

- `<destination>` : `any`, `<hôte>`, `<hôte>:<port>` ou `*:<port>` ; `--prefix` choisit le
  préfixe (par défaut : le préfixe par défaut de la VM), `--machine` la VM elle-même ; sans
  `--vm`, la seule VM de l'usager ou sa seule VM de programmes Windows.
- `allow` rappelle les limites qui tiennent encore. `list` affiche les règles écrites et celles de
  la session ; `log` les 20 dernières entrées du journal (`--last` pour un autre nombre).
- Codes de sortie : 0 ; 2 ligne de commande fausse ; 4 VM, préfixe ou règle inconnus
  (`rule-not-found`) ; 6 hôte sans réseau privé (`network-unavailable`).

Sortie du test `guest` (2026-11-16) :

```
$ x86 rules log
TIME (UTC)           VM              PREFIX            DESTINATION             EVENT    REASON          RULE  DETAIL
2026-11-16 21:00:39  a               p1                203.0.113.9:80          opened   prompt          -
2026-11-16 21:00:39  a               p1                203.0.113.9:80          closed   ended           -     out 48 B, in 110 B, 39 ms
2026-11-16 21:00:39  a               p2                203.0.113.9:80          refused  prompt          -
2026-11-16 21:00:56  a               p1                1.1.1.1:80              refused  relay           1     unreachable
2026-11-16 21:00:57  a               p1                203.0.113.9:80          opened   rule            1
```

## Ce qui attend Kazo

- **ADR 0255 (relais : identités et décisions d'une Feature), pas encore codée.** Il manque, côté
  Kazo : le champ `Identity` de `ProxyStreamRequest` et `KazoTcpConnectOptions.FlowIdentity`
  (réservé aux Features), l'envoi `identity=` par `IProxyClient.OpenStreamAsync` ; les
  enregistrements par identité de `POST /api/proxy/grants` (décision, portée, durée, limites) et
  leur application par `ProxyService` (codes de fermeture `4003 IDENTITY_DENIED` et
  `4004 IDENTITY_UNDECIDED`, limites par identité, UDP 53 et IPv6 refusés pour un flux avec
  identité) ; `PROXY_TCP_CLOSE` avec `appId` et `identity`, et la route `GET /api/proxy/audit`
  (`KazoRelayAudit.ReadAsync`). Ce qu'Os-x86 fera alors : un `KazoRelayConnector` sur
  `RelayedSocket` avec `FlowIdentity = identity.RelayLabel` ; à chaque décision écrite ou de
  session, l'enregistrement par identité au relais (source de vérité, 174 A) ; `4004` traité comme
  « non décidé » (ouvrir la fenêtre puis réessayer), `4003` comme un refus ; le journal local
  complété par l'audit du relais. Les contrats d'Os-x86 n'ont pas à changer : la passerelle donne
  déjà l'identité, la décision et les limites à chaque flux.
- **ADR 0246 (permissions déclarées par une Feature), fusionnée dans la série de Kazo mais pas dans
  une version publiée** (Os-x86 est épinglé sur `v3.4.0`). Il manque : la déclaration de
  `network.real.access` (181 A) avec son approbation par un administrateur, et l'écran sécurisé de
  l'OS pour la fenêtre de permission réseau d'Os-x86 (D23, D49) et pour la page des règles dans
  les Paramètres. Ce qu'Os-x86 fera alors : une implémentation d'`INetworkPermissionPrompt` sur la
  fenêtre de la Feature, installée par l'hôte (`VmHostEnvironment.PermissionPrompt`) à la place
  d'`UnavailablePermissionPrompt`, et la page des règles sur `NetworkRuleBook` et `NetworkJournal`
  (ce que `x86 rules` montre déjà).
- **Sans serveur connecté**, une VM n'a aucun réseau réel (comme toute app ; le réseau simulé de
  Kazo n'est pas touché, 161 A).

## Mesures

Chrome sans tête piloté par Playwright, Ryzen 7 3700X, pack `linux-base` servi en local, relais
de test en mémoire. Les trames passent du worker au processus des tests par Playwright (le code
C# des tests tourne hors du navigateur) : le débit mesuré est une borne basse, pas celui de la
page de Kazo.

| Mesure | Valeur |
| --- | --- |
| démarrage d'une VM `linux-base` jusqu'à son adresse (agent et `vm-up` compris) | 17,3 à 20,7 s (trois séries) |
| GET de 110 octets d'un préfixe (fenêtre injectée, relais, retour) | 39 à 45 ms |
| débit d'un GET de 8 Mio, espace de la VM (sans netns), deux passes à chaud, deux séries | 5,9 à 6,9 Mio/s (passe de chauffe : 3,3 à 3,5) |
| débit d'un GET de 8 Mio, espace d'un préfixe (netns, veth, nftables), deux passes à chaud, deux séries | 5,1 à 6,4 Mio/s (passe de chauffe : 4,9) |
| mémoire de l'invité prise par deux espaces de préfixes (`MemAvailable`) | 1,1 à 1,6 Mio |

Lecture : l'espace réseau d'un préfixe (saut veth, routage, filtrage) coûte 1 à 14 % du débit,
moins que les 15 à 30 % d'E6 sur `localhost` : ici le passage des trames par Playwright pèse plus
lourd et efface une part de l'écart. Le débit reste borné par l'émulation (un cœur) et le pont,
pas par la passerelle. Une VM à deux préfixes coûte le prix d'une VM (E6 : environ 1,45 Gio côté
navigateur contre 2,4 Gio pour deux VM) plus 1,1 à 1,6 Mio dans l'invité.

## Tests

- `unit` (`tests/OsX86.Tests/Network/`) : plan d'adresses et identités ; trames construites et
  relues (sommes de contrôle) ; commutateur (ARP et ping de la passerelle, adresses usurpées,
  IPv6, trames inconnues, VM à VM, préfixe vers une autre VM refusé, adresse Ethernet d'une autre
  VM, usagers séparés, emplacements épuisés) ; passerelle (fenêtre ouverte à la première
  connexion, « autoriser » passe par le relais avec l'identité et le journal enregistre ouverture
  et fermeture, « refuser » remet à zéro sans atteindre le relais, deux préfixes deux décisions,
  « toujours » et « session » deviennent des règles, sans fenêtre, ports, flux par identité et par
  usager, renvoi après perte, coupure d'inactivité à l'horloge manuelle, UDP et DNS, relais de
  test sans Internet, segments sans flux, VM arrêtée et préfixe supprimé, réponse de 1 Mio à
  travers une petite fenêtre) ; règles (destinations, arbitrage, livre des règles, format
  versionné), journal (rétention, borne, format), politique (une fenêtre pour plusieurs
  connexions) ; `x86 rules` (analyse, sortie, codes) ; la carte de la VM sur un moteur et un agent
  simulés (adresse, espace d'un préfixe, règles effacées avec le préfixe et la VM, image sans
  réseau, échec signalé, VM DOS) ; messages `net` du protocole du worker.
- `guest` (`NetworkLinuxBaseGuestTests`, `linux-base` en pack, Chrome sans tête, relais de test,
  fenêtre injectée) : deux VM d'un usager se joignent ; deux préfixes ont des adresses distinctes
  et ne se joignent pas, ni l'autre VM, ni les services de leur VM ; « autoriser » passe,
  « refuser » bloque, le journal enregistre ; les 8 contrôles de confinement d'E6 tiennent pour
  l'usager d'un préfixe (`unshare -n`, `nsenter`, dossier serveur et dossier personnel d'un autre
  préfixe, console série et port de l'agent, socket brute, programme setuid, `/home` en `nosuid`),
  plus UDP 53, IPv6 et un usager hors de tout préfixe ; une destination que le relais de test ne
  sert pas échoue (aucun Internet réel) ; les mesures. Lancé par le job `guest` d'`images.yml`
  avec le pack (son nom contient `LinuxBaseGuestTests`).

## Limites connues et non vérifié

- **Pas de noms d'hôte au jalon 0.** UDP 53 est bloqué (174 A) et Os-x86 ne fournit pas encore de
  `/etc/hosts` : une destination est une adresse IPv4. L'ADR 0255 prévoit que la Feature fournisse
  `/etc/hosts` ; à concevoir avec le relais réel (qui épingle le DNS côté serveur).
- **Pile TCP minimale.** Éprouvée en local (renvoi, fenêtre nulle, 1 Mio en `unit`, 8 Mio en
  `guest`) ; pas sur des pertes ou latences réelles, puisque le relais réel n'existe pas encore.
- **Pas de connexions entrantes** (un service d'un préfixe visible depuis Kazo, §14) : la
  passerelle n'initie rien vers l'invité.
- **Un root compromis dans l'invité** peut usurper l'adresse d'un autre préfixe de la même VM (pas
  celle d'une autre VM : le commutateur la refuse). Le root de l'invité est l'agent ; la frontière
  est la VM, comme dans E6.
- Les trames passent en base64 dans du JSON entre le worker et la page ; suffisant au jalon 0, à
  revoir (transfert d'`ArrayBuffer`) si le débit compte.
- Non vérifié : l'image Wine avec un programme Windows qui ouvre une connexion (la session Wine
  passe par le même lancement, donc par l'espace réseau du préfixe, mais le test `guest` du
  chantier tourne sur `linux-base`) ; Firefox et Safari ; la Feature dans Kazo (ni émulateur, ni
  relais, ni fenêtre avant le chantier 8 et les ADR 0255 et 0246).
