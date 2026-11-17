# Série 21 — code de l'ADR 0250 (273 à 276)

Posée le 2026-11-17. Les questions viennent du chantier Kazo `feature/19-storage-persistence-code` (PR [Kazo-ca/Os#594](https://github.com/Kazo-ca/Os/pull/594) vers `feature/base`), qui met en œuvre l'ADR 0250 (persistance et éviction du stockage : `KazoStorage`, détection d'éviction, page Stockage des Paramètres, `df`). Os-x86 en dépend pour ses images et ses couches d'écriture (décision 177). Chaque recommandation est déjà dans le code de la PR ; une autre réponse demande un correctif avant la fusion. Rien n'est accepté par défaut.

## 273. Où montrer l'éviction du stockage de Kazo OS ?

Contexte : quand le navigateur a effacé le stockage de Kazo, l'OS redémarre comme neuf ; il faut le dire à l'usager.

- A) ✨(Recommandé) Un avis en tête de l'écran de première installation (date, code `storage.evicted`, restauration d'une sauvegarde), puis l'installation continue.
- B) Une page bloquante dans le style de la page d'erreur de l'ADR 0240, avec un bouton « Continuer ».
- C) Autre : (Spécifiez)

## 274. Avis perdu si l'usager recharge avant de créer son compte

Contexte : le marqueur qui permet de détecter l'éviction est réécrit au démarrage.

- A) ✨(Recommandé) L'accepter : `kern.log` garde la trace `storage.evicted`.
- B) Garder l'avis jusqu'à la création du compte, par un champ « éviction à annoncer » dans le marqueur.
- C) Autre : (Spécifiez)

## 275. À qui va l'événement `Changed(Evicted)` d'une Feature ?

Contexte : Os-x86 doit apprendre que ses images et ses couches ont disparu pour proposer un export ou un nouveau téléchargement.

- A) ✨(Recommandé) Au premier processus de la Feature qui s'abonne (point d'entrée ou service), une fois.
- B) Gardé jusqu'à ce que la Feature l'acquitte, par une nouvelle API `AcknowledgeAsync`.
- C) Autre : (Spécifiez)

## 276. Test de bout en bout de l'éviction

Contexte : simuler une éviction réelle demande d'effacer IndexedDB et l'OPFS entre deux démarrages dans un navigateur piloté.

- A) ✨(Recommandé) Les tests unitaires (détecteur, coordinateur, service, page) et une procédure manuelle suffisent pour ce chantier ; le test de bout en bout vient avec Os-x86 (chantier 8).
- B) L'ajouter maintenant aux tests de bout en bout de l'interface de Kazo.
- C) Autre : (Spécifiez)
