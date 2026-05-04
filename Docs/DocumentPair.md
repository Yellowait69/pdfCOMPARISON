# DocumentPair (Modèle)

La classe `DocumentPair` est l'une des structures de données les plus fondamentales de l'application. Elle fait partie de l'espace de noms `PDFComparison.Models` et représente l'association entre un fichier source et un fichier cible qui doivent être comparés.

Contrairement à de simples modèles de transfert de données, cette classe hérite de `ObservableObject` (du framework `CommunityToolkit.Mvvm`), ce qui signifie qu'elle est conçue pour être directement liée à l'interface graphique (DataGrid) et notifier la vue de tout changement de ses propriétés.

---

## 📦 Propriétés Observables

Grâce à l'attribut `[ObservableProperty]`, le toolkit génère automatiquement les propriétés publiques avec le code nécessaire pour la notification MVVM (le `INotifyPropertyChanged`).

### 1. Identification et Chemins
*   **`MatchKey`** (`string`) : La clé unique extraite du nom du fichier (via Regex) qui a permis d'associer la source et la cible.
*   **`SourcePath`** (`string`) : Le chemin absolu vers le document original (Source)[cite: 18].
*   **`TargetPath`** (`string?`) : Le chemin absolu vers le document modifié (Cible)[cite: 18]. Il peut être nul si le fichier correspondant n'a pas été trouvé.

### 2. État et Suivi
*   **`Status`** (`CompareStatus`) : L'état d'avancement ou le résultat de la comparaison[cite: 18]. Initialisé par défaut sur `Pending`[cite: 18].
*   **`ErrorMessage`** (`string`) : Contient la description de l'erreur si le statut passe en erreur ou si le fichier cible est manquant[cite: 18].
*   **`CompletedTime`** (`DateTime?`) : L'heure exacte à laquelle l'analyse de cette paire s'est terminée[cite: 18].

### 3. Statistiques de Différence
Ces compteurs alimentent les colonnes numériques du tableau de bord de l'application.
*   **`DiffCount`** (`int`) : Le nombre total de différences trouvées[cite: 18].
*   **`InsertionsCount`** (`int`) : Le nombre de blocs de texte ajoutés[cite: 18].
*   **`DeletionsCount`** (`int`) : Le nombre de blocs de texte supprimés[cite: 18].

### 4. Rapport de Comparaison
*   **`ReportPath`** (`string`) : Le chemin vers le fichier PDF de rapport généré spécifiquement pour cette paire[cite: 18]. L'attribut `[NotifyPropertyChangedFor(nameof(HasReport))]` indique que toute modification de ce chemin doit aussi signaler à l'interface graphique que la propriété `HasReport` a changé[cite: 18].
*   **`HasReport`** (`bool`) : Une propriété calculée qui renvoie `true` si le `ReportPath` n'est pas vide[cite: 18]. Elle possède l'attribut `[JsonIgnore]` pour ne pas être sauvegardée inutilement dans le fichier de session JSON[cite: 18].

---

## 🛠️ Logique du Constructeur

Le modèle propose un constructeur intelligent pour faciliter la phase d'appairage (matching)[cite: 18] :
```csharp
public DocumentPair(string matchKey, string sourcePath, string? targetPath)