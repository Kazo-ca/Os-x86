# Série 25 — chantier 8, la Feature x86 sur Kazo 3.5.0 (287 à 290)

Posée le 2026-11-17. Questions du chantier 8 d'Os-x86 (branche `j0/8-feature`, PR vers `j0/base`). Elles bloquent la persistance et le moteur de la Feature. Rien n'est accepté par défaut.

Ce que révèle Kazo 3.5.0 :

- L'enfant `ca.kazo.x86.cli` a son propre dossier privé, `~/.config/ca.kazo.x86.cli/`.
- Le service `Ix86` tourne sous `kazoos-svc`, ou pour l'app qui l'appelle. Il n'atteint donc pas `~/.config/ca.kazo.x86/` de l'usager.
- Le constructeur d'un service de Feature ne reçoit pas d'`IJSRuntime`. Or le moteur v86 et l'OPFS en ont besoin.

## 287. Où ranger l'état de chaque usager (registre des VM, profils, règles et journal réseau) ?

- A) ✨(Recommandé) Dans l'OPFS, sous `osx86/users/<GUID>/`, à côté des disques. L'isolation entre usagers est tenue par Os-x86. Une éviction efface le registre avec les disques.
- B) Garder `~/.config`. Il faut une ADR Kazo (dossier privé partagé avec les enfants, service qui agit pour l'usager), et le chantier attend Kazo.
- C) Tout sous l'identité de la Feature (`kazoos-svc`), avec un contrat interne pour la commande.
- D) Autre : (Spécifiez)

## 288. Qui installe le moteur et le stockage, qui exigent `IJSRuntime` ?

- A) ✨(Recommandé) Un enfant de type Service, `ca.kazo.x86.host` : caché, démarré à l'ouverture de session, sur le fil principal, avec `IJSRuntime` dans son constructeur. La commande `x86` fait la même installation si elle manque.
- B) La commande seule : `Ix86` ne démarre une VM qu'après un premier `x86` tapé dans la session.
- C) Réécrire les ponts sur `[JSImport]` et `JSHost`, sans `IJSRuntime`.
- D) Une ADR Kazo pour donner `IJSRuntime` aux services de Feature, et le chantier attend Kazo.
- E) Autre : (Spécifiez)

## 289. Que faire quand le navigateur efface le stockage ?

Dans tous les cas : persistance demandée au premier `x86 vm create`, marqueur `osx86/.kazoos-marker`, place occupée déclarée.

- A) ✨(Recommandé) En plus de la notification de Kazo, le prochain `x86` avertit une fois de ce qui a été perdu, et `GetVmsAsync` rend une liste vide. Même chose, une fois, si le navigateur retire la protection.
- B) Seulement la notification de Kazo.
- C) A, plus un événement public `StorageEvicted` dans `Ix86` (change l'API).
- D) Autre : (Spécifiez)

## 290. Comment le paquet de la Feature obtient-il v86 et les workers JS ?

Contexte : KazoOsPack (ADR Kazo 0221) n'emballe que les ressources web du projet de l'app, `OsX86.Feature`, pas celles des projets référencés. Une app installée n'est servie qu'à `_content/<son assemblage>/` (ADR 0206). Or v86 et les workers sont dans `src/OsX86/wwwroot`, servis à `_content/OsX86/`. Le paquet construit ne contient que les trois DLL et les deux manifestes. Une fois installée, la Feature n'aurait donc ni moteur ni stockage.

- A) ✨(Recommandé) `OsX86.Feature` passe au SDK Razor et déclare les fichiers de `src/OsX86/wwwroot` comme ses propres ressources par `DefineStaticWebAssets`, comme l'hôte de dev le fait déjà pour les images. Aucun fichier ne bouge. Ils sont servis à `_content/OsX86.Feature/`, et la Feature passe ce chemin aux ponts JS. Le paquet gagne environ 2,6 Mo.
- B) Déplacer `wwwroot` de `src/OsX86` vers `src/OsX86.Feature`. C'est plus net, mais cela touche environ 25 fichiers (vendorisation de v86, tests `guest`, workflows, docs).
- C) Demander à Kazo une ADR qui emballe aussi le `wwwroot` des projets référencés, et le chantier attend Kazo.
- D) Autre : (Spécifiez)
