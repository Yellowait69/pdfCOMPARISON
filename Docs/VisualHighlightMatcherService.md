# VisualHighlightMatcherService

Le `VisualHighlightMatcherService` est le composant critique qui fait le pont entre l'analyse de texte logique et le rendu visuel dans le PDF[cite: 15].

Son rôle est de prendre les résultats du moteur de comparaison textuelle (DiffPlex) et de les faire correspondre précisément avec les coordonnées physiques (`LetterLoc`) des mots sur les pages[cite: 15]. L'objectif final est de retourner un objet `VisualHighlights` contenant les zones exactes à surligner en rouge (suppressions) et en vert (ajouts)[cite: 15].

---

## 🧠 Le Défi Technique

Le moteur `DiffPlex` compare des lignes de texte entières. Si un seul mot change dans une phrase, `DiffPlex` marque l'ancienne ligne entière comme "Modifiée" et la nouvelle ligne entière comme "Modifiée"[cite: 15].
Si le service se contentait de surligner aveuglément ces lignes, des phrases entières seraient encadrées en rouge et vert pour une simple virgule modifiée.

Pour résoudre ce problème, ce service exécute un algorithme de **réconciliation intelligente** qui "sauve" les mots identiques au sein des lignes modifiées afin de ne surligner que la vraie différence[cite: 15].

---

## 🚀 L'Algorithme de Réconciliation (`GenerateHighlights`)

L'algorithme se déroule en trois grandes étapes :

### 1. Extraction et Alignement (Flattening)
*   Le service parcourt le modèle de différences ligne par ligne (`diffLinesModel.NewText.Lines.Count`)[cite: 15].
*   Il extrait tous les mots des lignes marquées comme supprimées ou modifiées et les place dans une liste globale `globalDeletes`[cite: 15].
*   Il fait de même pour les lignes insérées ou modifiées en les plaçant dans `globalInserts`[cite: 15].
*   Chaque mot stocké conserve son texte nettoyé (`CleanText`), ses coordonnées (`Letters`), et son numéro de ligne logique (`LineIndex`)[cite: 15].

### 2. Les 3 Passes de Filtrage (Matching)
Le service tente ensuite de faire correspondre les mots de `globalDeletes` avec ceux de `globalInserts` pour les marquer comme "inchangés" (tableaux de booléens `matchedOld` et `matchedNew`)[cite: 15].

*   **Passe A (Correspondance de séquences parfaites) :** Le service cherche des séquences de mots identiques (longueur >= 2) qui se trouvent sur la même ligne logique (`currentLineIndex`) ou une ligne adjacente (différence d'index <= 1)[cite: 15]. Si une correspondance parfaite est trouvée, toute la séquence est ignorée (marquée `true`)[cite: 15].
*   **Passe B (Séquences partielles inter-lignes) :** Il cherche des suites d'au moins 2 mots identiques qui auraient pu glisser légèrement lors de la reconstruction de la ligne (différence de `LineIndex` <= 1)[cite: 15].
*   **Passe C (Mots isolés proches) :** Pour les mots restants, s'il trouve un mot identique dans la source et la cible sur le même index de ligne, il vérifie s'ils sont physiquement au même endroit sur la page grâce à la méthode `IsLocallyClose`[cite: 15].

### 3. Validation de Proximité Spatiale (`IsLocallyClose`)
Cette méthode de vérification très rapide (`AggressiveInlining`) s'assure que deux mots identiques ne sont pas des faux positifs[cite: 15] :
*   Ils doivent être sur la même page (`PageNumber`)[cite: 15].
*   Ils doivent être sur la même ligne horizontale physique (différence d'axe Y `BaselineY` inférieure à `15.0`)[cite: 15].
*   Ils doivent être proches horizontalement (différence d'axe X `BottomLeft.X` inférieure à `300.0`)[cite: 15].

### 4. Génération du Résultat
Après ces trois passes de nettoyage rigoureuses :
*   Tous les mots de la liste `globalDeletes` qui n'ont pas trouvé de jumeau sont ajoutés à `highlights.SourceRed` (ce sont de vraies suppressions)[cite: 15].
*   Tous les mots de la liste `globalInserts` qui n'ont pas trouvé de jumeau sont ajoutés à `highlights.TargetRed` (ce sont de vrais ajouts)[cite: 15].
*   L'objet final est renvoyé pour être dessiné sur le PDF[cite: 15].