# Série 8 — envoyée le 2026-11-10

La série ciblée demandée dans tes consignes (§2 « Répartition détaillée à proposer » et §3) : réglages, profils, partage, opérations admin et permissions, plus les détails que tes consignes laissent « à préciser ». Elle suit la série 7 (numéros 120 à 139). Même règle de réponse. Mots techniques : voir le début de la série 7.

---

### Réglages et profils

**120. Où vivent les réglages d'une VM?**
> Tes consignes disent qu'Os-x86 « conserve les configurations système » et que BoxedWine « porte les profils et configurations utilisateur », tout en exigeant que `x86` et l'API voient les mêmes données. 72.3 (ouverte) mettait tes réglages de VM dans Os-x86.

- A) Os-x86 garde tout réglage qui change la VM ; BoxedWine garde seulement ses préférences d'interface ✨(Recommandé)
  - Dans Os-x86 (par usager) : mémoire, disque, carte réseau, son, clavier, périphériques, Linux par défaut, décisions de permissions, et les options propres à Wine comme réglages opaques de la bouteille (29 C tient : aucun type Wine dans l'interface). Dans Os-x86 (machine, admin) : limites, règles imposées, images partagées. Dans BoxedWine (par usager) : bibliothèque, fichiers récents, apparence, tutoriel vu. Un profil (121) est un fichier que BoxedWine ou `x86` importe et qui écrit ces réglages dans Os-x86. Ainsi `x86` et BoxedWine modifient le même réglage au même endroit.
- B) Comme A, mais les options propres à Wine (version de Windows imitée, surcharges de DLL) restent dans BoxedWine, par bouteille
  - `x86` ne les verrait pas ; cohérent avec 29 C (Os-x86 sans type propre à Wine).
- C) Autre : (Spécifiez)

**121. Qu'est-ce qu'un « profil partageable », et que veut dire le modèle « Linux avec Wine »?**
> Tes consignes parlent de « configurations utilisateur partageables par profil » sans le définir, et de modèles prêts « Linux avec Wine et FreeDOS » (§8). Dans le journal, Wine vit dans la VM Programmes Windows gérée par Os-x86 (D24), pas dans ton Linux.

- A) Un profil = un modèle de VM exportable (réglages et liste de logiciels, sans disque) ; « Linux avec Wine » = le modèle Programmes Windows ✨(Recommandé)
  - Un fichier léger : genre de VM, mémoire, réseau, son, clavier, options de Wine, programmes à ajouter à la bibliothèque. Il se partage par export/import de fichier, entre tes appareils ou avec d'autres personnes. Il ne contient ni disque, ni bouteille, ni décision de permission (D61.1). Ça précise ton §2 : les profils d'usager sont à BoxedWine ; les modèles prêts de §8 sont des profils livrés par Os-x86, importables par BoxedWine ou `x86` ; « Linux avec Wine » désigne la VM Programmes Windows (D24), pas Wine installé dans ton Linux.
- B) Comme 102 B : un profil = une VM entière (disque compris), partagée entre les usagers d'une machine
- C) Autre : (Spécifiez)

### Admin

**122. Où l'admin règle-t-il les limites et son veto?**
> Os-x86 n'a pas d'app (63 A), mais garde des réglages machine, des limites et un veto sur les décisions des usagers (D23, §6). Tes consignes prévoient une section « Fonctionnalités » dans les Paramètres (D38). Fait vérifié : pour une commande lancée du terminal, Kazo demande aujourd'hui l'accord admin par une bannière dans le terminal et des boîtes de dialogue de l'app Terminal, pas par son écran sécurisé plein écran.

- A) Une page d'Os-x86 dans Paramètres → Fonctionnalités, plus `x86 admin` ✨(Recommandé)
  - La page (fournie par la Feature, affichée par les Paramètres avec élévation) : limites de mémoire et de stockage, règles réseau imposées, images partagées, refus par programme ou par usager. `x86 admin …` fait pareil en ligne de commande ; l'ADR « permissions déclarées par un fournisseur » fait passer ses vérifications par l'écran sécurisé de l'OS, même pour une commande (jamais un mot de passe tapé dans le terminal). BoxedWine ne touche jamais à ces réglages.
