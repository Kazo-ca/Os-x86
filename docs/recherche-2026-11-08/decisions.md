KEY CONSTRAINTS:
- Une app installée depuis le Store doit être Execution=MainThread: une boucle d'émulation synchrone gèlerait tout le bureau. Le moteur doit donc tourner dans un Web Worker JS lancé depuis le wwwroot de l'app (AppPackageInstaller.cs:639-645; ADR 0206).
- Kazo n'a ni AOT ni threads .NET (ADR 0106, 0131): le code C# chargé dynamiquement tourne dans l'interpréteur IL de Mono. Le multithreading de Blazor reste expérimental en .NET 10 (dotnet/runtime#129900, dotnet/aspnetcore#54365).
- Tout ce qui est intégré au noyau est précaché par le service worker publié (.dll, .wasm, .js; service-worker.published.js:37): chaque usager en paierait le téléchargement.
- Un paquet .kazoapp est limité à 64 Mio d'archive, 128 Mio extrait et 2000 fichiers (AppPackage.cs:62-68). Les assemblies propres d'une app ne sont pas encore empaquetées (#419), et je n'ai pas trouvé de résolution automatique des dépendances (confiance moyenne).
- VFS: 16 Mio au plus par fichier, écriture du fichier entier seulement (pas d'écriture aléatoire), et les apps ne peuvent pas enregistrer leur propre système de fichiers (FileContentStoragePolicy.cs:15-18; KazoFileStream.cs:93-130; ADR 0181).
- L'isolation cross-origin est active par défaut (KazoOs:CrossOriginIsolation=auto, CrossOriginIsolation.cs:53), donc SharedArrayBuffer est disponible. Exceptions: localhost et 127.0.0.1 (utiliser 127.0.0.2 en développement). COEP credentialless empêche de lire une ressource d'une autre origine sans CORS, et les fichiers des GitHub Releases n'ont pas de CORS.
- Le réseau réel demande la permission dangereuse network.real.access et le serveur optionnel connecté. Par défaut, le relais autorise TCP 80/443 et UDP 53/123, 4 flux et 8 requêtes simultanées par appareil, et bloque les réseaux privés (ProxyOptions.cs:160-166, 233-234). Règle: aucune fonctionnalité ne doit dépendre du serveur. Une connexion TCP brute vers un hôte simulé n'est pas supportée (#393). Aucune CSP trouvée: par déduction, le JS d'une app peut contourner la passerelle du SDK.
- Le shell hsh refuse les binaires (un octet NUL donne le code de sortie 126). Il n'existe aucun binfmt ni chargeur ELF/PE (ShellInterpreter.Scripts.cs:67-120).
- v86 (BSD-2, JIT x86 vers wasm, environ 2,0 Mo de wasm + 360 Ko de JS) est 32-bit seulement: pas de noyau 64-bit, pas de multicœur. Windows 9x/2000 fonctionnent; XP, Vista et 8 sous conditions; 7, 10 et 11 ne sont pas listés. Le projet refuse les contributions écrites par une IA générative (Readme de copy/v86).
- Personne n'a montré publiquement Windows 7, 10 ou 11 x64 dans un onglet (recherche du 2026-11-08; absence de preuve). QEMU amont ne fait tourner les invités 64-bit qu'avec son interpréteur TCI sur wasm64 (QEMU 11.0). Le JIT wasm n'existe que dans le fork ktock/qemu-wasm (master inactif depuis le 2025-09-04, wasm32, 2300 Mo), et la série TCG v4 n'est pas fusionnée.
- Mémoire: wasm32 plafonne à 4 Gio. Memory64 monte à 16 Gio mais coûte de 10 % à plus de 100 % en vitesse; il est dans Chrome 133 et Firefox 134, et dans Safari seulement derrière un drapeau (features.json du 2026-11-02).
- Windows 11 exige un CPU 64-bit à 2 cœurs, 4 Go de RAM, 64 Go de disque, UEFI avec Secure Boot et TPM 2.0 (learn.microsoft.com, 2026-07-21); 24H2 exige aussi SSE4.2/POPCNT (presse de 2024, confiance moyenne). Windows 10 x64 exige PAE, NX, SSE2, CMPXCHG16b, LAHF/SAHF et PrefetchW, 2 Go de RAM et 32 Go de disque.
- Licences: Kazo OS est sous CC BY-NC 4.0 (LICENSE.md), une licence non libre et incompatible avec la GPL selon le projet GNU; le SDK NuGet est livré avec ce LICENSE.md. Os-x86 est sous MIT. Du code GPL (Boxedwine, QEMU, js-dos, halfix) ne peut pas être combiné avec du code CC BY-NC, ni intégré au dépôt MIT sans rendre l'ensemble GPL; l'Apache-2.0 est incompatible avec la GPLv2. Ce n'est pas un avis juridique.
- Les médias et licences Windows ne sont pas redistribuables, et il faut une licence par instance, physique ou virtuelle (conditions OEM de Windows 11). ReactOS (GPL-2, i386) est le seul invité compatible Windows qu'on peut redistribuer.
- Stockage du navigateur: le quota par origine est partagé avec l'IndexedDB de Kazo, qui réserve 5 % (au plus 256 Mio, ADR 0099). Une éviction efface toute l'origine. Firefox en mode best-effort se limite à 10 Gio, et Safari efface après 7 jours sans interaction. Le SyncAccessHandle de l'OPFS ne fonctionne que dans un worker dédié.
- Conventions de Kazo: l'ADR est fusionné avant le code (prochain numéro libre: 0241), la documentation est en français d'abord avec un miroir en anglais, features.md et changes/unreleased sont mis à jour à chaque PR, et seul Benoit fusionne dans main. L'ADR 0135 interdit toute mention de jeux.
- MsDos.Clone (.NET 8) émule un 8086 avec un 386 partiel: le préfixe 0x67 n'est jamais lu, et il n'y a ni mode protégé, ni pagination, ni exceptions. Sa mémoire est un bloc plat de 1 Mo, son rendu est en texte seulement, et il n'a aucune mesure de performance. Son dépôt fait 2,8 Go à cause des données de test.

## [1] Vision, périmètre et gouvernance
Q: 1. Quand tu dis « exécuter des .exe dans Kazo OS » et « installer un sous-système Linux et Windows », quel modèle vises-tu?
  CTX: Il existe deux familles qui se complètent. (a) Système complet, style WSL2 ou VirtualBox: on démarre un vrai OS invité (Linux, Windows, ReactOS) dans une machine virtuelle et on lance les .exe à l'intérieur. L'intégration avec Kazo passe alors par des pilotes invités (9p, SMB, agent). (b) Mode utilisateur, style WSL1 ou Wine: on lance directement un .exe ou un ELF, et l'émulateur traduit ses appels système vers les services Kazo (fichiers, réseau), sans installer Windows et sans licence Microsoft. Boxedwine (GPL-2.0, version 26R1 du 2026-04-22) fait déjà (b) pour les programmes Windows 16/32-bit (github.com/danoon2/Boxedwine). Ce choix conditionne le moteur, les licences et toute l'intégration.
  *A) A) Les deux, en couches: une VM complète pour installer un OS invité, plus un mode « lancer un .exe » sans Windows (style Wine), branchés sur un même cadre de sous-systèmes
   B) B) VM complète seulement: on installe l'OS invité, puis on lance les .exe à l'intérieur
   C) C) Mode utilisateur seulement: on lance les .exe/ELF directement, comme des apps Kazo
   D) D) Autre (précise)
  WHY: Tu demandes à la fois des sous-systèmes installables et l'exécution de .exe. La VM complète donne la compatibilité (un vrai OS) et le mode utilisateur donne l'intégration sans licence Windows. Les deux réutilisent les mêmes ponts Kazo (fichiers, réseau, fenêtres): on les conçoit ensemble et on les livre par étapes.
  DEPS: []

Q: 2. Comment séquencer le 32-bit et le x64, sachant que tu préfères le x64 pour viser Windows 7, 10 et 11?
  CTX: État au 2026-11-08:
