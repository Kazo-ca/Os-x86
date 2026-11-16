# Chantier 11 (DOS dans le terminal) — note de passation

PR : https://github.com/Kazo-ca/Os-x86/pull/15 (j0/11-dos → j0/base).

## Choix faits
- VM DOS : profil livré `dos` (freedos 0.2.0, 64 Mio, sans agent ni série). Une VM DOS = un programme par
  démarrage (le disque du programme ne se branche qu'au démarrage) ; `x86 run` prend la plus ancienne VM DOS
  arrêtée ou en crée une (`dos`, `dos-2`) ; arrêt immédiat à la fin (pas de délai de grâce).
- Programme sur un second disque `hdb` fait en mémoire (MBR + FAT12, source `{inline}` de disk.js),
  pas dans la couche de `hda`. `FDAUTO.BAT` (image bumpée à 0.2.0) appelle `D:\OSX86RUN.BAT`.
- Fin sans agent : le lot écrit `OSX86-START` / `OSX86-EXIT %ERRORLEVEL%` sur COM1 ; `PAUSE >NUL` fige l'écran.
- Écran : `vga-text.js` seul lecteur des champs internes de v86 (218 A) ; SGR 24 bits dans les lignes de
  `TerminalScreenFrame` (le Terminal 3.4.0 les rend, comme htop) ; `IGuestScreenTerminal` = frontière ADR 0252.
- Accord Échap : lettre = Alt, 1..0 = F1..F10, `-`/`=` = F11/F12 (proposition), Échap seul après 800 ms.
- Programmes de test : NASM 3.01 (alpine `nasm=3.01-r0`) dans `osx86-nasm:3.01`, artefact `freedos-test-programs`.

## Mesures
Touche → écran médiane 48 ms, p90 50-52, max 65 ; FreeDOS jusqu'au programme 2,5-2,9 s ; lecture d'écran ~2 ms.

## Trouvé en route (pour d'autres chantiers)
- `x86` ne déclare que `filesystem.private.*` : dans Kazo, `x86 run ~/prog.exe` répond « no file » (Windows
  aussi, chantier 10). À traiter avec les permissions (chantier 8).
- `curl`/`wget` de Kazo vont au réseau simulé : pour essayer dans l'hôte de dev, envoyer le fichier par le
  bouton Upload de l'Explorateur (InputFile `#fileInput`), puis le copier dans `~/.config/ca.kazo.x86/`.
- Le Ctrl+C de l'outil d'automatisation du navigateur n'atteint pas la page ; envoyé à xterm.js par un
  KeyboardEvent, il passe.
- Le `cp` de Kazo avec une destination en `dossier/` répond `AlreadyExists` (nommer le fichier cible).