- B) `x86 admin` seulement, sans page
- C) Dans BoxedWine, avec élévation
- D) Autre : (Spécifiez)

**123. L'accord admin pour BoxedWine : à l'installation, ou au premier lancement?**
> D43 disait « à l'installation de BoxedWine ». Tes consignes disent « l'accord administrateur commun à l'installation de Kazo … accepté sous condition », ce qui peut vouloir dire l'une ou l'autre. Fait vérifié : Kazo demande les permissions dangereuses d'une app ordinaire au premier lancement, une fois pour toute la machine (ADR 0049) ; un refus d'usager l'emporte sur l'accord machine (règle actuelle ; la PR #428 la modifie).

- A) Au premier lancement, pour la machine, comme Kazo le fait déjà ✨(Recommandé)
  - Ça change D43. La condition de tes consignes tient : chaque opération sensible est revérifiée à chaque appel par Os-x86 (liste à la question 124). Conséquence proposée : sans accord, BoxedWine s'ouvre quand même, mais ses programmes n'ont pas de réseau réel et il ne règle pas les VM.
- B) À l'installation de BoxedWine (D43), par un ADR Kazo qui enregistre les permissions dangereuses à l'approbation d'installation
- C) Autre : (Spécifiez)

**124. Les opérations qu'Os-x86 revérifie à chaque appel : cette liste te convient-elle?**
> Tes consignes demandent la liste des opérations sensibles, vérifiées à chaque fois, quel que soit l'appelant. Les points marqués « déjà décidé » sont rappelés pour que la liste soit complète ; les retirer ne les rouvre pas.

- A) Cette liste ✨(Recommandé)
  1. Régler, supprimer ou partager une VM d'un autre usager ou une VM commune (selon 102 et 103) : admin seulement. Démarrer ou arrêter une VM commune : tout usager admis, un à la fois (103).
  2. Arrêter une VM dont des données ne sont pas enregistrées : confirmation dans une fenêtre d'Os-x86 (66 A.3).
  3. Supprimer un disque, une bouteille, un point de restauration : confirmation dans une fenêtre d'Os-x86, jamais par une option de commande (ça exclut les options « répondre d'avance » de 66 A.5 pour ces opérations).
  4. Importer une image partagée : admin (selon 128). Importer son propre disque : usager (D34).
  5. Changer une limite admin, une règle imposée, un refus machine : admin.
  6. Accorder une permission réseau à un programme : seule la fenêtre de permission ou la page des règles, jamais l'API (déjà décidé, D61.2).
  7. Lancer une commande ou un shell dans une VM que l'app n'a pas créée : permission de 105, ou « gérer les sous-systèmes ». Dans une bouteille : selon 134 (rien en V1 si l'héritage est reporté).
  8. Ouvrir un dossier de Kazo à une VM au-delà de ce que 107 prévoit : fenêtre d'Os-x86.
  9. Exporter un disque ou une bouteille : confirmation (le fichier contient les programmes et leurs données).
  10. Changer l'app par défaut d'une extension : confirmation de Kazo (déjà décidé, D61.10).
- B) Cette liste, sauf : (donne les numéros)
- C) Autre : (Spécifiez)

**125. Le mode invité de Kazo : que peut-il faire dans Os-x86?**
> Fait vérifié : le mode invité de Kazo est un admin sans mot de passe, dont l'élévation est un simple clic. Les garanties « veto admin » et « accord admin » ne valent donc rien pour lui.

- A) Toutes les opérations admin d'Os-x86 sont refusées en mode invité ; l'invité garde ses VM comme un usager ordinaire ✨(Recommandé)
  - Pas d'image partagée, pas de limite, pas de règle imposée depuis ce mode. À dire dans la spécification et à proposer à Kazo comme règle générale pour les Features.
- B) Le mode invité agit comme un admin, comme partout dans Kazo
- C) Autre : (Spécifiez)

### Images et téléchargements

**126. Quelle est la portée du consentement « télécharger l'image » et de « ne plus demander »?**
> Tes consignes demandent avant le premier téléchargement d'une image, avec « ne plus demander », et laissent la portée ouverte. Les images sont partagées par la machine (§8). Le téléchargement est fait par Os-x86, jamais par BoxedWine (D61.7, et ton §8 qui demande de réconcilier les deux).

