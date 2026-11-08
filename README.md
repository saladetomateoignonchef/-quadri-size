# quadri-size

Analyseur d'espace disque pour Windows. Quadri-Size scanne un lecteur ou un dossier et affiche
ce qui prend de la place, sous forme d'arbre dépliable : chaque dossier montre sa taille et
son pourcentage par rapport au dossier parent, jusqu'aux fichiers. Depuis l'arbre, tu peux
supprimer des fichiers et dossiers ou désinstaller des programmes.

## Compiler

Aucune installation nécessaire : le compilateur C# du .NET Framework 4 est fourni avec Windows.

```bat
build.bat
```

L'application est créée dans `bin\QuadriSize.exe`. Pour compiler et lancer aussi les tests :

```bat
build.bat test
```

## Utilisation

Quadri-Size se lance toujours **en administrateur** : Windows demande l'autorisation à chaque
ouverture. C'est ce qui lui permet de voir tous les dossiers.

1. Clique sur un lecteur dans le menu de gauche, ou sur **Choisir un dossier…**. L'analyse
   démarre tout de suite ; **Annuler** l'arrête en gardant les résultats partiels, **Analyser**
   la relance.
2. Les cartes du haut donnent la taille analysée, le nombre de fichiers et de dossiers, et
   l'espace libre du lecteur.
3. Déplie les dossiers avec la flèche, un double-clic ou les flèches du clavier (→ / ←).
   Ils sont triés du plus gros au plus petit. Les barres turquoise sont des dossiers, les
   grises des fichiers. **Tout replier** revient au premier niveau.
4. L'interrupteur soleil / lune passe du thème clair au thème sombre (le choix est retenu).

### Mettre l'appli à jour : bouton « maj git »

Le bouton **maj git** en haut à droite récupère la dernière version publiée sur GitHub :

1. il regarde s'il y a des nouveautés (`git fetch`) et te les liste ;
2. si tu confirmes, il les télécharge (`git pull`) ;
3. il recompile l'appli (`build.bat`) ;
4. il redémarre sur la nouvelle version.

Si la compilation échoue, l'ancienne version est remise en place. Il faut que Quadri-Size ait
été récupéré avec `git clone` (et que Git soit installé) : c'est le dossier git qui est mis à jour.

### Clic droit sur une ligne

| Action | Raccourci | Effet |
|---|---|---|
| Déplier / replier | Entrée | |
| Ouvrir dans l'Explorateur | | Ouvre le dossier, ou sélectionne le fichier |
| Copier le chemin | | |
| Actualiser ce dossier | F5 | Remesure le dossier (après une désinstallation par exemple) |
| Désinstaller « … » | | Lance le vrai désinstalleur du programme (n'apparaît que sur le dossier d'un programme installé) |
| Mettre à la corbeille | Suppr | Récupérable depuis la corbeille |
| Supprimer définitivement | Maj+Suppr | Irréversible |

Chaque suppression demande une confirmation. Pour enlever un programme, préfère **Désinstaller** :
effacer seulement son dossier laisse des traces dans Windows et peut casser d'autres logiciels.

Par sécurité, ne peuvent pas être supprimés : le dossier `Windows` et tout ce qu'il contient,
les dossiers système eux-mêmes (`Program Files`, `ProgramData`, `Users`, ton dossier perso…),
les éléments système à la racine d'un lecteur (fichier d'échange, corbeille…) et le dossier
analysé lui-même.

## Fonctionnement

- `src/Scanner.cs` parcourt l'arborescence dans un thread séparé et additionne les tailles.
  Les jonctions et liens symboliques ne sont pas suivis, pour ne pas compter deux fois les
  mêmes fichiers.
- `src/Programs.cs` lit la liste des programmes installés dans le registre (la même que
  « Applications installées » dans les Paramètres).
- L'interface est en WPF, avec la même charte graphique que Driver : `src/ui/Theme.xaml`
  (couleurs et styles), `src/ui/MainWindow.xaml` (fenêtre) et `src/ui/Dialog.xaml` (messages).
  Ces fichiers sont intégrés à l'exécutable par `build.bat`.
- `src/MainUi.cs` fait vivre la fenêtre. L'arbre est un tableau « à plat » (`src/Rows.cs`) :
  déplier un dossier insère ses enfants dessous, avec un retrait, pour garder les colonnes
  alignées (500 au maximum, le reste est résumé sur une ligne). Après une suppression, le
  dossier est remesuré et les tailles des dossiers parents sont recalculées.
- `src/Theme.cs` gère le thème clair / sombre (retenu dans `%LOCALAPPDATA%\QuadriSize`) et la
  barre de titre bleu nuit ; `src/Dialogs.cs` les fenêtres de message.
- `src/Updater.cs` fait la mise à jour du bouton « maj git ». Comme Windows interdit d'écraser
  un programme en cours d'exécution, l'exécutable actuel est d'abord renommé en
  `QuadriSize.old.exe`, supprimé au démarrage suivant.
- `src/app.manifest` demande les droits administrateur au lancement.
- La taille affichée est la taille réelle des fichiers, pas la place occupée sur le disque.
