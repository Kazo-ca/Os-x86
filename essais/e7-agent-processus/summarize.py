#!/usr/bin/env python3
"""Essai E7 — résumé compact de results-<n>.json (une ligne par mesure, colonnes = runs). Usage : python -I summarize.py 1 2 3"""
import json
import sys

runs = [json.load(open(f"results-{n}.json", encoding="utf8")) for n in sys.argv[1:]]
S = [r["steps"] for r in runs]


def g(s, *keys):
    for k in keys:
        s = s.get(k, {}) if isinstance(s, dict) else {}
    return s if s != {} else None


def row(name, fn):
    vals = []
    for s in S:
        try:
            vals.append(fn(s))
        except Exception as e:  # noqa
            vals.append(f"ERR {e}")
    print(f"{name:52s} | " + " | ".join(str(v) for v in vals))


row("boot: page -> prompt (s)", lambda s: s["boot"]["nav_to_prompt_s"])
row("boot: uptime invité à boot-ready (s)", lambda s: s["boot"]["guest_uptime_s"])
row("boot: agent ready (s)", lambda s: s["boot"]["agent_ready_s"])
row("boot: disque 256 Mio chargé (ms)", lambda s: s["boot"]["disk_load"]["ms"])
row("boot: octets servis (Mo)", lambda s: round(s["boot"]["download"]["bytes"] / 1e6, 1))
row("ping RTT ms min/med/max", lambda s: (s["agent_ping"]["rtt_ms"]["min"], s["agent_ping"]["rtt_ms"]["median"], s["agent_ping"]["rtt_ms"]["max"]))
row("agent RSS ko (ping)", lambda s: s["agent_ping"]["rss_kb"])
row("list-processes à vide ms med/max (n proc)", lambda s: (s["list_processes_idle"]["ms"]["median"], s["list_processes_idle"]["ms"]["max"], s["list_processes_idle"]["count"]))
row("X + twm (s)", lambda s: round(s["x_up"]["ms"] / 1000, 1))
row("mounts RTT ms", lambda s: s["mounts_setup"]["mounts_ms"])
row("A notepad visible (s) / pid lancement comm", lambda s: (s["launch_A_notepad_b1"]["visible_s"], s["launch_A_notepad_b1"]["launch_pid_comm"]))
row("B winemine visible (s)", lambda s: s["launch_B_winemine_b2"]["visible_s"])
row("C xterm visible (s)", lambda s: s["launch_C_xterm_b2"]["visible_s"])
row("list-processes ABC ms med/max (n)", lambda s: (s["processes_with_ABC"]["list_ms"]["median"], s["processes_with_ABC"]["list_ms"]["max"], s["processes_with_ABC"]["count"]))
row("list-windows ABC ms med/max", lambda s: (s["processes_with_ABC"]["windows_ms"]["median"], s["processes_with_ABC"]["windows_ms"]["max"]))
row("b1 procs / b2 procs avec ABC", lambda s: (len(s["processes_with_ABC"]["b1"]), len(s["processes_with_ABC"]["b2"])))
for sc in ["pid", "group", "cgroup"]:
    row(f"arbre kill {sc}: survivants / ms", lambda s, sc=sc: (len(s["tree_kill"][sc]["survivors"]), s["tree_kill"][sc]["kill"]["ms"], s["tree_kill"][sc]["kill"]["count"]))