- v86, le moteur navigateur le plus mûr (BSD-2, JIT x86 vers wasm), est 32-bit seulement et ne démarre pas de noyau 64-bit (copy/v86 Readme l.7-14 et 81). L'issue x86_64 #133 est ouverte depuis 2017.
- QEMU amont ne fait tourner des invités 64-bit qu'avec son interpréteur TCI sur wasm64 (commit 5aefc06403 du 2026-01-16, QEMU 11.0).
- Le JIT wasm de QEMU n'existe que dans le fork ktock/qemu-wasm, dont la branche master n'a pas bougé depuis le 2025-09-04.
- Bochs 3.1 (LGPL, interpréteur) démarre en UEFI.
- Personne n'a montré publiquement Windows 7, 10 ou 11 x64 dans un onglet. C'est une absence de preuve, pas une preuve d'impossibilité.
- Windows 11 exige 4 Go de RAM, 64 Go de disque, UEFI avec Secure Boot, TPM 2.0 et, depuis 24H2, SSE4.2/POPCNT.
- wasm32 plafonne à 4 Gio. Memory64 monte à 16 Gio, mais il est 10 % à plus de 100 % plus lent, et Safari ne l'a que derrière un drapeau.
- Windows 7 x64 est le plus accessible: démarrage BIOS, 2 Go de RAM, pas de TPM.
  *A) A) Par paliers: 1) le 32-bit avec v86 (Linux i686, Windows 9x/2000/XP, ReactOS); 2) Linux x86-64 sur un moteur wasm64; 3) Windows x64 expérimental: 7, puis 10, et 11 en dernier
   B) B) Le x64 dès le départ sur un seul moteur (QEMU ou Bochs en wasm64), en acceptant une grande lenteur et l'absence de Safari
   C) C) Le 32-bit seulement; on abandonne le x64
   D) D) Le 32-bit dans le navigateur, et Windows 10/11 x64 confié au « bac à sable serveur » (une VM Hyper-V diffusée en flux vidéo)
   E) E) Autre (précise)
  WHY: On livre vite quelque chose d'utilisable sur une base éprouvée, sans fermer la porte au x64: l'abstraction de moteur (Q4) permettra de brancher un cœur 64-bit quand les mesures le justifieront. Windows 10/11 x64 reste une piste de recherche, pas un engagement. L'option D pourra s'ajouter en complément (Q3).
  DEPS: ['Q1']

Q: 3. Comment Os-x86 s'articule-t-il avec Os-App-BoxedWine et l'idée du « bac à sable serveur »?
  CTX: Os-App-BoxedWine (GPL-2.0, « Run old Windows programs », statut Coming) et Os-App-EmulatorJS (GPL-3.0) ne contiennent qu'un fichier LICENSE. La description de BoxedWine parle encore de « HackerOs ». Le « bac à sable serveur » (une VM Hyper-V jetable par usager, fenêtre diffusée en H.264 et décodée avec WebCodecs) a été débattu et son récapitulatif validé le 2026-10-19, sans que rien soit commencé (doc/en/wasm/memory/decisions-not-built.md; doc/en/wasm/features.md:425-437). Décision déjà prise: la licence de chaque dépôt Os-App-* suit celle du projet qu'il emballe.
  *A) A) Os-x86 = cadre de sous-systèmes + moteur de VM permissif (v86) + interface; Os-App-BoxedWine reste le moteur « .exe sans Windows » (GPL) qui se branche sur ce cadre; le bac à sable serveur reste la voie pour Windows 10/11
   B) B) Os-x86 absorbe BoxedWine dans un seul dépôt, qui devient alors GPL
   C) C) Os-x86 remplace tout: on abandonne BoxedWine et le bac à sable serveur
   D) D) Autre (précise)
  WHY: Chaque licence reste dans son dépôt, comme la règle déjà décidée le veut, et on évite de dupliquer BoxedWine. Les trois pistes ont chacune un rôle clair: le 32-bit dans le navigateur, les .exe sans Windows, et Windows moderne côté serveur.
  DEPS: ['Q1', 'Q2']

## [1] Stratégie de moteur (réutiliser ou écrire)
Q: 4. Pour le moteur d'émulation, on réutilise un moteur open source existant ou on écrit le nôtre?
  CTX: Dans Kazo, une app installée tourne sur le thread principal (AppPackageInstaller.cs:639-645), sans compilation AOT, dans l'interpréteur IL de Mono et sans threads .NET (ADR 0106 et 0131). Un cœur CPU en C# à la manière de MsDos.Clone serait donc un interpréteur qui tourne dans un autre interpréteur. Mon estimation, non mesurée: de 0,3 à 2 millions d'instructions par seconde. Or démarrer Windows 10 demande environ 3e10 à 1e11 instructions, donc des heures.

v86, lui, traduit le code x86 en modules wasm pendant l'exécution. Il pèse environ 2,0 Mo de wasm et 360 Ko de JS, il est sous BSD-2 et il offre déjà 9p, le réseau et la sauvegarde d'état (v86.d.ts). Aucun émulateur PC x86 hébergé dans Blazor n'existe: Spice86, Aeon et Enzan ne font que du DOS.

AGENTS.md préfère le C# « sauf si c'est impraticable »: il faudra justifier l'exception dans un ADR.
  *A) A) Envelopper un moteur wasm existant (v86 d'abord) dans un Web Worker JS; le C# fait l'intégration (interface, fichiers, réseau, réglages) derrière une abstraction IVirtualMachine qui accepte d'autres moteurs
   B) B) Écrire un nouveau cœur en C# (dans la lignée de MsDos.Clone) qui tourne dans l'app Kazo
   C) C) Écrire un nouveau cœur en Rust ou en C++ compilé en wasm, avec un JIT à développer
   D) D) A maintenant, puis un cœur maison (Rust) plus tard pour le x64 ou le mode utilisateur
   E) E) Autre (précise)
  WHY: C'est la seule voie réaliste pour avoir un Linux ou un Windows utilisable à court terme. Le C# reste là où il excelle: l'intégration avec Kazo. L'abstraction garde l'option D ouverte sans s'y engager avant d'avoir des mesures.
  DEPS: ['Q1', 'Q2']

Q: 5. Qu'est-ce qu'on reprend de ton projet MsDos.Clone?
  CTX: MsDos.Clone (.NET 8, 4 projets, environ 29 000 lignes) émule un 8086 avec un 386 partiel (Cpu8086.cs:33,105,940; MemoryBus.cs:10-23; BlazorCanvasRenderer.cs:79-82):
- le préfixe 0x67 n'est jamais lu, donc pas d'octet SIB;
- LGDT et MOV CRn ne font que stocker les valeurs; il n'y a ni pagination ni exceptions;
- la mémoire est un bloc plat de 1 Mo et le rendu est en mode texte seulement;
- le temps est compté en instructions, pas en temps réel.

Ce qui est bon à garder:
- la séparation cœur/plateforme (IGraphicsRenderer, IEventRegistry, IStreamProvider);
- les tests SingleStepTests rejoués opcode par opcode;
- le workflow d'agent Copilot par phases;
- les modèles PIC, PIT, DMA, contrôleur de disquette et CMOS;
- l'astuce qui recule IP et rend la main pendant une attente.

Ce qu'il ne faut pas reproduire:
- 1,25 Go de données de test commitées (le dépôt fait 2,8 Go);
- le dossier GPL-3.0 8088-PC-Compatible et les programmes tiers de DOS_Files;
- un ROADMAP.md qui ne suit plus le code.
  *A) A) L'architecture (un cœur sans dépendance plus des adaptateurs d'hôte), la méthode de test et le workflow par phases; pas le code CPU
   B) B) Porter MsDos.Clone tel quel (.NET 8 vers 10) comme premier sous-système DOS dans Kazo
   C) C) Faire de MsDos.Clone la base du moteur et l'étendre jusqu'au 386 complet, puis au x64
   D) D) Rien
   E) E) Autre (précise)
  WHY: Le cœur CPU est à refaire pour viser Linux et Windows (mode protégé, pagination, exceptions, x64). Par contre, l'organisation du projet et la discipline de tests de MsDos.Clone s'appliquent telles quelles à Os-x86.
  DEPS: ['Q4']

Q: 6. Comment utilise-t-on v86: la version officielle ou un fork maison?
  CTX: v86 publie un paquet npm (0.5.473) et une release « latest » (2026-10-30). Ses points d'extension couvrent beaucoup de besoins sans toucher à son code: tampon disque personnalisé (get/set/load), adaptateur réseau par les événements net0-send et net0-receive, serveur 9p maison (handle9p), save_state et restore_state. Certaines intégrations demanderaient quand même un patch: par exemple, les ports virtio-console sont nommés « virtio-N » en dur (virtio_console.js:35,76,148). v86 n'accepte pas les PR ni les issues écrites, même en partie, par une IA générative (Readme l.201-204); un workflow Copilot ne pourrait donc pas contribuer en amont. Dans Kazo, l'habitude est de vendoriser une librairie JS précompilée dans wwwroot/lib, comme le fait Os-App-Webamp.
  *A) A) La version officielle, épinglée et vendorisée dans le wwwroot/lib de l'app (comme Webamp), intégrée par ses points d'extension; un fork Kazo-ca/v86 seulement si un patch devient bloquant
   B) B) Un fork Kazo-ca/v86 maintenu dès le départ
   C) C) Copier le code source de v86 dans Os-x86 et le modifier librement
   D) D) Autre (précise)
  WHY: Moins de maintenance, des mises à jour faciles, et la revue du Store voit une librairie connue. On ne paie le coût d'un fork, qui ne pourra pas contribuer en amont à cause de leur politique sur l'IA, que si c'est vraiment nécessaire.
  DEPS: ['Q4']

Q: 7. Pour le sous-système Linux, on fait un système complet ou un mode utilisateur?
  CTX: Système complet: v86 avec un vrai noyau i686. Linux contient déjà les pilotes 9p et virtio, donc l'intégration est complète sans rien installer dans l'invité.

