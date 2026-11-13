# FileDropBehavior (Glisser-Déposer Natif WPF)

## Rôle
`FileDropBehavior` est un comportement attaché (*Attached Behavior*) WPF permettant de glisser-déposer (*Drag & Drop*) directement un dossier ou un fichier `.pdf` unitaire depuis l'Explorateur Windows sur les champs `TextBox` de sélection des chemins (*Source*, *Cible*, *Dossier Rapports*).

## Fonctionnement
- Intercepte `PreviewDragOver` et `PreviewDrop` pour contourner le blocage natif des `TextBox` WPF lors du survol d'un fichier.
- Extrait le premier chemin déposé via `DataFormats.FileDrop` et met immédiatement à jour la liaison de données MVVM (`UpdateSource()`).
