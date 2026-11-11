#!/bin/sh
# Essai E5 — charge snd-sb16 sur la SB16 émulée de v86 (port 0x220, IRQ 5, DMA 1/5, MPU 0x330),
# démute le mélangeur ALSA et imprime MARK snd-ready. Lancé par /etc/local.d au boot, ou à la main.
mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }
if ! grep -q 'Sound Blaster' /proc/asound/cards 2>/dev/null; then
  OPTS="port=0x220 irq=5 dma8=1 dma16=5 mpu_port=0x330"
  modprobe snd-sb16 $OPTS isapnp=0 2>/dev/null || modprobe snd-sb16 $OPTS || echo "SND modprobe failed: $?"
fi
sleep 0.5
echo "SND cards: $(tr -s ' \n' ' ' < /proc/asound/cards 2>/dev/null)"
amixer -q sset Master 100% unmute 2>/dev/null
amixer -q sset PCM 100% unmute 2>/dev/null
amixer -q sset 'Master Mono' 100% unmute 2>/dev/null
if grep -q 'Sound Blaster' /proc/asound/cards 2>/dev/null; then mark snd-ready; else mark snd-failed; fi