Mode utilisateur, style WSL1:
- Blink (ISC) ne gère que le x86-64, pas les ELF i386. Il perd son JIT en wasm (environ 10 fois plus lent), et sa dernière release date du 2024-01-21.
- Il n'existe pas de qemu-user en wasm.
- CheerpX ne fait que du i386 et il est propriétaire (version 1.4.0 du 2026-10-07). Il n'est gratuit que chargé depuis le CDN de Leaning (cxrtnc.leaningtech.com); l'auto-hébergement demande une licence commerciale et les entreprises sont limitées à l'évaluation. Une licence CC BY-NC ne compte probablement pas comme « FOSS » pour eux; c'est une déduction à confirmer avec Leaning.

Le mode utilisateur donnerait un accès direct au VFS de Kazo, mais il faudrait traduire chaque appel système Linux vers Kazo.
  *A) A) Système complet (v86 + une distro i686 légère) avec un dossier partagé en 9p; le mode utilisateur sera réévalué plus tard
   B) B) Mode utilisateur avec Blink x86-64 interprété (appels système redirigés vers le VFS et le réseau de Kazo)
   C) C) CheerpX, si Leaning confirme que Kazo a droit au palier gratuit
   D) D) Les deux dès la v1
   E) E) Autre (précise)
  WHY: C'est compatible, libre, ça marche hors ligne et ça s'intègre nativement par 9p. Blink et CheerpX ont chacun une limite bloquante: pas d'i386 pour Blink, pas d'auto-hébergement pour CheerpX.
  DEPS: ['Q1', 'Q2', 'Q4']

Q: 8. Pour lancer un .exe sans installer Windows, on prend quelle approche?
  CTX: Boxedwine (GPL-2.0) fait tourner Wine 3.1 à 11 sur un noyau Linux et un CPU émulés:
- programmes Windows 16/32-bit seulement; les versions récentes de .NET ne fonctionnent pas;
- la version web publiée n'utilise que l'interpréteur, qui est lent;
- la branche master a des cibles JIT et multithread et une passerelle réseau WebSocket (octobre 2026), pas encore publiées;
- il exige les exceptions wasm exnref (Chrome/Edge 137+, Firefox 131+, Safari 18.4+);
- son audio AudioWorklet demande COOP/COEP même en mono-thread (buildInstructions.md l.88-93), ce que Kazo fournit déjà;
- le zip web 26R1 fait 122 Mo, au-delà de la limite de 64 Mio d'un paquet Kazo.

Les autres pistes:
- Hangover (LGPL) montre une voie plus intégrée: émuler seulement le code PE et réimplémenter sur Kazo le « côté Unix » de Wine. C'est un très gros chantier.
- Win32Emu (MIT, C#, Blazor) ne couvre que 15,4 % des quelque 4 962 fonctions exportées par les DLL de Windows ME.
- Les .exe .NET (reconnaissables à leur en-tête CLR) pourraient un jour passer par le runtime .NET de Kazo; la faisabilité n'est pas vérifiée.
  *A) A) Boxedwine tel quel, dans Os-App-BoxedWine (GPL-2), lancé par le cadre de sous-systèmes; l'approche Hangover sera réévaluée après des mesures
   B) B) Une approche Hangover maison: les DLL PE de Wine, avec le côté Unix réimplémenté sur Kazo
   C) C) Une réimplémentation de l'API Win32 en C# (style Win32Emu) pour des applications classiques simples
   D) D) Pas de mode « .exe sans Windows » en v1 (seulement dans une VM Windows)
   E) E) Autre (précise)
  WHY: C'est la seule solution mûre aujourd'hui, elle ne demande pas de licence Windows et elle est déjà prévue dans Os-App-BoxedWine. Le cadre commun permettra de la remplacer plus tard sans changer ce que voit l'usager.
  DEPS: ['Q1', 'Q3']

Q: 9. Pour le palier x64, on choisit le moteur maintenant ou après des mesures?
  CTX: Candidats:
- QEMU amont en wasm64 (GPL-2): interpréteur TCI seulement; configure exige wasm64 depuis janvier 2026.
- Le fork ktock/qemu-wasm (JIT et MTTCG): build wasm32 à 2300 Mo, binaire x86_64 de 40,8 Mo (14,6 Mo en gzip), master inactif depuis le 2025-09-04, bugs ouverts (dont POPCNT, #52), démos en console seulement. Sa série JIT TCG v4 n'est pas fusionnée en amont.
- Bochs 3.1 (LGPL-2.1): interpréteur, UEFI/OVMF, émulation VMX MBE requise pour Windows 11. Un build wasm existe via le fork ktock/Bochs utilisé par container2wasm.
- Les forks v86_64 (BSD-2): portlandhodl/v86_64 dit atteindre environ 530 MIPS et démarrer des bureaux Linux 64-bit. Rien de vérifié, 1 étoile, aucun Windows 64-bit annoncé.
- Un cœur maison.

Repère (FOSDEM 2025): compresser 10 Mo avec pigz sur un invité x86_64 prend environ 40 s sous Bochs, environ 30 s sous QEMU-wasm et environ 7 à 8 s avec 4 threads.
  *A) A) Ne pas trancher maintenant: un banc d'essai sur 2 ou 3 candidats (mêmes charges, mêmes navigateurs) avant d'ouvrir le palier x64
   B) B) Bochs-wasm (LGPL, meilleure compatibilité Windows, lent)
   C) C) QEMU-wasm (GPL, JIT dans un fork à maintenir)
   D) D) Suivre ou forker un v86_64 (BSD, jeune et non vérifié)
   E) E) Un cœur x64 maison
  WHY: Le paysage bouge vite (forks v86_64 de septembre-octobre 2026, JIT de QEMU pas fusionné) et aucune mesure n'existe: décider maintenant serait un pari. Le palier 32-bit n'en dépend pas.
  DEPS: ['Q2', 'Q4', 'Q16']

Q: 10. Est-ce qu'on prévoit un sous-système DOS, et sur quelle base?
  CTX: Les .exe DOS (en-tête MZ, 16-bit) forment un cas à part pour le dispatcher. Trois bases possibles:
- FreeDOS dans v86: aucun moteur de plus.
- MsDos.Clone: DOS émulé en C# (INT 21h). C'est déjà une approche « mode utilisateur » avec accès direct aux fichiers via IStreamProvider. Par contre le rendu est en texte seulement, la boucle tourne sur le thread principal avec Task.Delay, et le projet est en .NET 8.
- js-dos/DOSBox-X: GPL-2.0, donc dans une app séparée.

Le rapport sur les sous-systèmes suggère que DOS soit le premier sous-système livré, pour valider le dispatcher et les ponts fichiers et console.
  *A) A) DOS via v86 + FreeDOS (image libre), avec le même pipeline que les autres VM
   B) B) Porter MsDos.Clone comme petit sous-système DOS en C# (accès direct au VFS)
   C) C) js-dos dans une app GPL séparée
   D) D) Pas de DOS
   E) E) Autre (précise)
  WHY: Aucun moteur de plus à maintenir et le même chemin de code que Linux et Windows. L'option B reste intéressante si tu tiens à valoriser ton travail, mais il faudrait d'abord sortir sa boucle du thread principal.
  DEPS: ['Q4', 'Q5']

## [2] Livraison et empaquetage
Q: 11. Comment livrer la fonctionnalité: app du Marketplace, intégrée au noyau, ou un mélange des deux?
  CTX: Le service worker publié précache tous les .dll, .wasm et .js à l'installation (service-worker.published.js:37). Une app intégrée au noyau alourdit donc le téléchargement de chaque usager; le lazy-load ne fait que retarder le chargement en mémoire. Une app du Store peut livrer du JS, du wasm et des Web Workers servis à _content/<Assembly>/ (ADR 0206). Mais sans changement au noyau, trois choses restent impossibles:
- lancer un binaire depuis le terminal: le shell refuse tout fichier qui contient un octet NUL (ShellInterpreter.Scripts.cs:67-120);
- monter le système de fichiers de l'invité: les apps ne peuvent pas fournir d'IFileSystemProvider;
- stocker un fichier de plus de 16 Mio.
  *A) A) Un mélange: moteurs, images et interface dans des apps du Marketplace, plus quelques points d'extension génériques ajoutés au noyau par ADR (lancement des exécutables, stockage de gros fichiers, montage de sous-système)
   B) B) 100 % Marketplace, aucun changement au noyau (l'intégration se limite au double-clic et à la fenêtre)
   C) C) Intégré au noyau comme app système (IsSystemApp)
   D) D) Autre (précise)
  WHY: Ça respecte ton objectif de petit téléchargement et ça garde les moteurs GPL hors de l'OS sous CC BY-NC. Les points d'extension seront aussi utiles à d'autres apps.
  DEPS: ['Q1', 'Q4']

Q: 12. Comment découpe-t-on les paquets (gestionnaire, moteurs, images)?
  CTX: Contraintes:
- Un paquet .kazoapp est limité à 64 Mio d'archive, 128 Mio une fois extrait et 2000 fichiers (AppPackage.cs:62-68).
- Les assemblies supplémentaires d'une app ne sont pas encore empaquetées (#419).
- On peut déclarer des dépendances (AppDependencyManifest), mais je n'ai trouvé aucune résolution automatique dans l'installeur (confiance moyenne).
- Pour le catalogue officiel, la CI construit l'app à partir du commit, la passe à CodeQL, puis un mainteneur l'approuve et elle est signée (KAZO_STORE_SIGNING_KEY; Os-AppMarketPlace catalog-format.md:55-117).

Tailles: v86 environ 2,4 Mo plus SeaBIOS 128 Kio et VGA BIOS 36 Kio; QEMU-wasm environ 41 Mo; zip web de Boxedwine environ 122 Mo.
  *A) A) Une app « gestionnaire de sous-systèmes » (MIT) qui inclut v86, petit; chaque autre moteur (Boxedwine GPL, moteur x64) est une app du Marketplace séparée et signée qui s'enregistre auprès du gestionnaire; images et firmwares sont des données téléchargées à part et vérifiées par SHA-256
   B) B) Une app autonome par moteur, chacune avec sa propre interface
   C) C) Un gestionnaire qui télécharge lui-même tous les moteurs, hors du Store
   D) D) Autre (précise)
  WHY: Tout le code exécutable passe par la revue et la signature du Store, les moteurs GPL restent isolés, et les données lourdes échappent aux limites de paquet. Il faudra un mécanisme pour que les moteurs s'enregistrent (bus d'événements ou intents), ou alors implémenter la résolution des dépendances.
  DEPS: ['Q11', 'Q16']

Q: 13. Où héberge-t-on les images libres et les firmwares que le gestionnaire va télécharger?
  CTX: Les fichiers des GitHub Releases n'envoient pas d'en-têtes CORS, donc le navigateur ne peut pas les lire; raw.githubusercontent.com et jsDelivr en envoient (decisions-not-built.md). Sous COEP credentialless, une ressource d'une autre origine sans CORS ne peut pas être lue. Le Cache API du service worker ne sert pas les plages d'octets nativement (il faut workbox-range-requests). v86 accepte des images pré-découpées en morceaux (use_parts, split-image.py), faites pour l'hébergement statique, ou des requêtes Range, qui désactivent la compression HTTP. Les limites de taille par fichier de chaque hébergeur restent à vérifier. Le serveur Kazo est optionnel.
  *A) A) Un catalogue d'images pré-découpées en morceaux, hébergé statiquement avec CORS (dépôt dédié servi par raw.githubusercontent ou jsDelivr, limites de taille à vérifier), plus l'import local d'ISO par l'usager
   B) B) Sur Kazo.Os.Server (requêtes Range), donc dépendant du serveur optionnel
   C) C) Un bucket ou CDN externe avec CORS (coût récurrent)
   D) D) Import local seulement, aucun catalogue
   E) E) Autre (précise)
  WHY: Ça marche sans le serveur Kazo, c'est gratuit, ça passe COEP grâce à CORS, et c'est déjà le format prévu par v86. L'import local couvre Windows (Q18).
  DEPS: ['Q11']

Q: 14. Quelles images libres offre-t-on en premier dans le catalogue?
  CTX: v86 fait bien tourner Linux 32-bit, ReactOS (0.4.16 du 2026-08-29, GPL-2, i386 seulement), FreeBSD, Haiku, KolibriOS et FreeDOS (Readme de copy/v86). Une image Linux minimale en console pèse quelques dizaines de Mo; un bureau, de quelques centaines de Mo à quelques Go. Les distributions grand public offrent de moins en moins de versions i386 (à vérifier pour chacune). Une image construite par le projet peut contenir d'avance le montage 9p, la console série et un état déjà démarré pour un lancement instantané (Q24).
  *A) A) Une image Linux minimale en console, construite de façon reproductible par le projet (i686, 9p et console série prêts), puis une image avec un bureau léger, ReactOS et FreeDOS
   B) B) Une distribution grand public complète avec bureau dès le départ
   C) C) Aucune image fournie: l'usager importe ses propres ISO
   D) D) Autre (précise)
  WHY: Un petit téléchargement, un démarrage rapide et une intégration garantie pour valider le cadre. Les images plus lourdes suivront.
  DEPS: ['Q2', 'Q7', 'Q13', 'Q18']

Q: 15. Quels points d'extension veut-on ajouter au noyau Kazo, et dans quel ordre?
  CTX: Conventions Kazo: un ADR est fusionné avant d'écrire le code (le prochain numéro libre est 0241), le travail se fait en séries de branches, et seul Benoit fusionne dans main. Points d'extension candidats:
1. un lancement des exécutables façon binfmt: le noyau reconnaît MZ, PE ou ELF et passe le fichier à la commande du gestionnaire;
2. un stockage pour gros fichiers, ou en blocs (prolonge l'issue #379);
3. un point d'accès Wisp multiplexé dans Kazo.Os.Server, avec une politique propre aux VM;
4. le montage du système de fichiers de l'invité sous /mnt/<nom>;
5. l'enregistrement des workers JS externes comme processus (/proc, kill);
6. l'empaquetage des assemblies propres d'une app (#419);
7. la résolution des dépendances entre apps;
8. une API son dans le SDK.
  *A) A) Une série minimale, dans l'ordre où chaque jalon en a besoin: lancement des exécutables, puis stockage, puis réseau Wisp, puis montage; les autres seulement s'ils deviennent bloquants
   B) B) Aucun ADR noyau en v1: tout dans l'app, on verra ensuite
   C) C) Rédiger toute la série d'ADR d'un coup avant de commencer
   D) D) Autre (précise)
  WHY: Ça suit la règle « ADR avant le code » sans bloquer le premier jalon, qui n'a besoin d'aucun changement au noyau. Chaque ADR reste petit et générique.
  DEPS: ['Q11']

## [2] Licences
Q: 16. Quelle licence pour Os-x86, et qu'a-t-on le droit d'y mettre?
  CTX: Os-x86 est sous MIT (fichier LICENSE de 1 064 octets). Sont compatibles: v86 (BSD-2), Blink (ISC), tcpip.js (MIT), Theseus (Apache-2.0). Du code GPL (Boxedwine, QEMU, js-dos, halfix) imposerait la GPL à toute œuvre combinée. QEMU, en GPL-2 seulement, n'est compatible ni avec l'Apache-2.0 ni avec la GPL-3 (apache.org/licenses/GPL-compatibility.html; gnu.org/licenses/license-list.html). La LGPL (Bochs, SMBLibrary en LGPL-3, wisp-js en LGPL-3) est acceptable sous forme de module séparé et remplaçable, avec son code source.
  *A) A) Garder le MIT et n'y mettre que du code permissif (la LGPL seulement comme module séparé et remplaçable); les moteurs GPL vont dans leurs propres dépôts Os-App-*, sous la licence du projet emballé
   B) B) Passer Os-x86 en GPL-2.0 pour pouvoir y intégrer QEMU ou Boxedwine
   C) C) Autre (précise)
  WHY: Le MIT reste compatible avec v86 et avec l'OS, et la règle « la licence suit le projet emballé » isole déjà le GPL dans Os-App-BoxedWine. Passer en GPL fermerait des portes sans rien gagner tant que QEMU n'est pas choisi (Q9).
  DEPS: ['Q4']

Q: 17. Comment gère-t-on le conflit possible entre une app GPL (par exemple Boxedwine) et le SDK Kazo sous CC BY-NC?
  CTX: Kazo OS est sous CC BY-NC 4.0: le texte est dans LICENSE.md, et l'API GitHub affiche NOASSERTION. Les paquets NuGet du SDK livrent ce LICENSE.md (Packaging.props:31,48). Le projet GNU classe la CC BY-NC comme non libre, donc incompatible avec la GPL. La GPL interdit d'ajouter des restrictions, comme une interdiction d'usage commercial (FAQ GPL, #NoMilitary), et un plugin qui partage des structures en mémoire avec son hôte forme un seul programme combiné (#GPLPlugins). Creative Commons déconseille d'ailleurs ses licences pour du logiciel. Une app GPL liée au SDK sous CC BY-NC est donc douteuse juridiquement. Ce n'est pas un avis juridique.
  *A) A) En attendant un avis juridique, garder le GPL à distance: le moteur GPL tourne en JS/wasm dans son worker et dialogue par messages avec un code C# non GPL qui, lui, utilise le SDK; jamais de lien en mémoire entre le GPL et le SDK
   B) B) Passer le SDK (Kazo.Os.AppSdk* et les abstractions) sous une licence permissive (MIT ou Apache) tout en gardant l'OS sous CC BY-NC
   C) C) Ajouter côté Kazo une exception de licence qui permet aux apps GPL de lier le SDK
   D) D) Ignorer pour l'instant
   E) E) Autre (précise)
  WHY: A protège tout de suite sans décision lourde. La vraie solution durable est probablement B: un SDK permissif profiterait à tout le Marketplace. C'est ta décision, à valider avec un juriste.
  DEPS: ['Q8', 'Q16']

