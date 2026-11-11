#!/bin/sh
# Essai E14 : sonde du shell (TERM, taille, couleurs).
echo "TERM=$TERM"; stty size; tput colors; echo PROBE-SHELL-OK
