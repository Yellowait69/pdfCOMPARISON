# PdfDiffAnalyzer

Le `PdfDiffAnalyzer` est le cerveau analytique de l'application[cite: 9]. Son rôle est de transformer les données brutes extraites des PDF en une analyse structurée des différences, en combinant à la fois une approche textuelle (logique) et une approche spatiale (visuelle)[cite: 9].

Il prépare les données qui serviront à la fois à l'affichage dans l'interface utilisateur et à la génération des rapports[cite: 9].

---

## 🛠️ Dépendances

Ce service centralise l'intelligence de trois autres composants spécialisés[cite: 9] :
*   **`IPdfLayoutSanitizerService`** : Pour normaliser les caractères et regrouper les mots extraits en lignes cohérentes selon leur position Y[cite: 9].
*   **`ITextDiffSummaryService`** : Pour générer le résumé textuel des modifications (blocs de texte ajoutés ou supprimés)[cite: 9].
*   **`IVisualHighlightMatcherService`** : Pour faire correspondre les différences textuelles avec les coordonnées réelles des lettres dans le document[cite: 9].

---

## 🚀 Processus d'Analyse (`AnalyzeDifferences`)

La méthode `AnalyzeDifferences` suit un flux de travail rigoureux en plusieurs étapes pour garantir une précision maximale[cite: 9] :

### 1. Initialisation et Détection de la Langue
Le service initialise le résultat et tente d'extraire le code langue (ex: FR, EN) à partir de la clé de correspondance (`MatchKey`) du document[cite: 9]. Par défaut, il utilise "ND" (Non Déterminé)[cite: 9].

### 2. Analyse Logique (Textuelle)
*   Il nettoie les sources de texte via le `Sanitizer`[cite: 9].
*   Il appelle le `TextSummaryService` pour obtenir une liste de `DiffSummaryBlock`[cite: 9].
*   **Objectif** : Savoir *ce qui* a été modifié textuellement[cite: 9].

### 3. Analyse Spatiale (Visuelle)
C'est l'étape la plus complexe :
*   Il demande au `Sanitizer` de regrouper tous les mots extraits (objets `PdfWordInfo`) en lignes physiques (`GroupIntoLines`)[cite: 9].
*   Il reconstruit un texte temporaire à partir de ces lignes via la méthode privée `BuildDiffText`[cite: 9].
*   Il utilise **DiffPlex** pour comparer ces deux versions reconstruites ligne par ligne[cite: 9].
*   Enfin, il passe ce modèle de comparaison au `VisualMatcherService` qui va lier les différences détectées aux coordonnées `LetterLoc`[cite: 9].

---

## ⚙️ Reconstruction de Lignes (`BuildDiffText`)

Cette méthode privée est essentielle pour la précision visuelle[cite: 9]. Elle prend la structure complexe des lignes regroupées spatialement et les transforme en une chaîne de caractères simple, tout en respectant l'ordre de lecture[cite: 9] :
*   Elle ajoute des espaces entre les mots d'une même ligne[cite: 9].
*   Elle ajoute des sauts de ligne (`\n`) entre chaque bloc horizontal détecté[cite: 9].

---

## 📦 Type de Retour (`DiffAnalysisResult`)

Le service retourne un objet complet contenant :
1.  **Summary** : Le nom du document, la langue et la liste des blocs de texte modifiés[cite: 9].
2.  **DifferencesCount** : Le nombre total de différences détectées[cite: 9].
3.  **Highlights** : Les listes de coordonnées (`LetterLoc`) permettant de dessiner les zones rouges (suppressions) et vertes (ajouts)[cite: 9].