Q: 18. Pour Windows, qui fournit l'installation et la licence?
  CTX: Les images de Windows ne sont pas redistribuables, et il faut une licence par instance, physique ou virtuelle (conditions OEM de Windows 11). Le support de Windows 7 a pris fin le 2020-01-14 et celui de Windows 10 le 2025-10-14. ReactOS (GPL-2, i386) est le seul invité compatible Windows qu'on a le droit de redistribuer. Un état sauvegardé (save_state) d'un Windows installé contient Windows: on ne peut pas le partager. Les contournements des exigences de Windows 11 (clés de registre LabConfig) ne sont pas officiels et donnent une installation non supportée.
  *A) A) « Apporte ton ISO et ta licence » pour Windows: un assistant d'import local, sans jamais héberger ni partager d'images ou d'états Windows; Kazo ne fournit que des images libres (Linux, ReactOS, FreeDOS); aucun contournement automatique des exigences de Windows 11
   B) B) Pareil, mais l'assistant automatise les contournements de Windows 11 (LabConfig)
   C) C) Côté Windows, ne supporter que ReactOS (100 % libre)
   D) D) Autre (précise)
  WHY: Respecte les licences Microsoft, garde la fiche Store défendable en revue, et laisse quand même l'usager utiliser son propre Windows.
  DEPS: ['Q2']

## [2] Architecture d'exécution et sécurité
Q: 19. Le worker du moteur doit-il faire partie du modèle de processus de Kazo?
  CTX: Le moteur doit tourner dans un Web Worker JS dédié, lancé depuis le wwwroot de l'app (new Worker fonctionne, ADR 0206). L'app installée tourne sur le thread principal: une boucle synchrone gèlerait tout le bureau et déclencherait « Ne répond pas ». Ce worker échappe au modèle de processus de Kazo: pas de PID dans /proc, pas de kill -9, pas de comptabilité CPU et mémoire. Le coût de 15 à 40 Mo d'un runtime .NET par worker (ADR 0070) ne s'applique pas à un moteur en JS/wasm pur. Autre possibilité: une iframe sur une autre origine (Document-Isolation-Policy, Chrome 137).
  *A) A) Un worker JS dédié par VM, plus un ADR noyau pour l'enregistrer comme processus enfant de l'app (PID, visible dans /proc et le gestionnaire de tâches, kill, mémoire comptabilisée)
   B) B) Un worker JS dédié, sans intégration au modèle de processus (l'app le gère seule)
   C) C) Une iframe isolée sur une autre origine
   D) D) Autre (précise)
  WHY: Ça respecte le principe de Kazo de « faire comme le vrai matériel »: une VM se voit et se tue comme n'importe quel processus. Ça évite aussi qu'un worker hors contrôle consomme la RAM sans que personne ne le voie.
  DEPS: ['Q4', 'Q15']

Q: 20. Qui peut installer et utiliser les sous-systèmes, et avec quelles permissions?
  CTX: Les permissions dangereuses en jeu: network.real.access (qui demande aussi l'approbation d'un admin et un serveur connecté), filesystem.user-home.* et le presse-papiers. IsSystemApp accorde automatiquement les permissions dangereuses. Une installation passe par une approbation admin en deux étapes, des sources de confiance et une liste de blocage (app-installation.md). Exécuter des .exe ou des ELF non fiables avec un accès direct au VFS et au réseau réel est risqué: le relais pourrait servir de proxy ouvert. La passerelle réseau de Boxedwine se limite d'ailleurs par défaut à localhost et vérifie l'origine.
  *A) A) Le moteur est installé par un admin (au niveau système); chaque usager crée ses VM avec des permissions minimales par défaut (pas de réseau réel, partage limité à un dossier choisi), et chaque intégration s'active explicitement par VM, avec consentement
   B) B) Installation libre par chaque usager, toutes les permissions accordées à l'installation
   C) C) Admin seulement pour tout (installation et création des VM)
   D) D) Autre (précise)
  WHY: L'admin garde le contrôle de ce qui s'installe, l'usager garde son autonomie, et le principe du moindre privilège limite les dégâts d'un exécutable malveillant.
  DEPS: ['Q11']

## [3] Stockage des disques virtuels
Q: 21. Où vivent les disques virtuels (image de base et écritures de l'invité)?
  CTX: Le VFS limite chaque fichier à 16 Mio, découpé en morceaux de 256 Kio dédupliqués, et n'écrit qu'un fichier entier à la fois: pas d'écriture aléatoire (FileContentStoragePolicy.cs:15-18; KazoFileStream.cs:93-130). L'OPFS offre une lecture-écriture synchrone (SyncAccessHandle), mais seulement dans un worker dédié. v86 garde les écritures disque en RAM, et seulement dans save_state: sans couche d'écriture à nous, elles sont perdues au rechargement. Le quota est partagé avec l'IndexedDB de Kazo, qui réserve 5 % (au plus 256 Mio, ADR 0099). hostfs ne marche que dans Chrome et Edge, et ses poignées vivent sur le thread principal.
  *A) A) OPFS, géré par le moteur dans son worker: une image de base en lecture seule téléchargée au fil des besoins, plus une couche copie-sur-écriture; l'espace utilisé est affiché dans df et les Diagnostics de Kazo; une API noyau de stockage en blocs pourra venir plus tard
   B) B) Des fichiers du VFS découpés en morceaux de 16 Mio ou moins (visibles dans l'explorateur, mais lents et réécrits en entier)
   C) C) Une nouvelle API noyau de stockage en blocs dès la v1 (prolonge #379)
   D) D) Un dossier de l'ordinateur monté (hostfs), Chrome/Edge seulement
   E) E) Autre (précise)
  WHY: Seule l'OPFS offre des écritures aléatoires rapides depuis le worker. Afficher l'espace utilisé garde le tout visible dans Kazo sans attendre un nouvel ADR.
  DEPS: ['Q4', 'Q11']

Q: 22. Les disques des VM font-ils partie de la synchronisation et des sauvegardes de Kazo?
  CTX: Si les disques vivent sous /home, la synchro avec le serveur (/api/sync) et les sauvegardes .kosbak risquent d'exploser: une image fait de quelques centaines de Mo à des dizaines de Go. Sur ce point, le rapport Kazo laissait la question ouverte.
  *A) A) Exclus de la synchro et des sauvegardes par défaut; on peut exporter ou importer une VM (disque + configuration) à la main dans un fichier
   B) B) Inclus comme tout le reste
   C) C) Au choix, VM par VM (option « inclure dans les sauvegardes »)
   D) D) Autre (précise)
  WHY: Ça évite des sauvegardes et une synchro démesurées, tout en gardant un moyen explicite de sauvegarder une VM précieuse.
  DEPS: ['Q21']

Q: 23. Quel budget de stockage, et demande-t-on la persistance au navigateur?
  CTX: Quotas par origine: Chromium jusqu'à 60 % du disque; Firefox, en mode « best-effort », le plus petit de 10 % du disque et 10 Gio (50 % en mode persistant); Safari environ 60 %, mais il efface après 7 jours sans interaction. Une éviction efface toute l'origine, donc aussi tout le VFS de Kazo (MDN, Storage quotas). Windows 10 x64 demande au moins 32 Go de disque, Windows 11 au moins 64 Go.
  *A) A) Demander navigator.storage.persist() à la création de la première VM; des plafonds par défaut que l'admin peut changer (en ordre de grandeur: quelques Go pour un Linux léger, environ 10 Go pour un Windows 32-bit); disques creux et alerte avant de manquer de place
   B) B) Aucun plafond: on laisse le navigateur gérer
   C) C) Un plafond fixe, non modifiable
   D) D) Autre (précise)
  WHY: La persistance protège aussi le VFS de Kazo contre l'éviction, et des plafonds modifiables évitent qu'une VM remplisse tout le quota.
  DEPS: ['Q21']

Q: 24. Offre-t-on la mise en veille et les instantanés de VM?
  CTX: Le save_state de v86 ne garde que les pages de RAM non vides (par blocs de 4 Kio) et l'état des périphériques; restore_state accepte du zstd (cpu.js, state.js). Le projet nanokrnl utilise des instantanés pour éviter un démarrage à froid. Attention: en mode handle9p, le dossier partagé n'est pas mis en cache et doit rester cohérent avec les états sauvegardés (v86 docs/filesystem.md).
  *A) A) Oui: mise en veille et reprise de chaque VM, plus des instantanés nommés; les images libres sont livrées avec un état « déjà démarré » pour un lancement instantané
   B) B) Mise en veille et reprise seulement
   C) C) Pas d'instantanés en v1
   D) D) Autre (précise)
  WHY: Ça masque les longs démarrages, qui sont le point faible de l'émulation dans le navigateur, et v86 le fait déjà.
  DEPS: ['Q4', 'Q21']