- A) Par usager, pour les images officielles d'Os-x86 ; « ne plus demander » réglable dans la page des règles ✨(Recommandé)
  - La question s'affiche une fois par usager, dans une fenêtre d'Os-x86, avec la taille et la source. Une image déjà présente sur la machine ne demande rien. « Ne plus demander » vaut pour les images officielles seulement, pas pour une image apportée ni pour une mise à jour (qui suit §10 et D56).
- B) Par machine : la première réponse d'un usager vaut pour tous
- C) Autre : (Spécifiez)

**127. Mise à jour indispensable : que se passe-t-il?**
> Tes consignes : une nouvelle image se propose (« maintenant » ou « à la fermeture »), sans bloquer l'usage. D56 règle 1 et D44 (acceptées) prévoient des versions minimales : une app peut déclarer la version d'image dont elle a besoin. Tu demandes de distinguer « disponible » de « indispensable » et de valider le comportement. Le retour arrière d'une mise à jour ordinaire et « attendre un état sûr » pour « à la fermeture » viennent dans le plan, à valider là.

- A) Deux cas « indispensables », tous deux annoncés avant : Os-x86 ne sait plus démarrer l'ancienne image, ou une app exige une image plus récente (D44) ✨(Recommandé)
  - Dans ces cas : fenêtre au lancement, « mettre à jour maintenant » ou « annuler » (pas de « plus tard »), point de restauration automatique avant. Retour arrière vers l'ancienne image possible si l'ancienne version d'Os-x86 l'est aussi (à vérifier dans le Store de Kazo). BoxedWine (56) et `x86` affichent la même chose. Tout le reste est « disponible » et différable.
- B) Un seul cas : le moteur (ça change D44 et D56 règle 1 : une app ne peut plus exiger une image)
- C) Autre : (Spécifiez)

**128. Import d'une image apportée : admin, ou usager?**
> Tes consignes : importer une image Linux « avec l'autorisation d'un administrateur ». D34 laissait l'usager importer et exporter des disques par téléversement. Les deux peuvent coexister.

- A) Admin pour une image partagée (modèle pour tous) ; usager pour son propre disque ou sa propre VM, sans admin ✨(Recommandé)
  - Un disque importé par un usager reste à lui, compte dans l'espace du navigateur (pas de quota admin en V1), et n'a pas d'agent (capacités réduites, affichées, comme le demande ton §8 ; tableau en E7). Une image partagée passe par la page admin de 122 et la vérification d'intégrité.
- B) Admin pour tout import d'image ou de disque (ça change D34)
- C) Autre : (Spécifiez)

### Bibliothèque, comptes, capture, son

**129. Que fait « retirer une entrée de la bibliothèque »?**
> Tes consignes laissent ce point ouvert, et rappellent que la conservation des données à la désinstallation de BoxedWine est décidée.

- A) Retirer = enlever l'entrée et ses raccourcis seulement ; désinstaller et supprimer la bouteille sont deux autres actions ✨(Recommandé)
  - « Retirer de Kazo » enlève l'entrée du menu Démarrer et le raccourci de bureau. « Désinstaller » lance le programme de désinstallation dans la bouteille. « Supprimer la bouteille » efface tout, avec confirmation d'Os-x86 (124.3).
- B) Retirer = enlever l'entrée, ses raccourcis et désinstaller le programme
- C) Autre : (Spécifiez)

**130. La bibliothèque montre-t-elle les programmes trouvés dans tes bouteilles, sans rien ajouter?**
> 57 A (acceptée, avec ta précision) disait : « La bibliothèque liste les programmes trouvés dans les bouteilles. Leurs noms et leurs icônes viennent des programmes eux-mêmes : BoxedWine les traite comme des données non fiables. » Tes consignes refusent la « détection automatique » et la « proposition automatique après installation » comme obligations, et demandent de ne pas présumer le mécanisme de lecture.

- A) Oui : une section « trouvés dans tes bouteilles, pas dans Kazo », pour ajouter en un clic ; rien n'est ajouté sans toi ; désactivable ✨(Recommandé)
  - C'est 57 A puce 1 telle quelle ; le mécanisme de lecture vient dans le plan.
- B) Non : seulement ce que tu ajoutes toi-même (ça change 57 A puce 1)
- C) Autre : (Spécifiez)

