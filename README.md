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

1. Choisis un lecteur dans la liste, ou un dossier avec **Parcourir…** (tu peux aussi taper
   un chemin puis appuyer sur Entrée).
2. Clique sur **Analyser**. Tu peux arrêter un scan avec **Annuler** : les résultats partiels
   restent affichés.
3. Déplie les dossiers avec **+**. Ils sont triés du plus gros au plus petit. Les barres bleues
   sont des dossiers, les grises des fichiers.

### Clic droit sur une ligne

| Action | Raccourci | Effet |
|---|---|---|
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
- `src/MainForm.cs` affiche l'arbre. Les sous-éléments d'un dossier ne sont créés qu'au moment
  où on le déplie (500 au maximum, le reste est résumé sur une ligne). Après une suppression,
  le dossier est remesuré et les tailles des dossiers parents sont recalculées.
- `src/app.manifest` demande les droits administrateur au lancement.
- La taille affichée est la taille réelle des fichiers, pas la place occupée sur le disque.
