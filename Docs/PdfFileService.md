# PdfFileService

Le `PdfFileService` est responsable de la toute première étape du processus de comparaison : l'appairage (matching) des fichiers[cite: 12].

Avant d'analyser le contenu des PDF, l'application doit savoir quel fichier du dossier "Source" correspond à quel fichier du dossier "Cible" (Target). Ce service automatise cette association en se basant sur une convention de nommage stricte[cite: 12].

---

## 🔍 La Règle de Nommage (Regex)

Pour associer les fichiers, le service ne se base pas sur des noms de fichiers strictement identiques, mais extrait une "clé" unique (MatchKey) à la fin du nom du fichier[cite: 12].

Il utilise pour cela l'expression régulière (Regex) générée à la compilation suivante :
`([A-Z]{2}_\d+_\d+)\.pdf$`[cite: 12]

**Décryptage du format attendu :**
*   `[A-Z]{2}` : Deux lettres majuscules (généralement le code langue, ex: `FR`, `EN`).
*   `_` : Un tiret du bas (underscore).
*   `\d+` : Une suite de chiffres.
*   `_` : Un autre tiret du bas.
*   `\d+` : Une autre suite de chiffres.
*   `\.pdf$` : L'extension exacte du fichier à la toute fin du nom.

*Exemple valide :* `Document_Contrat_FR_1234_5678.pdf` donnera la clé **`FR_1234_5678`**[cite: 12].

---

## 🚀 Mécanisme d'Appairage (`MatchFiles`)

La méthode `MatchFiles` prend en entrée les chemins des deux répertoires (source et cible) et retourne une liste de paires (`List<DocumentPair>`)[cite: 12]. Son exécution est hautement optimisée grâce à l'utilisation d'un dictionnaire[cite: 12] :

### 1. Indexation du Répertoire Cible (Target)
Au lieu de parcourir le dossier cible pour chaque fichier source (ce qui serait très lent), le service commence par indexer le dossier cible[cite: 12].
*   Il liste tous les fichiers `.pdf` du dossier cible[cite: 12].
*   Il applique la Regex pour extraire la clé de chaque fichier[cite: 12].
*   Il stocke ces clés (converties en majuscules pour éviter les problèmes de casse) dans un Dictionnaire mémoire (`targetDict`) où la clé pointe vers le chemin complet du fichier[cite: 12].

### 2. Parcours du Répertoire Source
Ensuite, il boucle sur tous les fichiers `.pdf` présents dans le dossier source[cite: 12].
*   Il extrait la clé du fichier source via la même Regex[cite: 12].
*   Si le fichier possède une clé valide, il interroge le dictionnaire cible (`targetDict.TryGetValue`) pour voir si un fichier avec la même clé existe en face[cite: 12].

### 3. Création des Paires
Il instancie un nouvel objet `DocumentPair` avec la clé extraite, le chemin du fichier source, et le chemin du fichier cible (qui sera `null` si aucun fichier correspondant n'a été trouvé)[cite: 12]. Ces paires sont ensuite envoyées à l'orchestrateur pour commencer la comparaison textuelle[cite: 12].