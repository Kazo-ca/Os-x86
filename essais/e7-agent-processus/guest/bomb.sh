#!/bin/sh
# Essai E7 — bombe de processus bornée (R134.5, garantie 8), lancée depuis la console d'une bouteille.
# Le garde-fou est RLIMIT_NPROC posé par l'agent (ulimit -u), pas ce script.
echo "bomb start nproc_limit=$(grep processes /proc/self/limits | awk '{print $4}') uptime=$(cut -d' ' -f1 /proc/uptime)"
b() { b | b & }
b