**131. À la suppression d'un compte Kazo, ses VM et ses décisions?**
> Fait vérifié : un admin peut supprimer un compte (ADR 0169 de Kazo), avec ou sans son dossier personnel ; rien ne prévient une Feature ; un compte recréé avec le même nom reçoit un nouvel identifiant. Les données d'Os-x86 vivent dans son OPFS, hors du dossier personnel.

- A) Même sort que le dossier personnel : supprimées avec lui, gardées sinon ; Os-x86 range ses données par identifiant d'usager, pas par nom ✨(Recommandé)
  - Demande un petit ADR Kazo « événement de suppression de compte pour les Features ». Un compte recréé ne retrouve pas les VM de l'ancien.
- B) Toujours gardées, supprimables par l'admin dans la page de 122
- C) Autre : (Spécifiez)

**132. À quel niveau se règle la capture de la souris, et quelle touche la libère?**
> Tes consignes décident que les applications peuvent capturer la souris, avec priorité sur les raccourcis de Kazo pendant la capture, et laissent le niveau et la touche à préciser. Faits vérifiés : le navigateur garde Échap : une pression courte libère le pointeur ; Échap maintenu 1,5 s seulement quand le clavier est verrouillé (plein écran, Chromium) ; relancer la capture exige un nouveau clic ; hors plein écran, Ctrl+W et Ctrl+T restent au navigateur ; Firefox et Safari ont depuis peu un verrouillage du clavier par le plein écran que Kazo n'utilise pas encore.

- A) Réglage par VM, surchargé par programme dans BoxedWine ; libération par Ctrl droite seule, réglable, jamais supprimable, affichée à l'écran ✨(Recommandé)
  - La capture ne commence qu'après ton clic dans la fenêtre. Ctrl droite est traitée par Kazo avant tout programme. Pas Ctrl + Alt droite (AltGr sous Windows). ADR Kazo « une fenêtre peut tenir le clavier », avec les deux API de verrouillage. Les raccourcis du navigateur restent hors de portée hors plein écran : affiché à l'usager.
- B) Réglage par programme seulement, dans BoxedWine ; même touche de libération
- C) Autre : (Spécifiez)

**133. Son : quel niveau en V1?**
> Tes consignes veulent le son « dans la mesure où c'est faisable », sans choisir la granularité du muet, et refusent de considérer validé qu'un bouton muet coupe toute la VM. Faits vérifiés : v86 n'a que la Sound Blaster 16 en PCM (pas de musique FM/AdLib des jeux DOS), un seul flux par VM ; un muet par programme demande un mélangeur dans l'invité (PipeWire) piloté par l'agent ; le son de v86 doit passer d'un worker à la page ; v86 a un bogue de son saccadé en cours de correction.

- A) En V1 si E5 le juge acceptable : un flux par VM, muet et volume par VM ; par programme au jalon suivant ✨(Recommandé)
  - Le bouton muet de la vue (55 A) coupe donc sa VM en V1 ; l'étiquette le dit. Conséquences : volume par VM dans BoxedWine et par `x86` ; si E5 échoue, son en V2, coupé par défaut.
- B) Muet et volume par programme dès V1, par un mélangeur dans l'invité piloté par l'agent ; si E5 échoue, son en V2
- C) Son en V2 dans tous les cas
- D) Autre : (Spécifiez)

### Garanties, signalements, divers

**134. Si la console avancée dans une bouteille et l'héritage des permissions sont reportés en V2, quelles garanties V1 retiens-tu?**
> Tes consignes acceptent la console avancée et l'héritage, avec un report V2 possible « si trop complexe », et demandent de valider les garanties V1 dans ce cas. Ça change D58 A puce 3 (`x86` refusait un shell dans Programmes Windows).

- A) En V1 : aucune commande ni shell dans une bouteille par l'API ou par `x86` ; un programme lancé par un programme garde la décision réseau de sa bouteille (D20) ; rien de plus ✨(Recommandé)
  - Un programme enfant ne peut donc pas obtenir plus que sa bouteille. La console avancée arrive avec l'héritage, dans le même jalon.
- B) Console avancée en V1 même sans héritage fin : tout ce qui est lancé dans la bouteille reçoit ses droits (c'est déjà la limite de 50 et 69 A)
- C) Autre : (Spécifiez)

