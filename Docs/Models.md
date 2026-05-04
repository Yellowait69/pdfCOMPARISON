# Modèles de Données (Models)

Ce fichier centralise l'ensemble des structures de données (POCO) et énumérations utilisées par l'application pour transporter l'information entre l'extraction, l'analyse et le rendu PDF[cite: 18]. Il se trouve dans l'espace de noms `PDFComparison.Models`[cite: 18].

---

## 🎨 Styles Visuels

### `MarkupStyle` (Énumération)
Définit les différentes manières de dessiner une annotation graphique sur le PDF[cite: 18] :
*   `Strikethrough` : Pour barrer le texte[cite: 18].
*   `Underline` : Pour souligner le texte[cite: 18].
*   `Box` : Pour encadrer le texte[cite: 18].
*   `Highlight` : Pour surligner le texte (le style principalement utilisé pour les blocs de différences)[cite: 18].

---

## 📝 Synthèse Textuelle (Logique)

Ces classes sont utilisées pour construire l'analyse logique et alimenter le rapport de synthèse global.

### `DiffSummaryBlock`
Représente un bloc de différence logique (une phrase ou un paragraphe qui a été modifié)[cite: 18]. Il contient :
*   `Type` : Le type de changement (ex: Ajout, Suppression) basé sur l'énumération `ChangeType` de DiffPlex[cite: 18].
*   `OldText` et `NewText` : Le texte avant et après la modification[cite: 18].
*   `ContextBefore` et `ContextAfter` : Les phrases intactes qui entourent la modification, ce qui permet à l'utilisateur de comprendre où la modification s'est produite dans le contrat[cite: 18].
*   `SourceImage` et `TargetImage` : Des tableaux d'octets (`byte[]`) optionnels, prévus pour stocker d'éventuelles captures visuelles des zones modifiées[cite: 18].

### `DocumentDiffSummary`
Représente le résumé complet des différences pour une seule paire de documents[cite: 18]. Il inclut :
*   `DocumentName` et `Language` : Le nom du document cible et la langue détectée[cite: 18].
*   `ReportFileName` : Le chemin ou le nom du fichier de rapport PDF individuel généré pour cette paire[cite: 18].
*   `Blocks` : Une liste contenant tous les objets `DiffSummaryBlock` associés à ce document[cite: 18].

---

## 📐 Données Spatiales (Géométrie)

Ces structures lient le texte abstrait à sa position physique exacte sur les pages du PDF pour permettre le dessin des annotations.

### `PdfWordInfo`
Contient les informations d'un mot extrait[cite: 18] :
*   `Text` : La chaîne de caractères du mot[cite: 18].
*   `Letters` : La liste détaillée (`IReadOnlyList<Letter>`) des objets de base de la librairie PdfPig qui composent le mot[cite: 18].
*   `PageNumber` : La page sur laquelle se trouve le mot[cite: 18].

### `LetterLoc`
Une structure ultra-optimisée (`readonly record struct`) stockant les coordonnées d'un caractère unique pour le moteur de rendu visuel[cite: 18]. Elle encapsule :
*   `BoundingBox` : Le rectangle de délimitation physique de la lettre (`PdfRectangle`)[cite: 18].
*   `PageNumber` : Le numéro de la page[cite: 18].
*   `BaselineY` : La coordonnée Y de la ligne de base du texte[cite: 18].
*   `FontSize` : La taille de la police[cite: 18].

---

## 📦 Résultats d'Analyse

Ces classes encapsulent les résultats finaux retournés par le moteur de comparaison une fois l'analyse terminée.

### `VisualHighlights`
Stocke les coordonnées des éléments à annoter visuellement sur les rapports PDF individuels[cite: 18].
*   `SourceRed` : Liste des lettres (`LetterLoc`) appartenant au document source qui ont été supprimées ou modifiées[cite: 18].
*   `TargetRed` : Liste des lettres (`LetterLoc`) appartenant au document cible qui ont été ajoutées ou modifiées[cite: 18].

### `DiffAnalysisResult`
L'objet global et final retourné par le `PdfDiffAnalyzer` à l'orchestrateur[cite: 18]. Il regroupe tout le fruit de l'analyse :
*   `DifferencesCount` : Le nombre total d'erreurs/différences détectées[cite: 18].
*   `Summary` : L'objet `DocumentDiffSummary` contenant l'analyse textuelle logique[cite: 18].
*   `Highlights` : L'objet `VisualHighlights` contenant les coordonnées spatiales prêtes pour le dessin[cite: 18].