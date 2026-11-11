# DocumentationViewModel

Le `DocumentationViewModel` est le contrôleur logique (ViewModel) qui alimente la fenêtre de documentation (`DocumentationWindow`). Il respecte l'architecture **MVVM (Model-View-ViewModel)** et s'appuie massivement sur les générateurs de code du **CommunityToolkit.Mvvm** pour réduire le code répétitif (boilerplate).

Son rôle est de scanner le dossier contenant les fichiers Markdown (`.md`), de construire la liste de navigation, et de charger le texte du document sélectionné pour l'afficher à l'écran.

---

## 📦 Propriétés Observables (Data Binding)

Le ViewModel expose trois propriétés principales générées automatiquement par l'attribut `[ObservableProperty]`. Ces propriétés notifient la vue (l'interface graphique) à chaque fois que leur valeur change :

*   **`Documents`** (`ObservableCollection<DocItem>`) : Contient la liste dynamique de tous les fichiers Markdown trouvés. Cette liste est directement liée au menu latéral de l'interface.
*   **`SelectedDocument`** (`DocItem?`) : Représente le fichier sur lequel l'utilisateur a cliqué dans la liste.
*   **`MarkdownContent`** (`string`) : Contient le texte brut du fichier Markdown actuellement sélectionné. C'est cette chaîne de caractères qui est interprétée et rendue visuellement par le composant `MdXaml` dans la vue.

---

## 🚀 Mécanismes Principaux

### 1. Chargement Initial (`LoadDocuments`)
Appelée dès la construction du ViewModel, cette méthode initialise le système documentaire :
*   Elle détermine le chemin absolu vers le dossier `Docs`, qui doit se trouver à la racine de l'exécutable (`AppDomain.CurrentDomain.BaseDirectory`).
*   **Si le dossier existe** : Elle scanne tous les fichiers `.md` et crée un objet `DocItem` pour chacun, en utilisant le nom du fichier (sans l'extension) comme titre.
*   **Gestion des erreurs** : Si le dossier est introuvable ou s'il est vide, la propriété `MarkdownContent` est mise à jour avec un message d'erreur clair ou des instructions pour aider le développeur à configurer les fichiers.

### 2. Réactivité Magique (`OnSelectedDocumentChanged`)
C'est ici qu'intervient la puissance du `CommunityToolkit.Mvvm`. Plutôt que d'écrire un événement complexe pour écouter les clics dans l'interface, le ViewModel utilise une **méthode partielle** (`partial void`).

*   Cette méthode est **automatiquement déclenchée** par le Toolkit à chaque fois que la propriété `SelectedDocument` change.
*   Lorsqu'un nouveau document est sélectionné, la méthode vérifie que le fichier existe physiquement sur le disque.
*   Elle lit ensuite l'intégralité du fichier texte (`File.ReadAllText`) et l'injecte dans la propriété `MarkdownContent`.
*   Le changement de cette propriété prévient instantanément la vue WPF, qui rafraîchit l'affichage avec la nouvelle documentation.
*   Un bloc `try/catch` garantit que si le fichier est verrouillé ou corrompu, l'application ne plantera pas et affichera l'erreur proprement à l'écran.