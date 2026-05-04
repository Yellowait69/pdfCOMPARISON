# PdfChartService

Le `PdfChartService` est un service spécialisé dans la visualisation de données[cite: 7]. Son rôle unique est de générer les graphiques (camemberts/pie charts) qui s'affichent sur la page de tableau de bord (Dashboard) du rapport de synthèse global[cite: 7].

Le fait d'avoir extrait cette logique dans un service dédié permet de garder le générateur de rapport (`GlobalSynthesisReportGenerator`) propre et concentré sur la mise en page.

---

## 🛠️ Dépendances

Ce service fait le pont entre deux librairies clés[cite: 7] :
*   **ScottPlot** : Une librairie de création de graphiques très performante en C#. Elle est utilisée pour générer les camemberts en mémoire[cite: 7].
*   **PdfPig** : Utilisé pour intégrer l'image générée directement dans le flux du fichier PDF[cite: 7].

---

## 📊 Les Graphiques Générés (`DrawDashboardCharts`)

La méthode principale construit trois graphiques distincts, alignés horizontalement sur le tableau de bord[cite: 7]. Elle prépare les "parts" de camembert (`PieSlice`) en associant des couleurs hexadécimales spécifiques pour garder une cohérence visuelle[cite: 7] :

### 1. Répartition par Type d'Action
Ce graphique compare le volume des suppressions face aux ajouts[cite: 7] :
*   🟢 **Ajouts** : Surlignés en vert (`#10B981`)[cite: 7].
*   🔴 **Suppressions** : Surlignées en rouge (`#EF4444`)[cite: 7].

### 2. Nature des Données Impactées
Ce graphique catégorise le type de texte qui a été modifié pour aider à identifier les risques (ex: un changement de date est souvent plus critique qu'un changement de mot)[cite: 7] :
*   🔵 **Textes** (`#3B82F6`)[cite: 7].
*   🟣 **Nombres** (`#8B5CF6`)[cite: 7].
*   🟢 **Dates** (`#14B8A6`)[cite: 7].

### 3. Répartition par Langues
Ce graphique affiche la proportion de documents modifiés selon leur langue[cite: 7]. Il parcourt un dictionnaire (`languageFileCounts`) et attribue dynamiquement une couleur à chaque langue en bouclant sur une palette prédéfinie de 6 couleurs[cite: 7].

---

## ⚙️ Rendu et Mécanisme de Secours (`DrawPieChartOrEmptyState`)

Pour chaque graphique, le service fait appel à la méthode privée `DrawPieChartOrEmptyState` qui gère la création de l'image[cite: 7].

**Le processus de rendu :**
1.  **Si des données existent** :
    *   Un nouveau graphique (`Plot`) est initialisé[cite: 7].
    *   Les grilles et les axes sont masqués pour un rendu propre (`HideGrid()`, `HideAxesAndGrid()`)[cite: 7].
    *   Les parts de camembert sont ajoutées avec un léger effet d'éclatement (`ExplodeFraction = 0.05`) pour séparer visuellement les sections[cite: 7].
    *   Le graphique est exporté sous forme d'image PNG en mémoire (tableau d'octets de 240x200 pixels) puis dessiné sur le PDF via `page.AddPng(...)`[cite: 7].
2.  **État vide (Fallback)** :
    *   Si un graphique n'a aucune donnée (ex: aucune date ou nombre n'a été modifié), le service ne génère pas un carré blanc[cite: 7]. À la place, il écrit discrètement "(Aucune donnée)" en gris clair à l'emplacement prévu[cite: 7].