# MainViewModel

Le `MainViewModel` est le cœur névralgique de l'interface utilisateur de l'application. En suivant l'architecture **MVVM (Model-View-ViewModel)** via le `CommunityToolkit.Mvvm`, il agit comme le chef d'orchestre suprême : il relie la vue (WPF) aux services métiers (Backend), gère l'état global de l'application, et intercepte les actions de l'utilisateur.

---

## 🛠️ Initialisation et Graphe d'Objets

Contrairement aux autres services qui sont injectés, le constructeur du `MainViewModel` a la responsabilité de construire **l'intégralité du graphe d'objets** de l'application.
Il instancie tous les services de base (Normalizer, Masking, Watermark), les assemble dans les services intermédiaires (Extraction, DiffAnalyzer), pour enfin construire le `PdfComparisonOrchestrator` complet.

C'est également dans le constructeur que sont définis les chemins par défaut (le dossier `AppData` pour la sauvegarde de la session, et le `Bureau` pour les rapports générés).

---

## 📦 Propriétés Observables et Optimisations

Le ViewModel expose de nombreuses propriétés avec l'attribut `[ObservableProperty]`, ce qui met à jour l'interface graphique automatiquement :
*   **Les chemins** : `SourceDirectory`, `TargetDirectory`, `OutputDirectory`.
*   **L'état de traitement** : `IsProcessing` (qui désactive automatiquement certains boutons grâce à `IsNotProcessing`), `ProgressValue`, `ProgressMax`, et `StatusMessage`.

**L'optimisation `ObservableRangeCollection<T>` :**
Pour afficher la liste des documents (`Pairs`), l'application utilise une classe personnalisée `ObservableRangeCollection`. Contrairement à une liste classique qui fige l'interface graphique en notifiant la vue pour *chaque* document ajouté, la méthode `ReplaceRange` ajoute tous les documents en mémoire puis envoie **une seule notification** globale de mise à jour (`NotifyCollectionChangedAction.Reset`). Cela rend le chargement instantané même avec des milliers de fichiers.

---

## 💾 Gestion de Session (Persistance)

Pour éviter que l'utilisateur ne doive resélectionner ses dossiers à chaque lancement, le ViewModel intègre un système de persistance :
*   **`SaveSession`** : Sérialise l'état actuel (Dossiers et liste des résultats) dans un fichier JSON (`last_session.json`) situé dans le dossier `%AppData%/PDFComparisonPro`. Il utilise un fichier temporaire (`.tmp`) lors de l'écriture pour éviter de corrompre la sauvegarde en cas de coupure de courant.
*   **`LoadSession`** : Appelé au démarrage, il lit le fichier JSON et restaure instantanément le contexte de travail précédent.

---

## 🚀 Commandes Principales (`[RelayCommand]`)

Le toolkit génère automatiquement des commandes asynchrones ou synchrones à partir de ces méthodes pour être attachées aux boutons de l'interface :

### Le Cycle de Comparaison
*   **`StartComparisonAsync`** : C'est le flux de travail principal.
    1.  Vérifie la validité des dossiers.
    2.  Instancie un `CancellationTokenSource` pour permettre l'annulation.
    3.  Appelle le `PdfFileService` pour appairer les fichiers.
    4.  Lance le traitement parallèle via le `PdfComparisonOrchestrator`.
    5.  Trie les résultats (les fichiers contenant le plus d'erreurs en premier).
    6.  Affiche une pop-up de succès et sauvegarde la session.
*   **`CancelComparison`** : Interrompt gracieusement l'analyse en cours grâce au jeton d'annulation (Token).

### La Navigation et l'OS
*   **`BrowseSource` / `BrowseTarget`** : Ouvre les fenêtres de dialogue natives (`OpenFolderDialog`) pour choisir les dossiers.
*   **`OpenReport`** : Ouvre le rapport PDF individuel d'un document directement dans le lecteur PDF par défaut de Windows (`Process.Start`).
*   **`OpenOutputDirectory`** : Ouvre en priorité le rapport de synthèse global. S'il n'existe pas, ouvre le dossier Windows contenant les rapports.
*   **`ShareViaOutlook`** : Génère une URI de type `mailto:` pour préparer un e-mail pré-rempli invitant à consulter les rapports.

### La Documentation
*   **`OpenDocumentation`** : Instancie et affiche de manière non bloquante (`Show()`) la fenêtre `DocumentationWindow`. L'utilisateur peut ainsi lire la doc métier générée à partir des fichiers Markdown tout en utilisant le comparateur sur son autre écran.