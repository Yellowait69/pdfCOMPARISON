# MainWindow (Vue Principale)

Le fichier `MainWindow_2.xaml` (définissant la classe `PDFComparison.MainWindow`) constitue l'interface utilisateur principale de l'application "Automated PDF Comparator - Pro Version"[cite: 18].

Conçue avec le framework WPF, cette fenêtre respecte scrupuleusement le motif d'architecture MVVM en liant son contexte de données (`DataContext`) au `MainViewModel`[cite: 18]. Elle se distingue par une interface moderne, épurée et réactive, n'utilisant aucun contrôle standard dans son aspect brut.

---

## 🎨 Ressources et Styles Globaux (`Window.Resources`)

Pour garantir une cohérence visuelle sur toute l'application, la fenêtre embarque un dictionnaire de ressources contenant des styles personnalisés pour les contrôles de base[cite: 18] :

### 1. Boutons Modernes (`ModernPrimaryButton` & `ModernSecondaryButton`)
L'apparence des boutons natifs de Windows est totalement écrasée par un `ControlTemplate` sur mesure[cite: 18] :
*   **Design** : Les boutons utilisent des coins arrondis (`CornerRadius="8"`) et n'ont pas de bordure (`BorderThickness="0"`)[cite: 18].
*   **Interactivité (Triggers)** :
    *   Au survol (`IsMouseOver`), la couleur de fond s'assombrit[cite: 18].
    *   Au clic (`IsPressed`), une animation de transformation (`ScaleTransform`) réduit la taille du bouton à 97% (`ScaleX="0.97" ScaleY="0.97"`), offrant un retour physique tactile très satisfaisant sans utiliser d'effets 3D démodés[cite: 18].
    *   À l'état désactivé (`IsEnabled="False"`), le bouton passe en gris (`#94A3B8`)[cite: 18].

### 2. Champs de Texte (`TextBox`)
Les champs de saisie (utilisés pour afficher les chemins des dossiers) bénéficient également d'un `ControlTemplate`[cite: 18] :
*   En temps normal, ils présentent une bordure gris clair discrète[cite: 18].
*   Au focus (`IsFocused="True"`), la bordure s'épaissit (`BorderThickness="2"`) et devient bleue (`#3B82F6`) pour indiquer clairement à l'utilisateur où se trouve son curseur[cite: 18].

---

## 📐 Structure de la Page (Layout)

L'interface est structurée autour d'une `Grid` principale divisée en 4 lignes (Rows), entourée d'une marge généreuse de 30 pixels pour aérer le contenu[cite: 18].

### Ligne 0 : L'En-tête (Header)
Construit avec un `DockPanel`, il contient le titre de l'application et sa description à gauche, ainsi que les boutons d'actions principales alignés à droite[cite: 18] :
*   **Doc Interne** : Lié à `OpenDocumentationCommand`[cite: 18].
*   **Start Comparison** : Lié à `StartComparisonCommand`[cite: 18].
*   *Note typographique : Les icônes utilisées (ex: `&#xE8A5;`) proviennent de la police système Windows `Segoe MDL2 Assets`*[cite: 18].

### Ligne 1 : Configuration des Répertoires
Un panneau surélevé visuellement grâce à une ombre portée (`DropShadowEffect`) contenant une grille de configuration[cite: 18] :
*   Affiche les répertoires Source, Cible (Target) et de Sortie (Reports) via des `TextBox` en lecture seule (`IsReadOnly="True"`)[cite: 18].
*   Chaque ligne propose des boutons d'actions contextuelles (Parcourir, Ouvrir, Partager)[cite: 18].

### Ligne 2 : Panneau de Progression
Une section dédiée au retour d'information pendant le traitement[cite: 18] :
*   Affiche un message de statut dynamique (`StatusMessage`) et un compteur (`ProgressValue / ProgressMax`) via un `MultiBinding`[cite: 18].
*   Intègre une barre de progression (`ProgressBar`) dont les coins ont été arrondis via un style interne sur sa `Border`[cite: 18].

### Ligne 3 : Tableau des Résultats (`DataGrid`)
C'est la section la plus complexe, occupant tout l'espace vertical restant (`Height="*"`)[cite: 18]. Elle affiche la liste des documents (`Pairs`)[cite: 18].

**Design du Tableau :**
*   L'aspect "tableur" classique est supprimé : pas de lignes de grille (`GridLinesVisibility="None"`), lignes à fond alterné (Blanc et Gris très clair `#F8FAFC`), et en-têtes de colonnes transparents et en gras[cite: 18].
*   Au survol et à la sélection, les lignes du tableau se teintent de bleu clair pour faciliter la lecture[cite: 18].

**Colonnes Spécifiques :**
*   **Status (Badge)** : Utilise un `DataTemplate` avec un système de `DataTrigger` pour modifier dynamiquement les couleurs d'un encadré arrondi (Badge) selon la valeur textuelle[cite: 18] :
    *   *Different* : Fond rouge clair, texte rouge foncé[cite: 18].
    *   *Identical* : Fond vert clair, texte vert émeraude[cite: 18].
    *   *MissingInTarget* : Fond jaune/orange clair, texte orange foncé[cite: 18].
    *   *Error* : Fond rouge sombre, texte rouge vif[cite: 18].
*   **Additions & Deletions** : Textes mis en forme avec leurs couleurs sémantiques respectives (Vert pour les ajouts, Rouge pour les suppressions)[cite: 18].
*   **Action (View Report)** : Un bouton inséré directement dans la cellule, visible uniquement si un rapport existe (`Visibility="{Binding HasReport, Converter={StaticResource BooleanToVisibilityConverter}}"`)[cite: 18].