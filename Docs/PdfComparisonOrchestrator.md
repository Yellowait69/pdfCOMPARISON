# PdfComparisonOrchestrator

Le `PdfComparisonOrchestrator` est le véritable "chef d'orchestre" de l'application[cite: 8]. Son rôle est de coordonner l'ensemble du flux de comparaison de plusieurs paires de documents, de l'extraction initiale jusqu'à la génération des rapports finaux[cite: 8]. Il gère notamment le traitement en parallèle (multithreading), l'annulation des tâches, la gestion des erreurs d'accès aux fichiers, et la communication avec l'interface utilisateur[cite: 8].

---

## 🛠️ Dépendances

L'orchestrateur ne fait pas le travail de bas niveau lui-même. Il délègue les tâches à quatre sous-services injectés via son constructeur[cite: 8] :
*   **`PdfExtractionService`** : Pour lire et extraire le texte et les coordonnées des mots[cite: 8].
*   **`PdfDiffAnalyzer`** : Pour calculer les différences logiques et visuelles entre les textes[cite: 8].
*   **`IIndividualReportGenerator`** : Pour dessiner le rapport PDF annoté spécifique à une paire[cite: 8].
*   **`IGlobalSynthesisReportGenerator`** : Pour générer le tableau de bord global de la session à la fin du processus[cite: 8].

---

## 🚀 Fonctionnement du Processus Global (`ProcessPairsAsync`)

C'est le point d'entrée principal appelé par l'interface utilisateur. Cette méthode est asynchrone (`Task`) et extrêmement optimisée[cite: 8].

### 1. Traitement Multithread
Pour maximiser les performances, l'orchestrateur utilise `Parallel.ForEachAsync`[cite: 8]. Il traite les paires de documents simultanément, en limitant le nombre de threads (jusqu'à 16 maximum) en fonction de la puissance du processeur de la machine (`Environment.ProcessorCount`)[cite: 8].

### 2. Tri Rapide (Fast-Fail)
Avant de lancer une analyse approfondie et coûteuse, le service effectue une vérification rapide[cite: 8] :
*   Il extrait le texte brut de la source et de la cible (`ExtractTextFast`)[cite: 8].
*   Si les textes sont totalement vides, il marque la paire en erreur (`Unreadable files (Scanned/OCR required)`)[cite: 8].
*   Si les textes sont strictement identiques, il marque la paire comme `Identical` et passe au document suivant sans générer de rapport individuel pour économiser du temps[cite: 8].

### 3. Gestion Globale
Les données de résumé de chaque comparaison sont stockées de manière thread-safe dans un `ConcurrentBag<DocumentDiffSummary>`[cite: 8]. Une fois tous les documents traités, l'orchestrateur déclenche la génération du rapport de synthèse global[cite: 8].

---

## 🔍 Analyse Détaillée d'une Paire (`ProcessSinglePair`)

Si des différences sont détectées lors du tri rapide, cette méthode prend le relais pour une analyse profonde[cite: 8].

1.  **Extraction Spatiale** : Elle extrait les mots avec leurs coordonnées exactes (`ExtractWords`)[cite: 8].
2.  **Analyse** : Elle demande au `PdfDiffAnalyzer` de trouver les correspondances et les différences[cite: 8].
3.  **Comptage Visuel** : Elle utilise le `VisualSegmentHelper` pour compter intelligemment les vrais blocs d'erreurs (les ajouts et les suppressions visuelles)[cite: 8].
4.  **Résilience aux erreurs d'écriture** : Si l'utilisateur a déjà le rapport PDF individuel ouvert dans Adobe Reader, une erreur d'écriture (`IOException`) se produit[cite: 8]. Le service l'intercepte intelligemment et génère un rapport avec un nom alternatif (`fallbackPath` avec horodatage) plutôt que de faire planter l'application[cite: 8].

---

## 🧵 Thread Safety (Mise à jour de l'UI)

L'interface graphique (WPF) ne peut pas être mise à jour par les threads parallèles[cite: 8]. Les méthodes `UpdatePairStatus` et `SetReportPath` utilisent le répartiteur de l'application (`Application.Current.Dispatcher.Invoke`) pour s'assurer que les barres de progression et les statuts des documents dans le tableau se mettent à jour de manière sécurisée et fluide[cite: 8].