## [3] Dossiers partagés et fichiers
Q: 25. Pour un invité Linux, quels dossiers Kazo partage-t-on, et avec quels droits?
  CTX: Avec v86, l'invité monte le partage « host9p » (mount -t 9p -o trans=virtio,version=9p2000.L host9p /mnt), et handle9p permet d'écrire notre propre serveur 9p qui sert le VFS de Kazo. Les contraintes du VFS s'appliquent:
- les fichiers sont limités à 16 Mio et réécrits en entier: si l'invité écrit plus de 16 Mio, l'écriture échoue (EFBIG), et les écritures aléatoires coûtent cher;
- il faut définir la correspondance entre les uid/gid de l'invité et les permissions Linux de Kazo;
- il faut invalider les caches quand un fichier change côté Kazo.
  *A) A) Par défaut, le dossier personnel de l'usager Kazo, en lecture-écriture, monté automatiquement (par exemple /mnt/kazo); les autres dossiers (dont les lecteurs réseau /mnt) s'ajoutent explicitement; les fichiers de l'invité appartiennent à l'usager Kazo propriétaire de la VM
   B) B) Seulement un dossier dédié (par exemple ~/Partage)
   C) C) Tout le VFS (/), en appliquant les permissions de Kazo
   D) D) En lecture seule par défaut
   E) E) Autre (précise)
  WHY: C'est l'équivalent du /mnt/c de WSL, en plus prudent: utile tout de suite, limité à ce qui appartient à l'usager, et extensible au cas par cas.
  DEPS: ['Q7', 'Q20']

Q: 26. Où tourne le serveur 9p qui fait le lien avec le VFS?
  CTX: Les apps n'accèdent au VFS qu'à travers le SDK C# (KazoFile), sur le thread principal; les workers de l'app n'y ont pas accès, et hostfs vit lui aussi sur le thread principal (ADR 0181). Un serveur 9p en C# sur le thread principal charge l'interface; une API fichiers accessible depuis un worker JS demanderait un ADR. Côté interop, JSImport/JSExport transmettent Span et ArraySegment sans copie (MemoryView, learn.microsoft.com, 2026-11-03).
  *A) A) En v1, un serveur 9p en C# dans l'app (via KazoFile, permissions respectées), avec des requêtes groupées et des transferts par anneaux SharedArrayBuffer; on mesure, puis un ADR pour un service fichiers accessible aux workers si le thread principal sature
   B) B) D'abord un ADR noyau: un service fichiers accessible directement depuis un worker JS
   C) C) Copier les fichiers partagés dans l'OPFS et synchroniser (pas de vrai partage en direct)
   D) D) Autre (précise)
  WHY: Ça marche avec les API d'aujourd'hui, ça respecte les permissions de Kazo, et on ne change le noyau que si les mesures le justifient.
  DEPS: ['Q19', 'Q25']

Q: 27. Pour un invité Windows, comment partage-t-on les fichiers avec Kazo?
  CTX: Windows n'a pas de client 9p. Les pistes:
- virtio-fs: demande virtio-win et WinFsp dans l'invité, Windows 8 et plus seulement, et il faudrait écrire le périphérique côté émulateur.
- Le client SMB intégré à Windows, contre un serveur SMB en C# (SMBLibrary, LGPL-3) sur une IP virtuelle. Windows 11 24H2 exige la signature SMB, l'édition Pro refuse les connexions invité, et XP veut du SMB1.
- WebDAV: déprécié depuis novembre 2023.
- HGFS de VMware: v86 émule déjà une partie du backdoor VMware (souris absolue, presse-papiers texte, heure), mais pas HGFS.
- En dernier recours, valable partout: injecter les fichiers dans une image ISO ou disquette (v86 a un générateur ISO Joliet intégré).
  *A) A) En v1, des transferts ponctuels par image ISO ou disquette générée, plus le glisser-déposer; ensuite, un serveur SMB sur la passerelle virtuelle (de XP à 11, sans pilote dans l'invité)
   B) B) Un agent « Kazo Guest Tools » maison (pilote + service) pour toutes les versions
   C) C) virtio-fs (Windows 8 et plus seulement)
   D) D) HGFS par le backdoor VMware (Windows 9x/2000/XP)
   E) E) Autre (précise)
  WHY: Ça marche tout de suite sur toutes les versions, sans pilote. Le serveur SMB vient ensuite et donne un vrai lecteur réseau avec les outils intégrés à Windows. La LGPL de SMBLibrary reste compatible avec un module séparé (Q16).
  DEPS: ['Q2', 'Q16']

Q: 28. Veut-on aussi le sens inverse: voir les fichiers de l'invité dans l'explorateur Kazo (comme « wsl$ » dans Windows)?
  CTX: Il faut pour ça un nouveau point d'extension: les apps ne peuvent pas fournir leur propre IFileSystemProvider. Côté noyau, IDynamicFileSystemMountRouter existe déjà; c'est lui qui monte les dossiers de l'ordinateur comme lecteurs réseau (ADR 0181).
  *A) A) Pas en v1; un ADR plus tard pour monter un sous-système sous /mnt/<nom>
   B) B) Oui dès la v1 (ADR noyau en priorité)
   C) C) Jamais: on passe toujours par le dossier partagé
   D) D) Autre (précise)
  WHY: Le dossier partagé couvre déjà le besoin principal; le montage inverse est un plus, qui mérite son propre ADR.
  DEPS: ['Q15']

Q: 29. Accepte-t-on d'exiger des pilotes ou outils à installer dans l'invité?
  CTX: Linux a tout en natif: virtio-net, virtio-blk, 9p, virtio-input, virtio-gpu, HDA. Windows, sans pilote, n'a que ce qui est intégré: carte réseau e1000 ou rtl8139, disque AHCI ou IDE, tablette USB, son HDA, client SMB, adaptateur d'affichage de base. Au-delà, il faut virtio-win (Windows 8 et plus), WinFsp, spice-guest-tools, qemu-ga ou VMware Tools, et leurs conditions de redistribution ne sont pas vérifiées. De plus, les ports virtio-console de v86 s'appellent « virtio-N »: les agents standard, qui attendent com.redhat.spice.0, demanderaient un patch.
  *A) A) Tout doit d'abord marcher sans rien installer (périphériques reconnus nativement); une ISO « Kazo Guest Tools » optionnelle (agent maison) ajoute le presse-papiers, la résolution dynamique et le lancement d'apps
   B) B) Exiger des outils dans l'invité pour toute intégration
   C) C) Jamais d'outils dans l'invité
   D) D) Autre (précise)
  WHY: L'usager a une VM fonctionnelle dès le premier démarrage, et l'agent maison évite de dépendre de binaires tiers dont la redistribution n'est pas claire.
  DEPS: ['Q2', 'Q27']

## [3] Réseau
Q: 30. Comment l'invité accède-t-il à Internet?
  CTX: Un navigateur n'a pas de sockets bruts.

Le relais de Kazo ouvre une WebSocket par flux TCP. Par défaut il permet TCP 80/443 et UDP 53/123, au plus 4 flux par appareil, et il bloque les réseaux privés. Il exige network.real.access et un serveur connecté (ProxyOptions.cs:160-166 et 233-234). Un OS invité dépasse 4 flux tout de suite. Et la règle de Kazo dit qu'aucune fonctionnalité ne doit dépendre du serveur.

Wisp v2.1 multiplexe TCP et UDP sur une seule WebSocket. Sa spécification est sous CC-BY-4.0, mais je n'ai trouvé aucun serveur Wisp en C#.

Piles TCP/IP en espace utilisateur disponibles: fake_network.js de v86, et tcpip.js (lwIP en wasm, MIT, moins de 100 Ko).

Sans serveur, il ne reste que HTTP via fetch, limité par CORS.
  *A) A) Une pile TCP/IP dans le worker du moteur, qui termine les connexions de l'invité; hors ligne, DNS et HTTP passent par le réseau simulé et fetch; l'Internet réel passe par un nouveau point Wisp multiplexé dans Kazo.Os.Server (ADR), avec une politique propre aux VM, sous network.real.access
   B) B) Le relais actuel tel quel (une WebSocket par flux), en élargissant sa politique pour les VM
   C) C) Un relais Wisp ou wsproxy externe, configuré par l'usager et appelé directement par le JS de l'app
   D) D) Pas d'Internet réel (réseau simulé seulement)
   E) E) Autre (précise)
  WHY: Le serveur reste facultatif, comme la règle de Kazo l'exige: hors ligne, la VM a quand même le DNS et le HTTP simulé. Wisp fait passer tous les flux dans une seule WebSocket, ce qui règle la limite de 4 flux sans élargir le relais existant, et sa spécification est simple à porter en C#.
  DEPS: ['Q15', 'Q20']

