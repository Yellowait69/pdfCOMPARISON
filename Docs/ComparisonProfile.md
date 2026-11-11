# ComparisonProfile

Le modèle `ComparisonProfile` centralise l'ensemble des règles dynamiques d'exclusion, de masquage par expressions régulières et de tolérance visuelle/structurelle de l'application. Il remplace toutes les valeurs précédemment codées en dur dans le code.

---

## 🧱 Structures de Données

### 1. `ExclusionZoneRule` (Zones d'exclusion géométriques)
Permet de définir un rectangle sur la page PDF dans lequel tout texte sera ignoré :
* **`Scope` (`PageScope`)** : `AllPages`, `FirstPageOnly`, `LastPageOnly`, `EvenPages`, `OddPages`, `CustomRange`.
* **`Unit` (`CoordinateUnit`)** : `PdfPoints` (72 DPI) ou `NormalizedRatio` (`[0.0 .. 1.0]`).
* **`Origin` (`ZoneOrigin`)** : `TopLeft` (idéal pour les en-têtes et le tracé à la souris) ou `BottomLeft` (idéal pour les pieds de page quelle que soit la hauteur du PDF).
* **`CollectDatesInZone`** : Si activé, les dates détectées dans cette zone (ex: en-tête) sont mémorisées pour être aussi masquées dans le corps du document.

### 2. `RegexMaskingRule` (Masquage par expressions régulières)
Permet de remplacer dynamiquement des données variables (dates, numéros de facture, références, ancres de signature) par un jeton neutre (`[DATE_IGNORE]`, `[REF_IGNORE]`, etc.) :
* **`Pattern`** : Expression régulière compilée dynamiquement.
* **`ReplacementMask`** : Jeton de substitution (ou vide pour supprimer l'élément).
* **`MinOccurrences`** : Nombre minimal d'apparitions dans le document pour déclencher le masquage (`1` = toujours masquer, `2` = masquer uniquement si la valeur est répétée).
