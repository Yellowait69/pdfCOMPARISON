# DocumentationWindow (Vue)

Le fichier `DocumentationWindow.xaml` définit l'interface graphique de la fenêtre de documentation intégrée à l'application[cite: 18]. Il est lié à son ViewModel (`DocumentationViewModel`) via la propriété `DataContext`[cite: 18].

Cette fenêtre est conçue pour être claire, moderne et facile à lire, en divisant l'espace en deux zones distinctes : un menu de navigation latéral à gauche et une zone d'affichage du contenu Markdown à droite[cite: 18].

---

## 🎨 Design Général et Structure

La fenêtre utilise une structure en grille (`Grid`) principale divisée en trois colonnes :
1.  **Menu Latéral (Colonne 0)** : Largeur fixe de 250 pixels[cite: 18].
2.  **Gouttière (Colonne 1)** : Espace vide de 20 pixels pour aérer le design[cite: 18].
3.  **Contenu Principal (Colonne 2)** : Prend tout l'espace restant (`Width="*"`)[cite: 18].

Pour un aspect moderne, les deux zones principales (gauche et droite) sont enveloppées dans des `Border` avec un fond blanc (`Background="White"`), des coins arrondis (`CornerRadius="8"`) et une légère ombre portée (`DropShadowEffect`) pour les détacher de l'arrière-plan gris clair (`#F8FAFC`) de la fenêtre[cite: 18].

---

## 🧭 Le Menu Latéral (ListBox)

Le menu de gauche affiche la liste des documents disponibles[cite: 18]. Il utilise un contrôle `ListBox` lié (`ItemsSource`) à la collection `Documents` du ViewModel[cite: 18].

### Style Personnalisé (ItemContainerStyle)
Pour éviter le rendu par défaut un peu brut de WPF (le fameux carré bleu vif au survol ou à la sélection), le style des éléments de la liste a été redéfini :
*   **Survol (`IsMouseOver`)** : Le fond devient légèrement gris (`#F1F5F9`)[cite: 18].
*   **Sélection (`IsSelected`)** : Le fond devient bleu pastel (`#DBEAFE`) et le texte passe en bleu foncé (`#1D4ED8`)[cite: 18].
*   **Curseur** : Le curseur se change en "main" (`Cursor="Hand"`) pour indiquer clairement que l'élément est cliquable[cite: 18].

### Modèle de Données (ItemTemplate)
Chaque élément de la liste est composé de :
*   Une icône document (`&#xE8A5;`) tirée de la police Windows standard `Segoe MDL2 Assets`[cite: 18]. Grâce à un astucieux *Binding*, cette icône prend automatiquement la même couleur que le texte (bleue si sélectionnée, grise sinon)[cite: 18].
*   Le titre du document (`Title`), qui correspond au nom du fichier `.md`[cite: 18].

---

## 📖 Le Moteur de Rendu Markdown (MdXaml)

La zone de droite est la pièce maîtresse de cette fenêtre. Elle utilise le composant externe **MdXaml** (`md:MarkdownScrollViewer`)[cite: 18].

### Fonctionnement
*   La propriété `Markdown` de ce contrôle est directement liée à la propriété `MarkdownContent` du ViewModel[cite: 18]. Dès que le ViewModel lit un nouveau fichier, le composant se rafraîchit automatiquement.
*   **MdXaml** interprète la syntaxe Markdown (les `#` pour les titres, les `*` pour les listes, les blocs de code entre ```) et les transforme à la volée en contrôles visuels natifs de WPF (TextBlocks, Run, etc.).

### Personnalisation du Style (`DocumentStyle`)
Pour que le texte rendu par MdXaml s'intègre parfaitement avec le reste de l'application, un style spécifique a été appliqué au `FlowDocument` (le conteneur interne de MdXaml) :
*   **Police** : Segoe UI[cite: 18].
*   **Taille de base** : 14 pixels[cite: 18].
*   **Couleur de base** : Gris foncé (`#334155`) pour une lecture confortable[cite: 18].