Q: 31. Les VM apparaissent-elles sur le réseau simulé de Kazo?
  CTX: Le réseau simulé de Kazo ne fonctionne qu'au niveau HTTP: SimulatedHost, kazodns, les noms .hack, KazoHttpListener et ListeningPortTable. Une connexion TCP brute vers un hôte simulé est refusée (#393). Le backend fetch de v86 a une API pour que l'invité écoute en TCP (PR #1233). Rendre la VM visible voudrait dire: une IP sur le réseau local simulé, joignable par ping, nmap ou curl, et des ports d'écoute de l'invité publiés pour Kazo.
  *A) A) Oui: chaque VM reçoit une IP du réseau simulé, résout ses noms via kazodns et accède aux sites simulés en HTTP; ses ports d'écoute sont publiés dans ListeningPortTable; le TCP brut viendra avec #393
   B) B) VM isolée: seulement une traduction d'adresses vers l'Internet réel
   C) C) Plus tard
   D) D) Autre (précise)
  WHY: C'est cohérent avec le principe de Kazo de « faire comme le vrai matériel »: la VM devient une machine du réseau Kazo, utile pour les commandes réseau et les apps existantes, et ça ne coûte rien en attendant #393.
  DEPS: ['Q30']

Q: 32. Le JS d'une app peut-il ouvrir ses propres connexions et contourner la passerelle réseau de Kazo?
  CTX: Je n'ai trouvé aucune Content-Security-Policy dans l'index.html d'Ecosystem, le service worker ou le Program.cs du serveur. Déduction du fact-check: le JS d'une app installée pourrait appeler fetch ou ouvrir une WebSocket directement, et ainsi contourner network.real.access. C'est important pour la carte réseau virtuelle de la VM et pour la revue du Store, qui liste déjà les fichiers JS de chaque app.
  *A) A) Règle: tout le trafic de la VM passe par la passerelle SDK ou le serveur Kazo; un ADR ajoute une CSP (connect-src) à l'OS, et la revue du Store le vérifie
   B) B) L'app peut ouvrir ses propres WebSocket, à condition de le documenter et de le déclarer dans son manifeste
   C) C) Autre (précise)
  WHY: Sans CSP, n'importe quel JS d'app peut contourner network.real.access. Garder tout le trafic sous une même politique empêche qu'on se serve du relais comme proxy ouvert. Une CSP touche tout l'OS, d'où un ADR séparé.
  DEPS: ['Q30']

## [4] Interface et expérience usager
Q: 33. À quoi ressemble la création d'un sous-système?
  CTX: Avec WSL, une seule commande installe une distro toute prête (« wsl --install <distro> »). VirtualBox et Hyper-V proposent plutôt un assistant détaillé (RAM, disques, ISO). Kazo a le Store, kazopkg, les réglages KazoSettings (~/.config/<appId>/) et les notifications.
  *A) A) Les deux: des profils prêts à l'emploi installables en un clic (par exemple « Linux console », « ReactOS », « Windows XP: apporte ton ISO »), plus un mode avancé (RAM, disques, périphériques, réseau)
   B) B) Profils prêts seulement (style WSL)
   C) C) Assistant avancé seulement (style VirtualBox)
   D) D) Autre (précise)
  WHY: Les profils couvrent l'usage courant sans connaissances techniques, et le mode avancé couvre les invités apportés par l'usager, comme Windows.
  DEPS: ['Q1', 'Q14']

Q: 34. L'affichage: une fenêtre par VM, ou chaque fenêtre de l'invité devient une fenêtre Kazo?
  CTX: Le SDK 3.1 offre WindowStyle None et Tool, et les fenêtres enfants (ADR 0234). WSLg affiche chaque fenêtre Linux séparément grâce au mode VAIL de RDP. En système complet, sans agent dans l'invité, on n'a qu'une image de l'écran entier. Le plein écran de l'OS peut capturer le clavier avec navigator.keyboard.lock, mais seulement dans Chromium et en plein écran demandé par JS.
  *A) A) En v1, une fenêtre Kazo par VM (canvas, mise à l'échelle, plein écran, capture du clavier); un mode « fenêtres intégrées » viendra plus tard, avec l'agent invité ou Boxedwine
   B) B) Le mode intégré (chaque fenêtre de l'invité = une fenêtre Kazo) dès la v1
   C) C) Les deux dès la v1
   D) D) Autre (précise)
  WHY: La fenêtre unique marche avec n'importe quel invité, sans pilote. Le mode intégré demande un agent ou un pilote d'affichage Wine qui n'existe pas encore.
  DEPS: ['Q1', 'Q8']

Q: 35. Que se passe-t-il quand on ouvre un .exe ou un ELF depuis Kazo?
  CTX: Le double-clic est déjà possible: on déclare un FileHandlers (type média .exe) et Kazo passe par les intents. Le terminal, lui, ne peut pas: hsh lit le fichier comme du texte, refuse l'octet NUL (code de sortie 126) et n'a pas de binfmt (ShellInterpreter.Scripts.cs:67-120).

Pour choisir le bon moteur, un dispatcher lirait l'en-tête du fichier (doc pe-format de Microsoft):
- MZ, NE ou PE; e_lfanew, à l'offset 0x3C, pointe vers la signature PE;
- le champ Machine: 0x14c pour i386, 0x8664 pour x64;
- le sous-système: 2 pour une app graphique, 3 pour une app console;
- le répertoire de données 14, l'en-tête CLR, qui signale un exécutable .NET;
- ou bien la signature ELF.

Kazo a déjà des associations de fichiers par défaut pour chaque usager. Si le moteur choisi est une VM Windows, il faut en plus que le fichier soit visible dans l'invité (dossier partagé) et qu'un agent le lance.
  *A) A) En v1, le double-clic et « Ouvrir avec » via FileHandlers; un dispatcher lit l'en-tête et propose le bon moteur parmi ceux installés (DOS, Boxedwine, VM Windows, Linux), avec un choix que l'usager peut mémoriser; l'exécution depuis le terminal arrive en v2 avec un binfmt dans le noyau (ADR)
   B) B) Toujours demander à l'usager quel moteur utiliser
   C) C) Seulement depuis l'app gestionnaire, sans association de fichiers
   D) D) Un binfmt dans le noyau dès la v1
   E) E) Autre (précise)
  WHY: Ça donne tout de suite l'expérience « je double-clique et ça marche » sans toucher au noyau, et ça prépare le binfmt du terminal, qui suivra la règle de l'ADR d'abord.
  DEPS: ['Q1', 'Q8', 'Q15']

Q: 36. Les sous-systèmes Linux s'intègrent-ils au terminal de Kazo?
  CTX: v86 expose une console série (serial0_send) et virtio_console. Kazo a KazoConsole, des apps de type Terminal et des commandes. Avec WSL, chaque distro a son profil dans le terminal, et on peut lancer des .exe depuis Linux.
  *A) A) Oui: chaque sous-système Linux a une commande et un profil de terminal Kazo branchés sur la console série ou virtio (sans écran graphique), en plus de l'écran graphique optionnel; une commande de gestion permet de lister, démarrer, arrêter et exécuter
   B) B) Écran graphique seulement
   C) C) Console seulement (pas de graphique en v1)
   D) D) Autre (précise)
  WHY: La console série est légère, rapide et facile à tester sans écran (Q44). Elle donne l'usage « à la WSL » directement dans le terminal Kazo.
  DEPS: ['Q7']

Q: 37. Jusqu'où va le presse-papiers entre l'invité et Kazo?
  CTX: Kazo a un seul SystemClipboard, multi-format, qui marque l'origine de chaque contenu (Guest, GuestNative, Host). La copie vers l'ordinateur réel passe par writeText pendant le geste de l'usager (ADR 0065). Côté invité, trois transports possibles: les commandes texte du backdoor VMware (dans v86 depuis le 2026-06-14), vdagent sur un port virtio-serial (dont le nom est à patcher dans v86), ou un agent maison. Une copie faite dans l'invité arrive de façon asynchrone et risque de rater la fenêtre où le navigateur accepte d'écrire dans le presse-papiers de l'ordinateur (non vérifié).
  *A) A) En v1, du texte dans les deux sens (nouvelle origine « VM » dans SystemClipboard), et le glisser-déposer de fichiers Kazo vers l'invité par le dossier partagé ou une injection; les images et les fichiers par presse-papiers plus tard
   B) B) Texte, images et fichiers dès la v1
   C) C) Pas de presse-papiers en v1
   D) D) Autre (précise)
  WHY: Le texte est déjà pris en charge par v86 et par Kazo. Les images et les fichiers demandent un agent invité.
  DEPS: ['Q29']

Q: 38. Comment gère-t-on le son?
  CTX: Le SDK n'a pas d'API pour jouer du son; les sons de l'OS passent par osSounds.js dans Platform.Blazor. v86 joue le son avec un AudioWorklet. Côté pilotes, on vise SB16 pour Windows 9x/XP et HDA pour Windows 7 et plus ainsi que Linux. Le modèle recommandé passe par un anneau SharedArrayBuffer, et l'isolation cross-origin est déjà active dans Kazo.
  *A) A) Le son est géré dans le JS de l'app (AudioWorklet + anneau SharedArrayBuffer), avec un bouton muet et un volume dans la fenêtre de la VM; un ADR futur ajoutera une API son au SDK (volume système, mélangeur)
   B) B) Créer d'abord une API son dans le SDK Kazo
   C) C) Pas de son en v1
   D) D) Autre (précise)
  WHY: Ça ne bloque pas la v1 et ça suit le modèle de v86. Une API SDK profiterait ensuite à toutes les apps.
  DEPS: ['Q4']

