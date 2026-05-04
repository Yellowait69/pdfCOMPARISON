# IndividualReportGenerator

Le `IndividualReportGenerator` est le service responsable de la création d'un rapport PDF visuel détaillé pour une paire spécifique de documents comparés[cite: 5]. Contrairement au rapport de synthèse globale qui liste textuellement les différences, ce service génère un nouveau fichier PDF contenant les pages originales annotées visuellement[cite: 5].

---

## 🛠️ Dépendances (Inversion de contrôle)

Ce service s'appuie sur une dépendance principale injectée via son constructeur :

*   **`IPdfDrawingService`** : Utilisé pour tracer les encadrés de surbrillance (`DrawDiffMarkup`), ajouter les tampons d'identification sur chaque page (`DrawPageStamp`), et charger les polices nécessaires (`LoadFonts`)[cite: 5].

---

## 🎨 Conventions Visuelles

Le service utilise des codes couleurs stricts pour différencier les versions du document lors du rendu visuel[cite: 5] :
*   🔴 **Rouge (`ColorRedSource` : 255, 99, 71)** : Utilisé pour surligner les éléments dans le document **Source** (ce qui a été supprimé ou modifié)[cite: 5].
*   🟢 **Vert (`ColorGreenTarget` : 50, 205, 50)** : Utilisé pour surligner les éléments dans le document **Target / Cible** (ce qui a été ajouté ou modifié)[cite: 5].

---

## 🚀 Mécanisme de Génération (`GenerateIndividualReport`)

La méthode principale `GenerateIndividualReport` orchestre la création du rapport visuel selon le processus suivant[cite: 5] :

### 1. Préparation et Groupement
*   Le service vérifie d'abord la validité des chemins d'accès et crée le répertoire de destination si nécessaire[cite: 5].
*   Il prend en entrée un objet `VisualHighlights` contenant les coordonnées exactes (`LetterLoc`) des différences[cite: 5].
*   La méthode privée `GroupHighlightsByPage` regroupe intelligemment ces coordonnées par numéro de page à l'aide d'un dictionnaire (`Dictionary<int, List<LetterLoc>>`), ce qui optimise grandement le traitement lors du dessin[cite: 5].

### 2. Parcours et Création du Document Combiné
*   Le service ouvre simultanément le document source et le document cible à l'aide de la librairie **PdfPig** (`PdfDocument.Open`) avec l'option `ClipPaths = false`[cite: 5].
*   Il calcule le nombre maximum de pages entre les deux documents pour s'assurer qu'aucune page n'est oubliée[cite: 5].
*   Il boucle ensuite page par page, en insérant séquentiellement dans le nouveau rapport généré :
    1.  **La page Source** : Si elle existe, elle est copiée. Les différences qui la concernent sont surlignées en rouge (`MarkupStyle.Highlight`), et un tampon "SOURCE" est ajouté en haut de la page[cite: 5].
    2.  **La page Cible (Target)** : Si elle existe, elle est copiée à la suite. Les différences sont surlignées en vert, et un tampon "TARGET" est ajouté[cite: 5].

### 3. Sauvegarde
*   Une fois toutes les pages traitées, le document final est compilé et sauvegardé physiquement sur le disque au chemin spécifié (`reportPath`) sous forme d'un tableau d'octets (`File.WriteAllBytes`)[cite: 5].