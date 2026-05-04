# GlobalSynthesisReportGenerator

Le `GlobalSynthesisReportGenerator` est un service dédié à la génération du rapport PDF de synthèse globale. Contrairement aux rapports individuels qui montrent les différences visuelles page par page, ce rapport agglomère et résume toutes les différences détectées sur **l'ensemble des documents traités** lors d'une session.

---

## 🛠️ Dépendances (Inversion de contrôle)

Ce service s'appuie sur trois autres services injectés pour déléguer les tâches complexes :

*   **`IPdfDrawingService`** : Fournit les méthodes pour dessiner des éléments textuels, des encadrés statistiques (`DrawStatBox`) et charger les polices.
*   **`IPdfChartService`** : Génère et insère les graphiques (camemberts) sur la page de tableau de bord pour visualiser la répartition des erreurs.
*   **`IInlineDiffService`** : Découpe les blocs de texte modifiés en fragments précis (mots ajoutés/supprimés) pour pouvoir les colorer dynamiquement (en vert ou en rouge) dans le rapport.

---

## 🧠 Mécanisme d'analyse et de détection

Avant de dessiner le rapport, le service parcourt les résumés (`DocumentDiffSummary`) de tous les documents comparés[cite: 4]. Il utilise des **Expressions Régulières (Regex)** générées à la compilation (`[GeneratedRegex]`) pour catégoriser la nature des différences[cite: 4] :

1.  **Dates (`DateRegex`)** : Détecte si la modification concerne une date (ex: `12/05/2023`, `12-05-2023`)[cite: 4].
2.  **Nombres/Devises (`NumRegex`)** : Détecte si la modification est purement numérique ou financière (incluant les symboles `%`, `€`, `$`, `£`)[cite: 4].
3.  **Mots critiques (`CriticalRegex`)** : Compte les alertes de sécurité en scannant les textes modifiés à la recherche de vocabulaire sensible (ex: *prix*, *pénalité*, *résiliation*, *facture*, *tax*, etc.)[cite: 4].

*Note : Les modifications qui ne sont ni des dates ni des nombres sont catégorisées comme "Textes" (Words)[cite: 4].*

---

## 📄 Structure du rapport généré

La méthode principale `GenerateGlobalSynthesisReport` construit un fichier nommé `Global_Synthesis_Report.pdf` dans le dossier de sortie[cite: 4]. Le rapport est divisé en deux grandes parties :

### 1. La page Dashboard (Tableau de bord)
Générée par la méthode `DrawDashboardPage`, c'est la première page du rapport[cite: 4]. Elle offre une vue macroscopique de la session :
*   **En-tête :** Date de génération du rapport[cite: 4].
*   **Bandeau statistique :** Quatre encadrés affichant les KPI principaux : Fichiers impactés, Total des différences, Balance de mots (ajouts vs suppressions), et Alertes sur les mots sensibles[cite: 4].
*   **Graphiques :** Répartition des types d'actions (ajouts/suppressions), nature des données impactées, et répartition des documents par langue (détectée via le préfixe du fichier)[cite: 4].
*   **Top 3 :** Liste des 3 fichiers ayant subi le plus de modifications[cite: 4].

### 2. Le détail des différences (Pages suivantes)
Pour chaque document ayant des différences, le service liste les blocs modifiés de manière textuelle :
*   Le document est clairement identifié dans un encadré bleu[cite: 4].
*   Chaque différence affiche l'ancien texte (Source) à gauche, et le nouveau texte (Target) à droite[cite: 4].
*   Si la différence concerne des images (graphiques, logos modifiés), les images sont rendues directement dans le PDF[cite: 4].
*   Si la différence est textuelle, le `IInlineDiffService` est appelé pour surligner uniquement les mots modifiés au sein de la phrase, en conservant un contexte (les mots avant et après la modification) pour faciliter la lecture[cite: 4].