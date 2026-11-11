#!/bin/sh
# Essai E5 — scénarios de son côté invité. Imprime des "MARK <nom> <uptime>" horodatés par la page.
# Usage : sh /root/sound.sh aplay [device]   -> joue /root/tone.wav avec aplay (défaut plughw:0,0)
#         sh /root/sound.sh pipewire         -> PipeWire + WirePlumber, deux flux, muet d'un seul flux
#         sh /root/sound.sh cpu <secondes>   -> charge CPU dans l'invité (pour "VM sous charge")
mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }
WAV=/root/tone.wav

case "$1" in
aplay)
  DEV="${2:-plughw:0,0}"
  sh /root/snd-init.sh >/dev/null 2>&1
  mark aplay-start
  aplay -q -D "$DEV" "$WAV" 2>/root/aplay.err; rc=$?
  mark aplay-done
  echo "APLAY rc=$rc $(tr '\n' ' ' < /root/aplay.err)"
  ;;
pipewire)
  sh /root/snd-init.sh >/dev/null 2>&1
  export XDG_RUNTIME_DIR=/tmp/xdg; mkdir -p "$XDG_RUNTIME_DIR"
  export PIPEWIRE_LOG_LEVEL=2
  mark pw-start
  pipewire >/root/pipewire.log 2>&1 &
  sleep 2
  wireplumber >/root/wireplumber.log 2>&1 &
  i=0
  while ! wpctl status 2>/dev/null | grep -q 'Sinks:'; do sleep 0.5; i=$((i+1)); [ $i -gt 60 ] && break; done
  echo "PW status: $(wpctl status 2>&1 | grep -A3 'Sinks:' | tr -s ' \n' ' ')"
  if ! wpctl status 2>/dev/null | grep -A2 'Sinks:' | grep -qi 'sb16\|sound blaster\|alsa'; then mark pw-failed; exit 1; fi
  mark pw-ready
  # Deux programmes qui jouent en même temps (flux A : tone.wav, flux B : tone.wav aussi, en boucle courte).
  pw-play --media-role Music -P '{ node.name = "fluxA" }' "$WAV" 2>/root/pwA.err &
  PA=$!
  pw-play --media-role Music -P '{ node.name = "fluxB" }' "$WAV" 2>/root/pwB.err &
  PB=$!
  sleep 2
  mark pw-two-streams
  # Muet par programme : on coupe le flux B seulement (le canal gauche garde l'escalier du flux A + B, le RMS global baisse).
  IDB=$(wpctl status 2>/dev/null | awk '/Streams:/{s=1} s && /fluxB/{gsub(/\./,"",$1); print $1; exit}')
  [ -z "$IDB" ] && IDB=$(pw-cli ls Node 2>/dev/null | awk '/id [0-9]+/{id=$2} /node.name = "fluxB"/{gsub(/,/,"",id); print id; exit}')
  echo "PW fluxB id=$IDB"
  if [ -n "$IDB" ]; then wpctl set-mute "$IDB" 1 && mark pw-muted-B; else mark pw-mute-failed; fi
  sleep 3
  [ -n "$IDB" ] && wpctl set-mute "$IDB" 0 && mark pw-unmuted-B
  wait $PA $PB
  mark pw-done
  echo "PW errA: $(tr '\n' ' ' < /root/pwA.err) errB: $(tr '\n' ' ' < /root/pwB.err)"
  ;;
cpu)
  S="${2:-10}"
  mark cpu-start
  end=$(( $(cut -d. -f1 /proc/uptime) + S ))
  while [ "$(cut -d. -f1 /proc/uptime)" -lt "$end" ]; do :; done
  mark cpu-done
  ;;
*)
  echo "usage: sound.sh aplay|pipewire|cpu"; exit 2 ;;
esac
