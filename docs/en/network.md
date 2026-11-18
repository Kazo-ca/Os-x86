> [Version française](../fr/reseau.md)

# The private network, prefixes and the relay

Milestone 0, work item 12 (`j0/12-network`). This work item plugs the network card of each VM into
a private Ethernet switch held by Os-x86 on the page side, gives an address to each VM and to each
prefix, puts each prefix in its own network namespace in the guest, and sends TCP connections out
through a gateway that decides, enforces the limits and writes the journal before handing the
flow to a relay ([milestone order](../conception/jalon-0-execution.md)). Decisions: 161 A
(private network between VMs, one address per VM and per prefix, no NAT, no link with Kazo's
simulated network), 174 A (decision, limits and journal at the relay, nftables in the guest in
defence, `relay_url` fixed, UDP 53 and IPv6 blocked by rule), 176 A (permissions), D20, D21, D22,
146 (isolation between the prefixes of a VM). Reference trial: [E6](../essais/e6-reseau.md).

The milestone 0 scenario holds with the test relay: a connection from a prefix to an undecided
destination opens the permission window (an abstraction, see below); "Allow" goes through, "Deny"
blocks, the journal records both.

## Where things are

| Path | Content |
| --- | --- |
| `src/OsX86/Network/` | `Addresses.cs` (`Ipv4Address`, `MacAddress`), `Packets.cs` (Ethernet, ARP, IPv4, TCP, UDP, ICMP, checksums), `AddressPlan.cs` (address plan, `FlowIdentity`), `PrivateSwitch.cs` (switch, `INetworkPort`, drop reasons), `NetworkGateway.cs` and `TcpFlow.cs` (gateway, termination of the guest's TCP), `NetworkRules.cs` (destinations, rules, arbitration, limits), `NetworkRuleBook.cs` (a user's rules and journal), `NetworkPolicy.cs` (rules then window), `NetworkPermission.cs` (abstraction of the window), `Relay.cs` (abstraction of the relay, test relay), `NetworkDocuments.cs` (versioned formats, `INetworkStore`), `NetworkHub.cs` (`NetworkHub`, `UserNetwork`, `VmNetworkAttachment`) |
| `src/OsX86/Engine/` | `INetworkEngine` (the card's frames), `EngineStartOptions.Network`, `net` messages of the worker protocol |
| `src/OsX86.Feature/wwwroot/engine/` | `v86-worker.js`: v86's virtio-net card without `relay_url`, its frames in batches in the `net` messages; `disk.js`: the payload of an image opened shared read-only |
| `src/OsX86/Vm/VmInstance.Network.cs` | the VM on the network: card, address, network namespace of a prefix, switch port |
| `src/OsX86/Agent/AgentClient.Network.cs` | the `net` operation of protocol 3 of the agent |
| `src/OsX86.Feature/X86Command.Rules.cs` | `x86 rules list`, `allow`, `deny`, `ask`, `remove`, `log` |
| `src/OsX86/Storage/UserStateStore.cs` | rules and journal in `osx86/users/<guid>/` of the OPFS (work item 8, 287 A) |
| `images/agent/src/net.c` | the agent's `net` operation; `launch.c` enters the prefix's network namespace |
| `images/linux-base/net-admin.sh`, `net/vm.nft`, `net/prefix.nft` | the guest side: `eth0`, one network namespace per prefix, the nftables rules |
| `images/linux-base/probes/osx86-netcheck.c` | the network probe of the `guest` tests |
| `tests/OsX86.Tests/Network/` | `unit` tests and the `guest` test `NetworkLinuxBaseGuestTests` |

## Overview

```
guest (VM a)                           page (Os-x86)                                  out
┌────────────────────────────┐ frames  ┌──────────────────────────────────┐
│ prefix p1 (netns osx86-p1) │ « net » │ user's switch                    │
│   10.86.2.2 ─ vx0 ─┐       │ ┌─────┐ │  port VM a ─┐                    │
│ prefix p2          ├   root┼>│ v86 │>│  port VM b ─┼─> gateway          │──> IRelayConnector
│   10.86.2.6 ─ vx1 ─┘ eth0  │ └─────┘ │             │   10.86.0.1        │    (Kazo's relay,
│               10.86.0.2    │ worker  │             │   decision,        │     ADR 0255; local
│ nftables in defence        │         │             │   limits, journal  │     test relay)
└────────────────────────────┘         └──────────────────────────────────┘
```

## The network card and the switch (161 A)

- **The card.** When the VM manager has a network (`VmManager.Network`) and the VM is not a DOS
  VM, the engine starts with `EngineStartOptions.Network`: the worker gives v86 a virtio-net card
  **without `relay_url`**. v86 then creates no adapter (no `fetch`, no `inbrowser`, no wisp, no
  WebSocket): frames go only through its bus (`net0-send`, `net0-receive`), which the worker
  relays to the page in `net` messages (base64, in batches at the end of the current task, 64
  frames at most, 1514 bytes at most per frame). The guest reaches only the switch; the network
  mode is chosen by the page alone (E6).
- **The switch.** One switch per user (`UserNetwork`), created on first use: two users never have
  a frame in common. Each running VM is a port; the Os-x86 gateway is another. The switch checks
  each frame against the address plan: a VM sends only from its address or those of its prefixes,
  and has a single Ethernet address, the first its card uses; an Ethernet address belongs to one
  VM while it is plugged in, so a VM can neither take another's address nor cut it off. Between two VMs
  only traffic from one VM to another VM passes (VM addresses on both sides): a prefix never
  reaches another VM (146). An ARP request between VMs goes only to the VM asked. IPv6, the other
  Ethernet types, IPv4 broadcast frames and unknown destinations are dropped and counted
  (`UserNetwork.Drops`).
- **The VM's port.** `EngineNetworkPort` queues the frames for the guest and posts them to the
  engine in batches, in order, outside the switch's lock; the guest's frames go to the switch
  under the lock of the user's network.
- **Two VMs of the same image.** They run together since this work item: `disk.js` opens the
  image's payload (`image.bin`, never written by an engine) in `read-only` mode, which several
  workers can hold at once; the first `guest` trial showed that the exclusive open refused the
  second VM (`layer-locked`). A browser without this mode ignores the option and keeps the
  exclusive open; a writer of the storage worker still finds the file taken.

## The address plan

The same plan for each user (their switches do not see each other):

| Address | Who |
| --- | --- |
| `10.86.0.1` | the Os-x86 gateway (out through the relay), Ethernet address `52:54:00:86:00:01` |
| `10.86.0.S/24` | the VM in slot S (2 to 254), on its `eth0` |
| `10.86.S.4P+1/30` | VM side of the `vxP` link of the prefix in slot P (0 to 63) in VM S |
| `10.86.S.4P+2/30` | the address of the prefix itself, in its network namespace: its **identity** |

Slots are given at run time: a VM's when it starts (the lowest free one), a prefix's at its first
use in a run; they are released when the VM stops or the prefix is deleted. An address names a
VM or a prefix for the length of a run; what lasts is the identity it designates (the VM by its
GUID, the prefix by its name in the VM), and that is what the rules and the journal carry. Neither
NAT nor DHCP: the agent configures `eth0` and each network namespace with the numbers Os-x86 gives
it.

**Choice: allocation at run time rather than addresses stored in the registry.** Nothing to
migrate in the registry format, no collision between two runs, and the identity of a flow stays
that of the prefix even if its address changes from one run to the next.

## One network namespace per prefix (E6, 146)

- **Agent operation rather than an extension of `prefix-copy`** (the choice left to the work
  item): a prefix's address changes from one run to the next and network namespaces live in
  `/run` (lost at shutdown); so they cannot be made once and for all when the prefix is created.
  The agent protocol moves to **version 3** with a `net` operation:
  - `{"op":"net","action":"vm-up","vmSlot":S}`: `eth0` at `10.86.0.S/24`, default route via
    `10.86.0.1`, `ip_forward`, IPv6 off, the VM's rules (`vm.nft`);
  - `{"op":"net","action":"prefix-up","prefix":P,"vmSlot":S,"prefixSlot":N}`: the network
    namespace `osx86-P` (`ip netns`), the veth pair `vxN` / `eth0`, the address `10.86.S.4N+2/30`,
    the default route via the link, IPv6 off, the prefix's rules (`prefix.nft`);
  - `{"op":"net","action":"prefix-down","prefix":P}`: the network namespace removed.

  Like `prefix-ensure`, the agent never runs a shell line: it executes the image's fixed script
  `/usr/lib/osx86/net-admin` (root, as root) with an action it knows, a prefix name and numbers it
  has checked; codes 0 (done), 10 (nothing to do), other (failure, its last line says why).
  Response `{"ok":true,"changed":…,"ms":…}`; errors `no-net-support` (image without `net-admin`,
  like the Alpine test image), `net-failed`, `invalid-request`, `invalid-prefix`. The client still
  accepts agents of versions 1 and 2: they simply have no network.
- **When.** Os-x86 calls `vm-up` as soon as the agent answers at startup, and `prefix-up` at the
  first use of a prefix in the run (with `prefix-ensure`); `prefix-admin delete` removes the
  network namespace along with the prefix's user. A failure leaves the VM or the prefix without a
  network, reported in the background, without preventing the startup or the program.
- **Entering the namespace.** Everything the agent launches in a prefix (`launch`,
  `exec-in-prefix`, consoles) enters `/run/netns/osx86-<prefix>` through `setns` while the child is
  still root, before becoming the prefix's user; a failure refuses the launch. A prefix without a
  network namespace stays in the VM's, where the rules let no user out. The Wine session of a
  prefix (`wineserver`, which opens the programs' sockets, D20) is launched the same way: it is in
  the prefix's network namespace.
- **The guest's rules (nftables, in defence, 174 A).** `vm.nft` in the VM's namespace: `forward`
  chain with a `drop` policy, return of established connections accepted, a prefix (`vx*`) goes
  out only over TCP to an address outside the private network; to an address of the private
  network (another prefix, another VM, the gateway) it gets a `reset`; `input` chain: a prefix
  reaches no service of the VM (only the replies to the VM's connections pass); UDP 53 and IPv6
  refused by explicit rule, whoever the sender; `output` chain: outside `lo`, only root sends (a
  user outside any prefix has no network at all, E6: neither towards `eth0` nor towards a prefix
  through its link; a prefix whose namespace could not be made is in that case). `prefix.nft` in the prefix's namespace: IPv6 and UDP 53
  refused right at the source. A prefix's user has no capability: it does not change these rules
  and does not leave its namespace (`user.max_user_namespaces=0`, no `CAP_SYS_ADMIN`).
- **Image.** `linux-base` gains `iproute2` and `nftables` (Debian tools image), the modules
  `veth`, `nf_tables`, `nf_conntrack`, `nft_ct`, `nft_reject`, `nft_reject_inet`, the `net-admin`
  script, the two rules files and the static probe `osx86-netcheck` (connection, HTTP GET with
  throughput, listening, UDP datagram, IPv6 attempt). Pack of 76 MB (73 MB before); the 22 R134.5
  checks hold. The Wine image receives them from the `base` stage, and its `slim` stage reinstalls
  `net-admin` and the rules as it does for the init and the agent. The image version stays 0.1.0
  (none is published yet).

## The gateway and the relay (174 A, Kazo ADR 0255)

- **TCP terminated at the gateway.** Kazo's relay speaks in streams (`ProxyStreamRequest(Host,
  Port, AppId)`), not in segments: the gateway terminates the guest's TCP itself (like the relay
  of trial E6), with a deliberately minimal stack: no window scaling, no SACK or timestamps offered
  (so the guest uses none), out-of-order segments discarded (the guest resends them), resend from
  the first unacknowledged byte after a timeout (400 ms doubled up to 8 s), zero-window probe,
  256 KiB kept for the guest before it stops reading the relay, offered window reduced by what the
  relay has not yet taken. ARP and ping of `10.86.0.1` are served; nothing else is served at that
  address.
- **Identity per flow.** The source address says who is speaking: `vm:<guid>` for the VM itself
  (D22), `prefix:<guid>:<name>` for a prefix (D20: one decision per prefix). This is the label
  that ADR 0255 has the relay carry (`identity=`, at most 128 characters of `[A-Za-z0-9._:-]`).
  An address that names no one in the run is dropped (`unknown-source`).
- **Order of a connection.** SYN from the guest → port allowed? → flows of the identity and of the
  user under the caps? → decision (rules, then window; SYNs resent during the wait are ignored) →
  relay (`IRelayConnector.ConnectAsync` with the identity) → SYN-ACK. Every refusal answers a
  `reset` to the guest (the connection fails at once) and is written to the journal; a refused
  flow never reaches the relay.
- **UDP and the rest.** UDP (DNS included) never goes out: dropped and journaled (`udp/53 (dns)`
  for DNS). Every refusal that comes before a flow (UDP, port, limits, deny rule, no decision) is
  written only once per minute per identity, destination and reason: a program retrying in a loop
  counts as one attempt and never pushes the real entries out of the journal. ICMP to the outside, fragments and IPv6 are
  dropped and counted. At milestone 0, a destination is an IPv4 address (see the limits).
- **The relays.** `IRelayConnector` has three implementations:
  - `UnavailableRelay`, the Feature's today: every allowed flow fails with `relay-unavailable`
    (the journal says so);
  - `LocalTestRelay`, for the tests and the dev host (`VmHostEnvironment.Relay`): each
    destination it knows is a service answered **in memory** (its HTTP service answers any GET,
    and `GET /blob?mb=N` with N MiB for the measurements); any other destination is
    `unreachable`. It opens no socket and no request: a VM behind it has no real Internet;
  - Kazo's relay, to be written when ADR 0255 is coded (see below).

## Decisions and rules

- **A rule** belongs to a user (D61.1) and targets an identity: a prefix of a VM, or the VM
  itself. It carries a destination (`any`, a host, `host:port` or `*:port`) and an action:
  **allow**, **deny** or **ask** (open the window every time).
- **Arbitration.** The most precise rule wins (host and port, then host, then port, then `any`);
  at equal precision, a session rule prevails over a written one, then deny over ask, and ask over
  allow. "Allow" never lifts the limits (D14).
- **The permission window** is behind `INetworkPermissionPrompt`: the first connection that no
  rule decides calls it, with the user, the identity, the VM name, the host and the port. Answer:
  allow or deny; for this connection, for the session (rule kept in memory, never written) or
  always (rule written); for this host and this port, or for any destination. An "always" answer
  replaces the rules of the same scope (no asking again after "always"); a "session" answer
  prevails over the written rule of the same scope for the session and never erases it. Several
  connections waiting for the same decision share a single window, which belongs to none of them:
  a connection that goes (its VM stops) leaves it open for the others, and the question is
  forgotten as soon as it has its answer, so an answer for "this connection" never serves a later
  one. A connection with no decision after 120 s (window unanswered, relay silent) is refused
  (`undecided`) and frees its place in the limits; a window or a relay that fails also refuses,
  never leaving the connection waiting. Implementations:
  `UnavailablePermissionPrompt` (no window: refusal for this time, reason `no-prompt`, nothing is
  kept), the Feature's as long as ADR 0246 is not in a published version of Kazo;
  `InjectedPermissionPrompt` (answers given in advance, requests kept) for the tests and a command
  line that decides before launching. Never a consent typed in a terminal (D49): on the command
  line, the user sets their rules with `x86 rules`, which is their own command, not an answer to a
  window that a guest program could imitate.
- **Erasure.** Deleting a prefix deletes its rules and ends its flows; deleting a VM deletes all
  its rules: a prefix or a VM created again under the same name inherits nothing.

## The limits

`NetworkLimits`, values from trial E6 and from Kazo's relay, enforced by the gateway as long as
Kazo's relay does not enforce them per identity:

| Limit | Value | Refusal in the journal |
| --- | --- | --- |
| reachable TCP ports | 80 and 443 | `port` |
| flows at once per identity | 4 | `flows-identity` |
| flows at once for the user (device cap at the relay) | 4 | `flows-user` |
| inactivity before cut-off | 300 s | `idle` (close) |
| waiting for a decision | 120 s | `undecided` |

ADR 0255 (answer 1 A) will give the relay 2 flows per identity by default under a cap of 4 per
device; the Os-x86 values are set through `NetworkLimits` and will follow the relay's.

## The journal

One entry per decision and per end of flow (174 A; ADR 0255 §3: per identity): time, VM, prefix
(or the VM), host, port, event (`Opened`, `Closed`, `Refused`), reason, rule that decided, and for
a close the bytes sent and received and the duration. Reasons: `rule`, `prompt`, `no-prompt`,
`undecided`, `port`, `flows-identity`, `flows-user`, `relay` (detail: `identity-denied`, `identity-undecided`,
`unreachable`, `relay-unavailable`), `udp`, `idle`, `ended`, `reset`, `vm-stopped`. Written as it
goes, in order, by one queue per user (bounded to 4096 waiting entries: a guest flooding the
queue loses entries, never the host's memory); kept 7 days and 1000 entries at most (the bounds of the
audit route of ADR 0255). The journal contains no user name: it is stored per user.

## Versioned formats (plan, section 13)

Stored in the OPFS of the Feature, next to the VM registry, in the directory of the user's stable
identifier (work item 8, decision 287 A): `osx86/users/<guid>/network-rules.json` and
`network-journal.json`, written by the storage worker (`UserStateStore`). An unknown format is refused and nothing is written (169); a document of
another user, a malformed or duplicate rule are refused.

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

- `<destination>`: `any`, `<host>`, `<host>:<port>` or `*:<port>`; `--prefix` chooses the prefix
  (by default: the VM's default prefix), `--machine` the VM itself; without `--vm`, the user's
  only VM or their only Windows programs VM.
- `allow` recalls the limits that still hold. `list` shows the written rules and those of the
  session; `log` the last 20 journal entries (`--last` for another number).
- Exit codes: 0; 2 wrong command line; 4 unknown VM, prefix or rule (`rule-not-found`); 6 host
  without a private network (`network-unavailable`).

Output of the `guest` test (2026-11-16):

```
$ x86 rules log
TIME (UTC)           VM              PREFIX            DESTINATION             EVENT    REASON          RULE  DETAIL
2026-11-16 21:00:39  a               p1                203.0.113.9:80          opened   prompt          -
2026-11-16 21:00:39  a               p1                203.0.113.9:80          closed   ended           -     out 48 B, in 110 B, 39 ms
2026-11-16 21:00:39  a               p2                203.0.113.9:80          refused  prompt          -
2026-11-16 21:00:56  a               p1                1.1.1.1:80              refused  relay           1     unreachable
2026-11-16 21:00:57  a               p1                203.0.113.9:80          opened   rule            1
```

## What waits for Kazo

- **ADR 0255 (relay: identities and decisions of a Feature), not yet coded.** Missing on the Kazo
  side: the `Identity` field of `ProxyStreamRequest` and `KazoTcpConnectOptions.FlowIdentity`
  (reserved for Features), the sending of `identity=` by `IProxyClient.OpenStreamAsync`; the
  per-identity records of `POST /api/proxy/grants` (decision, scope, duration, limits) and their
  enforcement by `ProxyService` (close codes `4003 IDENTITY_DENIED` and `4004 IDENTITY_UNDECIDED`,
  per-identity limits, UDP 53 and IPv6 refused for a flow with an identity); `PROXY_TCP_CLOSE`
  with `appId` and `identity`, and the `GET /api/proxy/audit` route (`KazoRelayAudit.ReadAsync`).
  What Os-x86 will do then: a `KazoRelayConnector` on `RelayedSocket` with
  `FlowIdentity = identity.RelayLabel`; on each written or session decision, the per-identity
  record at the relay (source of truth, 174 A); `4004` treated as "undecided" (open the window
  then retry), `4003` as a refusal; the local journal completed by the relay's audit. The Os-x86
  contracts do not have to change: the gateway already gives the identity, the decision and the
  limits to each flow.
- **ADR 0246 (permissions declared by a Feature), merged in Kazo's series but not in a published
  version** (Os-x86 is pinned to `v3.4.0`). Missing: the declaration of `network.real.access`
  (181 A) with its approval by an administrator, and the OS's secure screen for the Os-x86 network
  permission window (D23, D49) and for the rules page in Settings. What Os-x86 will do then: an
  implementation of `INetworkPermissionPrompt` on the Feature's window, installed by the host
  (`VmHostEnvironment.PermissionPrompt`) in place of `UnavailablePermissionPrompt`, and the rules
  page on `NetworkRuleBook` and `NetworkJournal` (what `x86 rules` already shows).
- **Without a connected server**, a VM has no real network (like any app; Kazo's simulated
  network is not touched, 161 A).

## Measurements

Headless Chrome driven by Playwright, Ryzen 7 3700X, `linux-base` pack served locally, in-memory
test relay. The frames go from the worker to the test process through Playwright (the C# code of
the tests runs outside the browser): the measured throughput is a lower bound, not that of Kazo's
page.

| Measurement | Value |
| --- | --- |
| startup of a `linux-base` VM up to its address (agent and `vm-up` included) | 17.3 to 20.7 s (three series) |
| GET of 110 bytes from a prefix (injected window, relay, return) | 39 to 45 ms |
| throughput of an 8 MiB GET, VM namespace (no netns), two warm passes, two series | 5.9 to 6.9 MiB/s (warm-up pass: 3.3 to 3.5) |
| throughput of an 8 MiB GET, prefix namespace (netns, veth, nftables), two warm passes, two series | 5.1 to 6.4 MiB/s (warm-up pass: 4.9) |
| guest memory taken by two prefix namespaces (`MemAvailable`) | 1.1 to 1.6 MiB |

Reading: a prefix's network namespace (veth hop, routing, filtering) costs 1 to 14 % of the
throughput, less than the 15 to 30 % of E6 on `localhost`: here the passage of the frames through
Playwright weighs more and erases part of the gap. The throughput stays bounded by the emulation
(one core) and the bridge, not by the gateway. A VM with two prefixes costs the price of one VM
(E6: about 1.45 GiB on the browser side against 2.4 GiB for two VMs) plus 1.1 to 1.6 MiB in the guest.

## Tests

- `unit` (`tests/OsX86.Tests/Network/`): address plan and identities; frames built and read back
  (checksums); switch (ARP and ping of the gateway, spoofed addresses, IPv6, unknown frames, VM to
  VM, prefix to another VM refused, another VM's Ethernet address, separate users, slots
  exhausted); gateway (window opened at the first connection, "allow" goes through the relay with
  the identity and the journal records open and close, "deny" resets without reaching the relay,
  two prefixes two decisions, "always" and "session" become rules, no window, ports, flows per
  identity and per user, resend after loss, inactivity cut-off on the manual clock, UDP and DNS,
  test relay without Internet, segments without a flow, VM stopped and prefix deleted, 1 MiB
  response through a small window); rules (destinations, arbitration, rule book, versioned
  format), journal (retention, bound, format), policy (one window for several connections);
  `x86 rules` (parsing, output, codes); the VM's card on a simulated engine and agent (address,
  prefix namespace, rules erased with the prefix and the VM, image without network, failure
  reported, DOS VM); `net` messages of the worker protocol.
- `guest` (`NetworkLinuxBaseGuestTests`, `linux-base` as a pack, headless Chrome, test relay,
  injected window): two VMs of a user reach each other; two prefixes have distinct addresses and
  do not reach each other, nor the other VM, nor the services of their VM; "allow" goes through,
  "deny" blocks, the journal records; the 8 confinement checks of E6 hold for a prefix's user
  (`unshare -n`, `nsenter`, server directory and home directory of another prefix, serial console
  and agent port, raw socket, setuid program, `/home` as `nosuid`), plus UDP 53, IPv6 and a user
  outside any prefix; a destination that the test relay does not serve fails (no real Internet);
  the measurements. Run by the `guest` job of `images.yml` with the pack (its name contains
  `LinuxBaseGuestTests`).

## Known limits and not verified

- **No host names at milestone 0.** UDP 53 is blocked (174 A) and Os-x86 does not yet provide an
  `/etc/hosts`: a destination is an IPv4 address. ADR 0255 plans for the Feature to provide
  `/etc/hosts`; to be designed with the real relay (which pins DNS on the server side).
- **Minimal TCP stack.** Proven locally (resend, zero window, 1 MiB in `unit`, 8 MiB in `guest`);
  not on real losses or latencies, since the real relay does not exist yet.
- **No incoming connections** (a prefix's service visible from Kazo, §14): the gateway initiates
  nothing toward the guest.
- **A compromised root in the guest** can spoof the address of another prefix of the same VM (not
  that of another VM: the switch refuses it). The guest's root is the agent; the boundary is the
  VM, as in E6.
- Frames travel as base64 in JSON between the worker and the page; enough for milestone 0, to be
  revisited (`ArrayBuffer` transfer) if throughput matters.
- Not verified: the Wine image with a Windows program that opens a connection (the Wine session
  goes through the same launch, so through the prefix's network namespace, but the work item's
  `guest` test runs on `linux-base`); Firefox and Safari; the Feature in Kazo (no emulator, no
  relay, no window before work item 8 and ADRs 0255 and 0246).
