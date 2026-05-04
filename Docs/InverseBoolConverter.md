# InverseBoolConverter

La classe `InverseBoolConverter` est un outil d'interface graphique (UI) spécifique au framework WPF[cite: 18]. Elle fait partie de l'espace de noms `PDFComparison.Converters` et implémente l'interface standard `IValueConverter`[cite: 18].

Son rôle est extrêmement simple mais indispensable en XAML : elle prend une valeur booléenne (`true` ou `false`) provenant du ViewModel et renvoie son inverse strict pour l'affichage ou le comportement de la vue[cite: 18].

---

## 🛠️ Fonctionnement des Méthodes

### `Convert` (ViewModel vers Vue)
C'est la méthode exécutée lorsque la donnée transite du code métier vers l'interface graphique[cite: 18].
*   Elle vérifie d'abord si la valeur reçue (`value`) est bien de type booléen (`if (value is bool b)`)[cite: 18].
*   Si c'est le cas, elle retourne l'inverse de cette valeur (`return !b`)[cite: 18]. (Exemple : `true` devient `false`).
*   Si la valeur n'est pas un booléen (cas d'erreur de binding), elle renvoie la valeur telle quelle par sécurité (`return value`)[cite: 18].

### `ConvertBack` (Vue vers ViewModel)
Cette méthode est appelée lorsque la donnée fait le chemin inverse (par exemple, si l'utilisateur coche une case à cocher qui doit modifier le ViewModel)[cite: 18].
*   Ici, elle lève une exception `NotImplementedException`[cite: 18].
*   **Pourquoi ?** Parce que ce convertisseur est pensé pour être utilisé dans des liaisons à sens unique (*One-Way Binding*), généralement pour gérer l'affichage conditionnel (visibilité, activation de boutons). Si un besoin bidirectionnel se présente à l'avenir, cette méthode devra être implémentée pour renvoyer également `!b`.

---

## 💡 Cas d'usage classique en XAML

Bien que le `MainViewModel` utilise intelligemment des propriétés dérivées (comme `IsNotProcessing`), ce convertisseur est souvent déclaré dans les ressources XAML de cette façon :
```
<Window.Resources>
    <conv:InverseBoolConverter x:Key="InverseBoolConverter"/>
</Window.Resources>
Et utilisé pour désactiver un élément quand une condition est vraie :

<!-- Le bouton "Traiter" devient inactif (IsEnabled=False) lorsque IsLoading passe à True -->
<Button Content="Traiter" IsEnabled="{Binding IsLoading, Converter={StaticResource InverseBoolConverter}}" />