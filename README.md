# quadri-size

Analyseur d'espace disque pour Windows. Quadri-Size scanne un lecteur ou un dossier et affiche
ce qui prend de la place, sous forme d'arbre dépliable : chaque dossier montre sa taille et
son pourcentage par rapport au dossier parent, jusqu'aux fichiers.

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

1. Choisis un lecteur dans la liste, ou un dossier avec **Parcourir…** (tu peux aussi taper
   un chemin puis appuyer sur Entrée).
2. Clique sur **Analyser**. Tu peux arrêter un scan avec **Annuler** : les résultats partiels
   restent affichés.
3. Déplie les dossiers avec **+**. Ils sont triés du plus gros au plus petit. Les barres bleues
   sont des dossiers, les grises des fichiers.
4. Clic droit sur une ligne : **Ouvrir dans l'Explorateur** ou **Copier le chemin**.

Certains dossiers système ne sont lisibles qu'en administrateur : leur nombre s'affiche dans
la barre d'état. Lance l'application en administrateur pour un résultat complet.

## Fonctionnement

- `src/Scanner.cs` parcourt l'arborescence dans un thread séparé et additionne les tailles.
  Les jonctions et liens symboliques ne sont pas suivis, pour ne pas compter deux fois les
  mêmes fichiers.
- `src/MainForm.cs` affiche l'arbre. Les sous-éléments d'un dossier ne sont créés qu'au moment
  où on le déplie (500 au maximum, le reste est résumé sur une ligne).
- La taille affichée est la taille réelle des fichiers, pas la place occupée sur le disque.
