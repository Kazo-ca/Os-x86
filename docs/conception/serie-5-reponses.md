Réponses de l'usager à la série 5 (verbatim, 2026-11-09) :

49. a) Os-x86 génère le rendu, boxedWine l'affiche. Unpeut comme le bureau et les fenêtres fonctionne. On pourrais voir Os-x86 comme des dll utilisé par BoxedWine.

51. d) Non, BoxedWine reste une application qui utilise la feature Os-x86. Faut voir BoxedWine comme une façon simple d'utiliser Os-x86. On pourrais tout faire par ligne de commande avec seulement Os-X86. Mais Os-X86 n'a pas de paramètres utilisateur, pas de fichier récent, pas d'utilitaire de configuration de disque, machine, réseau, etc ...
52. A) Elle peut demander à BoxedWine dirrectement ou simplement demander au Shell de l'executer et le shell passe par le régistre d'association d'extension.
53. a) Quand le terminal utilise la fonctionalitée Os-x86, il s'occupe aussi d'afficher le rendu. Le terminal devras donc avoir des modifications pour lui permettre d'utiliser Os-x86.

55. a) Lancé depuis le terminal: Il s'occupe aussi de rendre l'application si c'est une application console.

57. a) Chaque application déclare son icone dans le manifest. On vas devoir changer le SDK pour permettre a une application de changer l'icone de la fenètre et de déclarer un raccourci avec une icone. Dans boxedWine, nous allons avoir un element dans le menu pour pouvoir ajouter une application dans Kazo Os. l'utilisateur devras naviguer vers l'executable ou le racourçis (.lnk) et spécifier si il veut que un racourçis soit aussi ajouté sur le bureau (bureau: optionel, menu démarrer: requis). Une nouvelle ADR devras être fait pour que BoxedWine puisse créer un element dans le menu démarrer et changer l'icone de la fenêtre (le button de la barre des taches suit aussi ce changement). Quand on crée un racourçis vers une application géré par BoxedWine, boxedWine ajoute un petit symbole (logiquement son icone) dans l'icone png original dans le bas-droit de l'icone pour savoir que cette application seras ouverte avec BoxedWine.

59. C) On devrais pouvoir avoir plus d'une VM qui s'execute en même temps si les ressources système le permettre. Mais quand une VM tourne, on doit l'afficher à l'utilisateur avec la possibilité de fermer une autre vm, ouvrir cette application dans une vm existante ou créer une nouvelle VM.

Pour le reste je suis d'accord avec tes recommendations.
(=> 50 A, 54 A, 56 A, 58 A, 60 A, 61 A acceptés ; 49 A, 52 A, 53 A, 55 A, 57 A acceptés avec précisions ; 51 D et 59 C ne sont pas mes recommandations.)
