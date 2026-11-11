# Essai E2 — stockage des disques (code jetable)

Banc OPFS (`createSyncAccessHandle` dans un Web Worker) contre IndexedDB (morceaux de 256 Kio) sur une image de 1 Gio. Résultats commentés dans `docs/essais/e2-stockage.md`.

Pour relancer : `npm install`, puis `npx playwright install firefox chromium` (navigateurs Playwright, ~700 Mio), puis `node run.mjs` (3 passes, 1 Gio, 2000 opérations ; options `--runs`, `--size` en Mio, `--ops`, `--browsers chrome,msedge,chromium,firefox`, `--headed`). Les JSON bruts vont dans `resultats/`, la médiane des passes dans `resultats/summary.json`. Chaque passe nettoie son fichier et sa base, et supprime son profil temporaire.