row("console: prompt ms", lambda s: s["console_open"]["prompt_ms"])
row("R134.5: held / leak / durée s", lambda s: (s["console_checks_R134_5"]["held"], s["console_checks_R134_5"]["leak"], round(s["console_checks_R134_5"]["ms"] / 1000, 1)))
row("console: winecfg visible (s)", lambda s: s["console_graphical_and_daemon"]["winecfg_visible_s"])
row("close-console SIGHUP: shell gone/daemon still/winecfg still", lambda s: (s["console_close"]["shell_gone"], s["console_close"]["daemon_sleep_still"], s["console_close"]["winecfg_window_still"]))
row("cgroup.kill console: count/ms, puis daemon/winecfg/B/C", lambda s: (s["console_close"]["cgroup_kill_of_console"]["count"], s["console_close"]["cgroup_kill_of_console"]["ms"], s["console_close"]["after_cgroup_kill"]["daemon_still"], s["console_close"]["after_cgroup_kill"]["winecfg_window_still"], s["console_close"]["after_cgroup_kill"]["B_window_still"], s["console_close"]["after_cgroup_kill"]["C_window_still"]))
row("bombe: pings pendant (ms)", lambda s: s["fork_bomb"]["pings_during_ms"])
row("bombe: b2/total procs pendant", lambda s: [(c["b2"], c["total"]) for c in s["fork_bomb"]["counts_during"]])
row("bombe: list-processes ms pendant", lambda s: [c["list_ms"] for c in s["fork_bomb"]["counts_during"]])
row("bombe: b1 vivant (n) / fenêtres", lambda s: (len(s["fork_bomb"]["b1_alive_during"]), s["fork_bomb"]["windows_during"]))
row("bombe: cgroup.kill count/ms ; b2 après ; B fenêtre", lambda s: (s["fork_bomb"]["kill_console_cgroup"]["count"], s["fork_bomb"]["kill_console_cgroup"]["ms"], len(s["fork_bomb"]["b2_after"]), s["fork_bomb"]["B_window_after"]))
row("partage disparu: mounts état/errno/ms ; détection ms", lambda s: (s["mount_gone"]["mounts_during"]["state"], s["mount_gone"]["mounts_during"]["statfs_errno"], s["mount_gone"]["mounts_during"]["ms"], s["mount_gone"]["detect_ms"]))
row("partage disparu: cat cache/frais/ls (rc, ms)", lambda s: (s["mount_gone"]["cached_read"]["exit"], s["mount_gone"]["cached_read"]["ms"], s["mount_gone"]["fresh_read"]["exit"], s["mount_gone"]["fresh_read"]["ms"], s["mount_gone"]["ls"]["exit"], s["mount_gone"]["ls"]["ms"]))
row("partage disparu: EIO envoyés ; après retour rc", lambda s: (s["mount_gone"]["eio_replies"], s["mount_gone"]["after_restore"]["exit"]))
row("disque: find agent ms (n) ; wine cmd ms", lambda s: (s["disk"]["agent_find_ms"], s["disk"]["agent_find_count"], s["disk"]["wine_cmd"]["ms"]))
row("disque: export 256 Mio ms ; sync ms ; umount ms", lambda s: (s["disk"]["export"]["first_export_ms"], s["disk"]["sync_ms"], s["disk"]["umount_ms"]))
def norm(xs):  # « root » côté invité = uid 0 côté hôte
    return sorted(x.replace(" root ", " 0 ") for x in xs)


def consistent(s, k):
    return norm(s["disk"][k]["files"]) == norm(s["disk"]["guest_files"])


row("disque: hôte dirty/synced/umount cohérent (ms lecture)", lambda s: ((consistent(s, "host_dirty"), s["disk"]["host_dirty"]["ms"]), (consistent(s, "host_synced"), s["disk"]["host_synced"]["ms"]), (consistent(s, "host_unmounted"), s["disk"]["host_unmounted"]["ms"])))
row("disque: manquant côté hôte avant sync", lambda s: [x for x in norm(s["disk"]["guest_files"]) if x not in norm(s["disk"]["host_dirty"]["files"])])
row("open-url: événement (prefix,uid)", lambda s: [(e["prefix"], e["uid"]) for e in s["open_url"]["events"]])
row("kill A: RTT ms / proc gone ms / fenêtre gone ms", lambda s: (s["kill_A"]["kill"]["rtt_ms"], s["kill_A"]["process_gone_ms"], s["kill_A"]["window_gone_ms"]))
row("kill A: B proc / B fen / C fen / VM ping ms / b1 restant", lambda s: (s["kill_A"]["B_process_still"], s["kill_A"]["B_window_still"], s["kill_A"]["C_window_still"], s["kill_A"]["vm_ping_ms"], len(s["kill_A"]["b1_remaining"])))
row("kill A: exits reçus (pid,sig)", lambda s: [(e["pid"], e["signal"]) for e in s["kill_A"]["exit_events"]][:4])
row("kill bouteille b1: ms ; b1/b2 restants ; fenêtres", lambda s: (s["kill_bottle_b1"]["kill"]["ms"], len(s["kill_bottle_b1"]["b1_remaining"]), len(s["kill_bottle_b1"]["b2_remaining"]), s["kill_bottle_b1"]["windows"]))
row("mémoire: renderer privé Mio / total Chrome", lambda s: (s["memory"]["browser"]["biggest_renderer_private_MiB"], s["memory"]["browser"]["total_private_MiB"]))
row("mémoire invité utilisée ko", lambda s: s["memory"]["guest"][0].split()[2])
row("RTT toutes ops (op: med/max)", lambda s: [(r["op"], r["median"], r["max"]) for r in s["memory"]["rtt_all_ops"]])
print("erreurs :", [r["errors"] for r in runs])
