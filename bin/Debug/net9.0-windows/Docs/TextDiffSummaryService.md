# TextDiffSummaryService

Le `TextDiffSummaryService` est le moteur responsable de la création du résumé logique et textuel des différences entre deux documents[cite: 14].

Contrairement aux services de rendu visuel qui cherchent à encadrer des mots sur une page PDF, ce service génère une structure de données propre (`DiffSummaryBlock`) qui alimentera le rapport de synthèse global. Il répond à la question : *"Quels paragraphes ont été ajoutés, supprimés, ou modifiés, et dans quel contexte ?"*[cite: 14]

---

## 🛠️ Dépendances

Ce service s'appuie sur la librairie de comparaison textuelle **DiffPlex** (`SideBySideDiffBuilder` et `Differ`)[cite: 14].

---

## 🧠 Algorithme de Synthèse (`BuildTextSummary`)

La méthode principale prend en entrée les deux textes préalablement nettoyés (via le *Sanitizer* et le *Normalizer*) et exécute un algorithme complexe en trois passes pour garantir un résumé pertinent[cite: 14] :

### Passe 1 : La Comparaison Brute
Le service demande à `DiffPlex` de générer un modèle de comparaison complet ligne par ligne (`diffBuilder.BuildDiffModel`)[cite: 14].

### Passe 2 : L'intelligence de Déplacement (Move Detection)
C'est la grande force de ce service. Dans un contrat, un paragraphe peut simplement être déplacé d'une page à l'autre sans que son contenu ne change[cite: 14]. Un comparateur naïf signalerait une énorme "Suppression" à la page 2, et un énorme "Ajout" à la page 4[cite: 14].
*   Le service compte toutes les lignes supprimées (`sumDel`) et toutes les lignes ajoutées (`sumIns`)[cite: 14].
*   Il croise ces dictionnaires : si une ligne a été à la fois supprimée et ajoutée ailleurs avec le même texte exact, il enregistre le nombre d'occurrences dans des listes d'ignorance (`skipDel` et `skipIns`)[cite: 14].
*   **Résultat** : Les blocs de texte simplement déplacés sont ignorés et ne pollueront pas le rapport d'erreurs[cite: 14].

### Passe 3 : Regroupement en Blocs (Block Grouping)
Le service parcourt ensuite le modèle de différences ligne par ligne[cite: 14] :
*   Il ignore les lignes marquées comme "déplacées" à l'étape précédente[cite: 14].
*   Il regroupe les lignes supprimées consécutives dans une liste `currentDel`, et les lignes ajoutées consécutives dans `currentIns`[cite: 14].
*   Dès qu'il détecte la fin d'une zone de différence, il "flush" (vide) ces listes pour créer des objets `DiffSummaryBlock` via la méthode privée `FlushBlocks`[cite: 14].

---

## 🔍 Extraction du Contexte (`GetValidContextLine`)

Pour qu'un rapport de synthèse soit lisible par un humain, il ne suffit pas de montrer la phrase modifiée. Il faut montrer la phrase qui la précède et celle qui la suit[cite: 14].

Lorsqu'un bloc de différence est détecté, le service utilise la méthode `GetValidContextLine` pour remonter ou descendre dans le document afin de capturer :
*   `ctxBefore` : La première ligne de texte valide (non vide) juste au-dessus de la modification[cite: 14].
*   `ctxAfter` : La première ligne de texte valide juste en dessous de la modification[cite: 14].

---

## 📦 Type de Retour

La méthode retourne un *Tuple* contenant trois éléments[cite: 14] :
1.  **`DifferencesCount`** : Le nombre total de blocs de différences générés[cite: 14].
2.  **`Blocks`** : La liste des `DiffSummaryBlock` (qui contiennent le type de changement, l'ancien texte, le nouveau texte, et le contexte)[cite: 14].
3.  **`DiffLinesModel`** : Le modèle brut de DiffPlex, qui est renvoyé pour pouvoir être réutilisé plus tard (notamment par le `VisualHighlightMatcherService`) afin d'éviter de recalculer deux fois la comparaison[cite: 14].