# Série 30 — Kazo 0254, choix faits à la relecture (299 à 304)

Posée le 2026-11-19 par la session Kazo, sur la PR [Kazo-ca/Os#631](https://github.com/Kazo-ca/Os/pull/631) (code de l'ADR 0254, vers `feature/base`). Ces choix ne sont tranchés ni par l'ADR ni par les séries 28 et 29. Rien n'est accepté par défaut.

## Questions (résumé)

- **299. Limites de débit et de taille** : a) ✨ garder 200 événements par app et par usager en 30 s, 60 relectures en 30 s, genre 128, message 2 048, sujet 256, 16 propriétés (clé 64, valeur 512) ; b) les élargir ; c) les resserrer.
- **300. Où écrire ces décisions** : a) ✨ dans `app-log-files.md` (fr et en) seulement ; b) dans une nouvelle ADR qui complète 0254.
- **301. Genres `kazoos.*`** : a) ✨ réservés à l'OS seul ; b) permis aussi aux apps système.
- **302. Le journal appartient à l'usager (0600)** : une app à `filesystem.system.read` ou `.write` peut le lire ou le modifier. a) ✨ limite documentée ; b) protéger `/var/log/users/` (nouvelle ADR) ; c) limite pour l'instant et issue TODO pour b.
- **303. Écriture depuis un worker** : a) ✨ envoi sans réponse ; b) attendre la réponse de l'OS.
- **304. Auto-fix sur #631** : a) ✨ tu l'actives ; b) tu autorises `mcp__ccd_pr__set_monitor` ; c) pas d'Auto-fix.

## Réponses

Réponse donnée dans la session Kazo. Tes mots : « 302. La garder pour l'instant et ajouter une toto pour b (mais simple, une protection implicite. Ou changer le user authorisé pour système sans que l'utilisateur ne puisse modifier ce fichier) », « 304. b (pas d'auto fix de chatGPT (copilot sur github), seulement toi) » et « pour le reste je suis d'accord avec tes recommendations ».

- 299 A, 300 A, 301 A, 303 A : recommandations acceptées ; le code de #631 ne change pas.
- 302 : **C, avec une précision**. La limite reste ; issue TODO [Kazo-ca/Os#632](https://github.com/Kazo-ca/Os/issues/632) pour une protection simple : l'autoriseur des fichiers refuse toute écriture sous `/var/log/users/` sauf celle de l'OS, ou le fichier appartient à `system` et l'usager garde la lecture seule. Elle demande d'abord une ADR.
- 304 : **B**. La surveillance de la PR passe par l'Auto-fix de l'app (la session Claude), jamais par Copilot. L'Auto-fix était déjà actif sur #631.
