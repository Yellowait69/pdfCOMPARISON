# PdfLayoutSanitizerService

Le `PdfLayoutSanitizerService` est un composant essentiel de préparation des données[cite: 14]. Les documents PDF sont notoirement complexes et contiennent souvent des variations typographiques ou des caractères invisibles qui faussent les algorithmes de comparaison[cite: 14].

L'objectif de ce service est d'uniformiser le texte (sanitizing) et de reconstruire la structure physique des lignes (layout) avant que l'analyse des différences ne commence[cite: 14].

---

## 🧹 Nettoyage et Normalisation Typographique

Le nettoyage est effectué en deux étapes pour éviter les "faux positifs" lors de la comparaison.

### 1. Remplacement des Ligatures et Puces (`NormalizeLigaturesAndBullets`)
Les PDF fusionnent souvent certaines lettres pour des raisons esthétiques (ligatures), ce qui trompe les comparateurs de texte[cite: 14]. Cette méthode résout ce problème en amont[cite: 14] :
*   **Ligatures** : Sépare les caractères fusionnés (ex: `ﬁ` devient `fi`, `œ` devient `oe`)[cite: 14].
*   **Puces (Bullet points)** : Convertit tous les symboles de listes (`•`, `▪`, `●`, etc.) en un simple tiret `-` pour uniformiser les énumérations[cite: 14].
*   **Tirets et Guillemets** : Uniformise les différents types de tirets et de guillemets vers des caractères standards ASCII (`-`, `'`, `"`)[cite: 14].

### 2. Méthodes de Nettoyage Spécifiques
*   **`CleanLineForDiff`** : Utilisé pour la comparaison logique globale[cite: 14]. Conserve les espaces mais transforme les espaces insécables (`\u00A0`) en espaces standards[cite: 14].
*   **`CleanWord`** : Utilisé pour la comparaison granulaire des mots[cite: 14]. Plus stricte, elle supprime tous les espaces, les caractères invisibles (ex: Zero-Width Space `\u200B`), transforme les virgules en points (pour les nombres) et convertit tout en minuscules (`ToLowerInvariant`)[cite: 14].

---

## 📐 Reconstruction Géométrique des Lignes (`GroupIntoLines`)

L'algorithme d'extraction brut renvoie une liste de mots ("bag of words"). Pour pouvoir comparer visuellement deux documents, il faut reconstituer des phrases (lignes physiques)[cite: 14].

Cette méthode prend une liste de mots extraits (`PdfWordInfo`) et les regroupe intelligemment :

1.  **Nettoyage initial** : Chaque mot et chaque lettre (Glyphe) est nettoyé via `CleanWord`[cite: 14]. Les lettres qui se superposent exactement (souvent un artefact des PDF pour créer du "faux gras") sont ignorées pour éviter les doublons[cite: 14].
2.  **Groupement par Page** : Le traitement est isolé page par page[cite: 14].
3.  **Alignement Vertical (Axe Y)** : Les mots sont triés par leur hauteur (coordonnée `BaselineY`)[cite: 14]. Si la différence de hauteur entre deux mots est inférieure à `5.0` points, l'algorithme considère qu'ils appartiennent à la même ligne physique[cite: 14].
4.  **Alignement Horizontal (Axe X)** : Une fois la ligne constituée, les mots sont triés de gauche à droite en fonction de leur coordonnée X (`BoundingBox.BottomLeft.X`) pour reconstituer l'ordre de lecture naturel[cite: 14].

**Retour :**
Une liste de lignes, où chaque ligne contient une liste de mots nettoyés associés à leurs coordonnées précises (`LetterLoc`).