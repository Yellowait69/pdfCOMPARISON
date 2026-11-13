# VisualSegmentHelper

La classe `VisualSegmentHelper` est un utilitaire statique (Helper) dédié à la géométrie spatiale du document PDF[cite: 16].

Contrairement aux autres services qui analysent le texte d'un point de vue grammatical ou sémantique, ce Helper ne se soucie que des **coordonnées physiques (X, Y)**. Son but est de transformer une "soupe de lettres" (des caractères isolés) en éléments visuels cohérents : des segments (lignes) et des blocs (paragraphes)[cite: 16].

Il est principalement utilisé par le `PdfDrawingService` pour dessiner des encadrés de surbrillance propres, et par le `PdfComparisonOrchestrator` pour compter le nombre de zones d'erreurs réelles[cite: 16].

---

## 📐 Algorithmes de Regroupement

Le Helper propose deux méthodes principales qui fonctionnent en cascade : d'abord on regroupe horizontalement, puis on regroupe verticalement.

### 1. Regroupement Horizontal : `GetSegments`
Cette méthode prend une liste de lettres individuelles (`LetterLoc`) en vrac et les fusionne en "Segments" horizontaux continus[cite: 16].

**Le processus :**
1.  **Tri intelligent (Sorting)** : Les lettres sont triées de haut en bas (axe Y), puis de gauche à droite (axe X)[cite: 16]. L'algorithme utilise une tolérance d'alignement (`AlignmentTolerance = 5.0m`) pour s'assurer que des lettres très légèrement décalées verticalement (un défaut courant dans les PDF) soient quand même considérées comme étant sur la même ligne[cite: 16].
2.  **Fusion (Merging)** : L'algorithme parcourt les lettres triées. Si deux lettres sont sur la même ligne et que l'espace horizontal qui les sépare est petit (inférieur à 1,5 fois la taille de la police ou 15 points maximum), elles sont fusionnées dans la même "boîte" ou segment[cite: 16].
3.  **Résultat** : La méthode retourne une liste de tuples contenant les coordonnées de la boîte englobante de la ligne : `(minX, maxX, baselineY, fontSize)`[cite: 16].

*Utilité : Cela permet au moteur de dessin de tracer un seul grand rectangle autour d'une phrase modifiée, plutôt que de dessiner 50 petits rectangles autour de chaque lettre de cette phrase.*

### 2. Regroupement Vertical : `CountBlocks`
Une fois que les lettres sont regroupées en segments horizontaux, cette méthode prend le relais pour regrouper ces lignes en "Blocs" (qui correspondent visuellement à des paragraphes modifiés)[cite: 16].

**Le processus :**
1.  L'algorithme analyse l'espace vertical entre chaque segment horizontal[cite: 16].
2.  Il calcule la limite basse du bloc actuel (`currentMinY`) et la limite haute du bloc suivant (`boxMaxY`)[cite: 16].
3.  Si la distance verticale entre les deux lignes est inférieure à deux fois la taille de la police (`fontSize * 2.0m`), il considère que ces deux lignes font partie du même bloc/paragraphe d'erreur et étend la boîte englobante[cite: 16].
4.  Si l'écart est plus grand, il clôture le bloc actuel, incrémente le compteur (`blocksCount++`), et commence un nouveau bloc[cite: 16].

*Utilité : Cette méthode est vitale pour le tableau de bord de l'Orchestrateur. Si un paragraphe de 10 lignes a été supprimé, l'utilisateur veut voir "1 suppression" dans son rapport, et non pas "100 mots supprimés". Ce Helper permet de compter précisément le nombre de "zones" visuelles impactées.*