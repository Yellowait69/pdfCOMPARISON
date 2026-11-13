# DocItem (Modèle)

La classe `DocItem` est un modèle de données simple (POCO) utilisé spécifiquement par le module de documentation interne de l'application[cite: 18]. Elle fait partie de l'espace de noms `PDFComparison.Models`[cite: 18].

Contrairement aux autres modèles de ce dossier qui gèrent la comparaison de PDF, `DocItem` est exclusivement destiné à l'interface graphique de la fenêtre de documentation (`DocumentationWindow`).

---

## 📦 Propriétés

Ce modèle contient les informations nécessaires pour afficher et charger un fichier Markdown[cite: 18] :

*   **`Title`** (`string`) : Représente le titre d'affichage du document dans le menu latéral de l'interface[cite: 18]. Lors du chargement par le ViewModel, cette propriété est alimentée par le nom du fichier sans son extension (ex: "DocItem" au lieu de "DocItem.md").
*   **`FilePath`** (`string`) : Représente le chemin d'accès absolu vers le fichier physique sur le disque[cite: 18]. C'est cette valeur qui est utilisée par le système pour lire le contenu textuel (`File.ReadAllText`) lorsque l'utilisateur clique sur ce document.

---

## 🔗 Utilisation

Ce modèle est principalement instancié dans le `DocumentationViewModel`. Il est stocké dans une `ObservableCollection<DocItem>` qui est ensuite liée (Data Binding) à la `ListBox` du menu de navigation de la fenêtre de documentation.