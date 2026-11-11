# PdfDrawingService

Le `PdfDrawingService` est le moteur de rendu graphique de l'application[cite: 10]. Il agit comme une couche d'abstraction au-dessus de la librairie **PdfPig** pour faciliter le dessin d'annotations, de textes stylisés et d'éléments d'interface directement dans les fichiers PDF[cite: 10].

---

## 🎨 Fonctionnalités Clés

### 1. Gestion des Polices (`LoadFonts`)
Ce service tente d'abord de charger les polices système **Arial** et **Arial Bold** pour un rendu professionnel[cite: 10].
*   **Sécurité** : Si les polices système sont inaccessibles, il bascule automatiquement sur les polices standards PDF (Helvetica) pour éviter tout plantage[cite: 10].

### 2. Annotation des Différences (`DrawDiffMarkup`)
C'est la fonction principale utilisée pour générer les rapports visuels[cite: 10]. Elle prend une liste de coordonnées de lettres (`LetterLoc`) et applique un style graphique selon la nature du changement[cite: 10] :
*   **Highlight** : Dessine un rectangle de surbrillance autour du texte et ajoute une barre verticale dans la marge gauche pour repérer rapidement la ligne modifiée[cite: 10].
*   **Strikethrough** : Barre le texte[cite: 10].
*   **Underline** : Souligne le texte[cite: 10].
*   **Box** : Encadre le texte[cite: 10].

*Note : Cette méthode utilise le `VisualSegmentHelper` pour regrouper intelligemment les lettres individuelles en segments de lignes cohérents avant de dessiner[cite: 10].*

### 3. Rendu de Texte Avancé
Le service gère deux types de rendu textuel pour les rapports de synthèse :
*   **`DrawTextLines`** : Gère le retour à la ligne automatique (`WrapText`) pour les longs blocs de texte simple[cite: 10].
*   **`DrawMixedTextLines`** : Permet d'écrire des lignes contenant des styles mixtes (mots en gras, couleurs différentes) sur une même ligne, utilisé pour le "Inline Diff"[cite: 10].

### 4. Éléments de Tableau de Bord (`DrawStatBox`)
Utilisé exclusivement dans le rapport global, cette méthode dessine des encadrés statistiques stylisés contenant un libellé gris et une valeur numérique colorée (Bleu, Vert ou Rouge selon le contexte)[cite: 10].

---

## 📐 Algorithmes Internes

### Mesure de précision (`MeasureStringWidth`)
Comme les PDF ne sont pas des fichiers texte fluides, le service doit calculer manuellement la largeur de chaque mot pour savoir quand passer à la ligne[cite: 10]. Il utilise une table de largeurs relatives pour chaque caractère (ex: 'm' est plus large que 'i') afin de garantir que le texte ne dépasse pas des colonnes du rapport[cite: 10].

### Découpage intelligent (`WrapText`)
Découpe les chaînes de caractères en conservant l'intégrité des mots, s'assurant qu'aucun mot n'est coupé en plein milieu lors d'un retour à la ligne[cite: 10].

---

## 📝 Exemple technique
Lorsqu'il dessine une zone de texte modifiée, le service calcule le `strokeWidth` (épaisseur du trait) proportionnellement à la taille de la police originale (`FontSize * 0.08`) pour que l'annotation soit toujours esthétique, quelle que soit la taille du texte dans le PDF[cite: 10].