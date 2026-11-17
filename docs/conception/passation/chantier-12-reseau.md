# Chantier 12 (j0/12-network) : note de passation (2026-11-16)

PR #16 (`j0/12-network` → `j0/base`), fusionnée. Référence : `docs/fr/reseau.md` (miroir `docs/en/network.md`).

## Ce que la suite peut prendre tel quel

- `src/OsX86/Network/` : `NetworkHub` (un par `VmManager`, paramètre `network:` du constructeur ; la Feature en crée un dans `X86Runtime`), `UserNetwork` (commutateur, passerelle, règles, journal, politique d'un usager), `VmNetworkAttachment` (emplacement de la VM, emplacements des préfixes).
- Abstractions à brancher plus tard : `IRelayConnector` (relais de Kazo quand l'ADR 0255 est codée : `FlowIdentity.RelayLabel` = `vm:<guid>` ou `prefix:<guid>:<nom>`), `INetworkPermissionPrompt` (fenêtre de la Feature quand l'ADR 0246 est dans une version publiée). L'hôte les donne par `VmHostEnvironment.Relay` et `.PermissionPrompt` ; l'hôte de développement installe `LocalTestRelay` (203.0.113.9:80 en mémoire).
- Agent protocole 3 : opération `net` (`vm-up`, `prefix-up`, `prefix-down`) qui lance `/usr/lib/osx86/net-admin` ; tout lancement dans un préfixe entre dans `/run/netns/osx86-<préfixe>` (setns avant setuid).
- `VmInstance.ExecInPrefixAsync` (interne) : exécuter une commande comme l'usager d'un préfixe, dans son espace réseau.
- `x86 rules list|allow|deny|ask|remove|log` ; règles et journal versionnés (format 1) dans `~/.config/ca.kazo.x86/network/`.

## Choix faits

- Netns par une opération de l'agent plutôt que par `prefix-copy` : les adresses sont données à l'exécution (emplacements libérés à l'arrêt) et les espaces réseau vivent dans `/run`.
- Le TCP de l'invité se termine à la passerelle côté page (le relais de Kazo parle en flux) ; pile minimale, sans mise à l'échelle de fenêtre ni SACK.
- Limites d'E6 tenues par la passerelle (80 et 443, 4 flux par identité et par usager, 300 s d'inactivité, 120 s pour une décision) en attendant le relais réel.
- Une réponse « session » l'emporte sur la règle écrite de même portée sans l'effacer ; « toujours » la remplace.
- Versions des images inchangées (0.1.0), comme au chantier 10 : rien n'est publié.

## Pièges trouvés

- **Deux VM de la même image ne démarraient pas ensemble** : `disk.js` ouvrait `image.bin` par un `SyncAccessHandle` exclusif (`layer-locked: already open by another engine`). Corrigé : mode `read-only` partagé pour la charge utile d'une image (Chrome 121+ ; sinon comportement d'avant).
- `linux-base` n'a ni `nc` ni `curl` ni `perl` (retiré par `slim.sh`) : la sonde statique `/usr/lib/osx86/osx86-netcheck` sert aux tests.
- Chaîne `input` de `vm.nft` : sans `ct state established,related accept` en tête, la racine de la VM ne joint pas un préfixe.
- Les tests qui prenaient la version 3 du protocole comme « inconnue » passent à 4.
- `guest-wine` (« the typed line did not reach the program ») échoue par intermittence depuis la fusion de #15, aussi sur `j0/base` : sans rapport avec le réseau.
- La relecture du code a corrigé : décisions qui pouvaient rester en attente et occuper les limites, réponse « une fois » réutilisable, règle écrite effacée par une réponse de session, journal inondable, derniers octets de l'invité perdus, `net-admin vm-up` qui rendait 1 au lieu de 10, préfixe sans netns qui joignait les autres, adresse Ethernet d'une VM prenable par une autre.

## Mesures (Chrome sans tête, pont Playwright)

VM `linux-base` jusqu'à son adresse 17 à 21 s ; GET de 110 o d'un préfixe 39 à 45 ms ; 8 Mio : 5,9 à 6,9 Mio/s par l'espace de la VM, 5,1 à 6,4 par celui d'un préfixe (1 à 14 %) ; deux espaces de préfixes 1,1 à 1,6 Mio dans l'invité.

## Non vérifié

Un programme Windows qui ouvre une connexion dans l'image Wine ; Firefox et Safari ; la Feature dans Kazo (ni émulateur, ni relais, ni fenêtre avant le chantier 8 et les ADR 0255 et 0246) ; l'hôte de développement à la main.

## Questions ouvertes

- Passer `linux-base` et `wine` en 0.2.0 avant leur première publication ?
- Déclarer `network.real.access` dans la Feature dès maintenant, ou avec le relais réel ?
- Les noms d'hôte (`/etc/hosts` fourni par la Feature, ADR 0255) : à concevoir avec le relais réel.
