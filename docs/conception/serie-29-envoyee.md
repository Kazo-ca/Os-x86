# Série 29 — Kazo 0254, événements par usager (297 et 298)

Posée le 2026-11-18 par la session Kazo, qui écrit le code de l'ADR 0254 (branche `feature/22-per-user-events-code`, 296 A). Rien n'est accepté par défaut.

## 297. Événement « opération sensible d'une Feature authentifiée »

L'ADR 0254 §2 veut que le sujet soit ce que la Feature a transmis. Aujourd'hui, `KazoFeatureAuthentication.AuthenticateAsync(operation, scope)` ne reçoit aucun sujet.

- A) ✨(Recommandé) Ajouter la surcharge `AuthenticateAsync(operation, scope, KazoEventSubject? subject)` ; l'OS écrit l'événement de l'usager quand le billet est consommé (`ConsumeAsync`), avec ce sujet. Ajout au SDK public (`PublicAPI.Unshipped`).
- B) Pas de surcharge : l'OS écrit l'événement avec la Feature comme sujet (type « app », id de la Feature) ; la Feature écrit en plus son propre événement avec `KazoEvents.Write` (ADR 0247).
- C) L'OS n'écrit pas cet événement dans ce chantier ; issue TODO.
- D) Autre : (Spécifiez)

## 298. Un administrateur lit le journal d'un autre usager

L'ADR 0254 §3 exige l'authentification de l'OS, un billet par session de la visionneuse et une entrée `events.read-other user=` dans `auth.log`. Où mettre l'API dont l'Error Log Viewer a besoin ?

- A) ✨(Recommandé) `KazoUserAccounts.ReadEventsAsync(login, filtre)`, à côté des autres opérations d'administration des comptes ; même fenêtre d'authentification de l'OS (`AdministratorGate`) une fois par processus de la visionneuse, billet valable jusqu'à sa fermeture. Toute app peut l'appeler, l'authentification d'un administrateur est exigée.
- B) La même méthode, réservée aux apps système en plus de l'authentification.
- C) Pas de nouvelle API : lecture du fichier avec l'approbation ponctuelle actuelle (ADR 0042), une approbation par fichier, pas d'entrée `events.read-other` propre.
- D) Autre : (Spécifiez)

## Réponses

Réponse du 2026-11-18. Tes mots : « 297. a » et « 298. a ».

- 297 : **A**. Surcharge `AuthenticateAsync(operation, scope, KazoEventSubject? subject)` ; l'OS écrit l'événement à la consommation du billet.
- 298 : **A**. `KazoUserAccounts.ReadEventsAsync(login, filtre)`, authentification d'administrateur une fois par processus de la visionneuse.