**135. Puis-je ouvrir trois issues dans Kazo-ca/Os pour les failles trouvées dans son code?**
> `StartChild` (créer un processus enfant) ne vérifie aucune permission ; les permissions « sujet » de l'ADR 0040 sont accordées en silence à la connexion ; `filesystem.user-home.write` peut effacer le fichier de refus d'un usager. Ce sont des issues publiques, pas des PR, dans un dépôt dont tes consignes (§18) interdisent la modification de ma propre initiative.

- A) Oui, trois issues, aucune PR ✨(Recommandé)
- B) Non, je les garde dans le rapport
- C) Autre : (Spécifiez)

**136. Le presse-papiers réel (hors du navigateur) : en V1?**
> Tes consignes : texte simple en V1 entre Kazo et l'invité ; l'intégration avec le presse-papiers de ton ordinateur est « distincte », son échéance V1 « n'a pas été tranchée séparément ». Fait vérifié : le navigateur n'accepte une copie vers l'extérieur que pendant ton geste ; une copie faite dans l'invité arrive trop tard et se perd, sauf nouvelle tentative au geste suivant (E9).

- A) En V1, par la nouvelle tentative au geste suivant (petit ajout à l'ADR 0065 de Kazo), si E9 marche ; sinon V2 ✨(Recommandé)
- B) V2 dans tous les cas
- C) Autre : (Spécifiez)

**137. Le journal d'événements : qui voit quoi?**
> Tes consignes (§16) veulent les événements importants dans le journal de Kazo, sans « accès universel » ni journal « réservé aux admins ». Fait vérifié : Kazo n'a ni journal par usager ni visionneuse par usager ; ses journaux d'apps sont lisibles par tous.

- A) Deux niveaux : événements de l'usager (permission accordée, conflit résolu, mise à jour de ses VM) visibles par lui et par les admins ; événements système sans donnée personnelle dans le journal d'app ✨(Recommandé)
  - Demande un ADR Kazo « événements par usager », avec le lien vers la VM ou le programme concerné.
- B) Tout dans le journal d'app de Kazo, lisible par tous (comme aujourd'hui)
- C) Autre : (Spécifiez)

**138. Les déclencheurs de tests entre dépôts : une PR vers Kazo-ca/Os, au moment de l'implémentation?**
> Tes consignes (§17) veulent qu'un changement de Kazo qui touche Os-x86 déclenche ses tests. Fait vérifié : le workflow de Kazo n'a aucun déclencheur vers d'autres dépôts ; les dépôts d'apps épinglent Kazo par sous-module sur une version. Ça demande une PR dans Kazo-ca/Os, que §18 m'interdit de ma propre initiative.

- A) Oui, une PR dédiée dans Kazo-ca/Os, à proposer au moment de l'implémentation, avec son ADR, soumise aux règles de ce dépôt (dont « aucune mention de jeux ») ✨(Recommandé)
- B) Non : Os-x86 et BoxedWine relancent leurs tests eux-mêmes à chaque nouvelle version de Kazo (plus simple, plus tard)
- C) Autre : (Spécifiez)

**139. Quand un programme invité ouvre un lien web ou un document, que se passe-t-il?**
> Les installateurs et programmes ouvrent souvent une page (aide, licence) ou un document avec « l'application par défaut ». Le Linux invité n'a pas de navigateur. Tes consignes (§5) disent seulement qu'à l'intérieur d'une VM, l'ouverture suit l'invité.

- A) Un lien web s'ouvre dans le navigateur de Kazo, un document dans l'app Kazo associée, après une confirmation de Kazo qui montre la cible ; sans réseau accordé, le lien est seulement affiché ✨(Recommandé)
  - Par l'agent, qui remplace l'ouverture par défaut de l'invité. C'est le seul cas où un programme invité fait ouvrir quelque chose dans Kazo ; le presse-papiers (136) ne fait que transporter du texte.
- B) Rien ne sort de l'invité : le lien est affiché, à copier
- C) Autre : (Spécifiez)

---

On retient mes recommandations pour les questions 120 à 139? Une ligne vide ou « ok » veut dire oui ; sinon, réponds par numéro (par exemple « 124 B sauf 3 »).
