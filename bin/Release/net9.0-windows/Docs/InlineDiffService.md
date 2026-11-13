# InlineDiffService

Le `InlineDiffService` est un service spécialisé dans l'analyse granulaire des différences textuelles. Alors que la détection standard identifie des paragraphes ou des lignes entières comme étant modifiés, ce service descend au niveau du **mot** (inline diff).

Il est principalement utilisé par le générateur de rapports globaux pour surligner de manière chirurgicale les mots exacts qui ont été ajoutés ou supprimés à l'intérieur d'une phrase.

---

## 🛠️ Dépendances

Ce service s'appuie fortement sur la librairie externe **DiffPlex** (`SideBySideDiffBuilder`, `Differ`), qui est le moteur de comparaison sous-jacent.

---

## 🎨 Conventions Visuelles (Codes Couleurs)

Le service prépare directement les données pour l'interface graphique ou le rendu PDF en associant des codes couleurs RGB stricts à chaque mot selon son état :
*   🔴 **Rouge (`ColorDeleted` : 200, 0, 0)** : Attribué aux mots supprimés dans le document source (`ChangeType.Deleted` ou `ChangeType.Modified`).
*   🟢 **Vert (`ColorInserted` : 0, 150, 0)** : Attribué aux mots ajoutés dans le document cible (`ChangeType.Inserted` ou `ChangeType.Modified`).
*   ⚪ **Gris (`ColorUnchanged` : 100, 100, 100)** : Attribué au texte de contexte qui n'a pas subi de modification[cite: 6].

---

## 🚀 Mécanisme de Génération (`GetInlineDiffChunks`)

La méthode unique `GetInlineDiffChunks` prend l'ancien texte (`oldText`) et le nouveau texte (`newText`) et retourne deux listes de segments formatés (gauche/source et droite/cible)[cite: 6].

Voici comment l'algorithme fonctionne :

### 1. Découpage intelligent des mots (Regex)
La méthode utilise une expression régulière compilée `(?<=\s+)` (`SplitWordsRegex`) pour découper le texte en mots[cite: 6].
*L'astuce de cette Regex (`(?<=...)` - lookbehind) est qu'elle coupe sur les espaces mais **conserve l'espacement attaché au mot**. Cela permet de ne pas perdre le formatage d'origine lors de la reconstruction.*

### 2. L'astuce du saut de ligne pour DiffPlex
Le moteur `DiffPlex` est conçu pour comparer des lignes, pas des mots[cite: 6]. Pour le forcer à faire une comparaison intra-ligne, le service "triche" en recollant le tableau de mots avec des sauts de ligne (`\n`)[cite: 6] :
Ainsi, DiffPlex traite chaque mot comme s'il s'agissait d'une ligne distincte, offrant une granularité parfaite[cite: 6].

### 3. Parcours et Formatage (Chunks)
   Le service parcourt ensuite le modèle généré (SideBySideDiffModel) ligne par ligne (qui sont en réalité nos mots)[cite: 6].

Il ignore les lignes virtuelles (ChangeType.Imaginary) créées par DiffPlex pour l'alignement[cite: 6].

Il nettoie les sauts de ligne artificiels ajoutés à l'étape précédente (Replace("\n", ""))[cite: 6].

Il détermine la couleur et si le texte doit être mis en gras (isBold) en fonction du type de changement[cite: 6].

📦 Type de Retour
La méthode retourne un Tuple contenant deux listes de "Chunks" (fragments)[cite: 6] :

C#
(List<(string Text, byte r, byte g, byte b, bool isBold)> Left,
List<(string Text, byte r, byte g, byte b, bool isBold)> Right)
Chaque élément de ces listes contient toutes les informations nécessaires (le texte, sa couleur RGB, et son style typographique) pour que le moteur de dessin PDF (ou l'UI WPF) puisse les afficher directement à la suite les uns des autres[cite: 6].