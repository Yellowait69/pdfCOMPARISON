# PdfTextNormalizerService

Le `PdfTextNormalizerService` est la dernière étape de préparation du texte brut avant qu'il ne soit envoyé au moteur de comparaison[cite: 12].

Étant donné que l'algorithme sous-jacent (DiffPlex) compare les textes **ligne par ligne**, il est crucial que le texte ne soit pas un seul bloc géant. Ce service se charge de reformater le texte extrait pour lui donner une structure logique (phrases et puces) optimisée pour la détection de différences[cite: 12].

---

## 🚀 Mécanisme de Normalisation (`NormalizePdfText`)

La méthode principale applique une série de transformations successives sur la chaîne de caractères brute[cite: 12] :

### 1. Compression des Espaces (Whitespace Collapsing)
L'extraction PDF génère souvent des espaces multiples ou des tabulations irrégulières.
*   Le service parcourt le texte caractère par caractère[cite: 12].
*   Dès qu'il détecte une suite de caractères d'espacement (`char.IsWhiteSpace`), il les fusionne en un seul et unique espace standard (` `)[cite: 12].
*   Cela garantit que deux phrases identiques séparées par deux espaces dans la source, mais par un seul espace dans la cible, ne seront pas considérées comme une erreur.

### 2. Segmentation Logique (Sauts de ligne forcés)
Pour forcer le moteur DiffPlex à comparer des phrases plutôt que des paragraphes entiers, le service injecte artificiellement des sauts de ligne (`\n`) à des endroits stratégiques[cite: 12] :
*   **Fins de phrases** : Il remplace la ponctuation suivie d'un espace (`. `, `? `, `! `, `: `) par un saut de ligne[cite: 12].
*   **Énumérations et Puces** : C'est une étape cruciale pour l'alignement visuel. Il force un saut de ligne juste avant les puces classiques (` • `, ` - `, ` o `) pour s'assurer que chaque élément de liste est analysé sur sa propre ligne[cite: 12].

### 3. Nettoyage Final (Trimming)
Une fois les sauts de ligne injectés :
*   Il découpe (`Split`) le texte entier en un tableau de lignes[cite: 12].
*   Il supprime automatiquement les lignes vides (`StringSplitOptions.RemoveEmptyEntries`) et nettoie les espaces restants en début et fin de chaque ligne (`StringSplitOptions.TrimEntries`)[cite: 12].
*   Enfin, il recolle toutes les lignes propres avec un saut de ligne simple (`string.Join("\n", lines)`)[cite: 12].

---

## 🎯 Pourquoi ce service est indispensable ?

Sans ce service, si un document contient un paragraphe de 20 lignes et qu'une seule virgule est modifiée, le moteur de comparaison mettrait l'intégralité des 20 lignes en rouge (supprimé) et en vert (ajouté).

Grâce au `PdfTextNormalizerService`, le texte est découpé phrase par phrase. Ainsi, seule la phrase précise contenant la virgule modifiée sera signalée, offrant un rapport de différences clair et chirurgical.