Q: 39. Comment partage-t-on la souris et le clavier entre Kazo et la VM?
  CTX: Kazo n'utilise pas requestPointerLock. Le verrouillage du clavier ne marche que dans Chromium, en plein écran. Kazo intercepte certains raccourcis (Alt+Tab et autres). Pour une souris absolue, trois options: le backdoor VMware (déjà dans v86), la tablette USB (pilote HID intégré à Windows) ou virtio-tablet.
  *A) A) Une capture explicite: un clic dans la VM capture la souris et le clavier (pointeur verrouillé si la souris est relative), avec une combinaison d'échappement affichée (par exemple Ctrl + Alt droite); souris absolue par défaut quand l'invité la supporte; Kazo laisse passer ses raccourcis pendant la capture
   B) B) Pas de capture: souris absolue seulement, et les raccourcis de Kazo ont toujours priorité
   C) C) Autre (précise)
  WHY: C'est le comportement que les gens connaissent dans les logiciels de VM. On peut toujours en sortir, et l'invité reçoit quand même ses raccourcis.
  DEPS: ['Q34']

## [4] Performance et plateformes
Q: 40. Quels navigateurs doivent tout supporter?
  CTX: Ce qui manque ailleurs que dans Chromium:
- Memory64: dans Safari seulement derrière un drapeau (features.json du 2026-11-02).
- Verrouillage du clavier: Chromium seulement.
- hostfs: Chrome et Edge sur ordinateur seulement.
- COEP credentialless: absent de Safari, où Kazo retombe sur require-corp.
- Boxedwine demande Safari 18.4 ou plus.
- WebGL dans un OffscreenCanvas: Safari 17 seulement, avec des bugs.

Pour le développement: l'isolation n'est pas active sur localhost ni 127.0.0.1; il faut utiliser 127.0.0.2.
  *A) A) Chrome et Edge sur ordinateur = la cible de référence (tout fonctionne); Firefox sur ordinateur est supporté pour le 32-bit; Safari et le mobile au mieux (32-bit, sans garantie)
   B) B) Parité complète exigée entre Chrome, Firefox et Safari
   C) C) Chrome et Edge seulement
   D) D) Autre (précise)
  WHY: Chromium a toutes les API nécessaires (Memory64, verrouillage du clavier, File System Access, credentialless). Exiger la parité avec Safari bloquerait le x64 et compliquerait la capture du clavier.
  DEPS: ['Q2', 'Q9']

Q: 41. Quelles machines vise-t-on, et comment définit-on « assez rapide »?
  CTX: Aucune mesure de performance n'existe, ni dans MsDos.Clone ni dans les rapports. Seulement des anecdotes: un Vista 32-bit sous v86 a pris environ une heure à démarrer sur un Chromebook (2021); halfix tourne à 10-30 MIPS dans un navigateur. Sur les machines de 8 Go, les navigateurs refusent souvent d'allouer plusieurs Go bien avant les plafonds théoriques de 4 et 16 Gio.
  *A) A) Des critères mesurables par jalon (temps de démarrage, MIPS, latence des entrées, RAM maximale), fixés après un banc d'essai initial; machines de référence: 8 Go de RAM pour le 32-bit (VM de 1 Go ou moins), 16 Go pour le x64 expérimental
   B) B) Des cibles chiffrées dès maintenant, sans mesure
   C) C) Pas de cibles: « au mieux »
   D) D) Autre (précise)
  WHY: Sans aucune mesure, des chiffres fixés maintenant seraient arbitraires. Un banc d'essai au jalon 0 rend les critères vérifiables, y compris en CI.
  DEPS: ['Q2']

## [5] Structure du projet et conventions
Q: 42. Comment organise-t-on la solution Os-x86?
  CTX: MsDos.Clone a quatre projets: Core (net8.0), Blazor, WinForms et Tests (xUnit). Dans Kazo, une app part du gabarit Os-AppTemplate (kazoos-solution), avec [AppManifest] et la cible KazoOsPack. Webamp sert de modèle: du C#, un module JS et une librairie vendorisée dans wwwroot/lib. Avec un moteur en JS/wasm, une interface WinForms n'a plus vraiment de sens (sauf dans une WebView2). Un hôte de développement autonome contournerait aussi le problème d'isolation sur localhost.
  *A) A) Une solution .NET 10 tirée d'Os-AppTemplate: Abstractions (IVirtualMachine: disques, partage, réseau, affichage, état), Engines.V86 (JS/wasm vendorisé + pont vers le worker), l'app Kazo (fenêtre, commandes, intégrations), un hôte Blazor autonome pour le développement et les démos hors de Kazo, et Tests; pas de WinForms
   B) B) Reproduire exactement MsDos.Clone (Core, Blazor, WinForms, Tests)
   C) C) Un seul projet
   D) D) Autre (précise)
  WHY: Ça reprend la séparation cœur/adaptateurs de MsDos.Clone dans le format attendu par le Store. L'hôte autonome permet de développer et de tester sans lancer tout Kazo.
  DEPS: ['Q4', 'Q5', 'Q12']

Q: 43. Os-x86 suit-il les conventions de Kazo OS, y compris l'ADR 0135?
  CTX: Conventions de Kazo:
- l'ADR avant le code;
- la documentation en français d'abord, avec un miroir en anglais;
- features.md et une entrée dans changes/unreleased à chaque PR;
- l'API publique suivie dans PublicAPI.Unshipped.txt.

L'ADR 0135 interdit toute mention de jeux dans le code, la documentation, les plans, les ADR et la mémoire de Kazo. La seule exception est features.md, pour les apps portées (ADR 0192). Or les émulateurs sont souvent présentés pour les jeux, et MsDos.Clone contient des jeux tiers dans DOS_Files.
  *A) A) Les mêmes conventions dans Os-x86 (ADR, français et anglais, journal des changements), et l'ADR 0135 s'applique aussi à Os-x86 et à sa fiche Store (présentée comme « applications et systèmes patrimoniaux »)
   B) B) Les conventions de Kazo dans Os-x86, mais l'ADR 0135 reste limité au dépôt Kazo OS
   C) C) Des conventions allégées pour Os-x86
   D) D) Autre (précise)
  WHY: Os-x86 sera publié dans le même Store et ses ADR seront lus avec ceux de Kazo; une règle unique évite les ambiguïtés.
  DEPS: []

## [5] Stratégie de tests
Q: 44. Comment teste-t-on Os-x86?
  CTX: MsDos.Clone compte environ 678 tests, dont 323 qui rejouent les SingleStepTests du 8088 (MIT), avec 1,25 Go de données commitées, et aucune mesure de performance. Avec v86, les tests du CPU opcode par opcode relèvent du projet amont; nos risques sont dans l'intégration: 9p avec le VFS, réseau, stockage OPFS, interface. Les suites 80286 et 80386 couvrent le mode réel seulement (paquets NuGet d'OpenRakis), donc elles ne serviraient qu'à un cœur maison. Tes règles exigent 80 % de couverture pour le code C#.
  *A) A) Tests unitaires C# (serveur 9p, dispatcher des en-têtes PE/ELF, politique réseau), tests d'intégration qui démarrent des images de référence sans écran (console série, attente de l'invite), tests E2E Playwright dans Kazo, et bancs de performance en CI; les images de test sont téléchargées et vérifiées par hash, jamais commitées
   B) B) Reprendre la méthode de MsDos.Clone (SingleStepTests commités)
   C) C) Des tests manuels seulement au début
   D) D) Autre (précise)
  WHY: On teste là où sont les risques, sans gonfler le dépôt de plusieurs Go, et on respecte ta cible de 80 % de couverture.
  DEPS: ['Q4', 'Q42']

## [6] Jalons
Q: 45. Dans quel ordre livre-t-on?
  CTX: Ce séquencement découle des choix recommandés plus haut. Chaque jalon valide un pont Kazo à la fois: affichage, fichiers, stockage, réseau, puis lancement des exécutables.
  *A) A) M0: ADR et bancs d'essai; M1: app gestionnaire + v86 avec un Linux console dans une fenêtre Kazo; M2: dossier partagé 9p, persistance OPFS et instantanés; M3: réseau (simulé hors ligne + Wisp); M4: Windows 32-bit (apporte ton ISO), ReactOS, dispatcher des .exe et branchement de Boxedwine; M5: essai de Linux x64; M6: Windows 7 x64 expérimental
   B) B) Windows d'abord (M1 = Windows XP ou 2000 dans v86)
   C) C) Les .exe d'abord (M1 = Boxedwine branché sur le double-clic)
   D) D) Autre (précise)
  WHY: Chaque jalon livre quelque chose d'utilisable et on commence par le moins risqué: Linux s'intègre sans pilote et ses images sont libres. Le x64 arrive après les mesures, comme tu le souhaites, sans bloquer le reste.
  DEPS: ['Q1', 'Q2', 'Q4', 'Q11']

