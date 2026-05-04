# PdfExtractionService

Le `PdfExtractionService` est le moteur de lecture principal de l'application[cite: 11]. Il s'appuie sur la librairie **PdfPig** pour ouvrir les fichiers PDF, analyser leur structure interne et en extraire le contenu (texte brut ou objets spatiaux)[cite: 11].

Son rôle majeur est de filtrer intelligemment le "bruit" visuel du PDF pour ne conserver que le texte utile à la comparaison[cite: 11].

---

## 🛠️ Dépendances (Inversion de contrôle)

Ce service orchestre plusieurs sous-services pour nettoyer les données à la volée, qui sont injectés dans son constructeur[cite: 11] :

*   **`IPdfWatermarkFilterService`** : Détecte et retire les filigranes ou textes parasites[cite: 11].
*   **`IPdfIntelligentMaskingService`** : Masque les données variables (comme les dates répétées) pour éviter de fausses différences[cite: 11].
*   **`IPdfTextNormalizerService`** : Gère la normalisation finale du texte extrait[cite: 11].

---

## 🚀 Méthodes Principales

### 1. Extraction Rapide (`ExtractTextFast`)
Cette méthode lit le PDF et reconstruit un texte brut continu (`string`) en gérant l'espacement et les sauts de ligne[cite: 11].

**Logique de filtrage spatial :**
Pour chaque mot, le service ignore le texte s'il se trouve dans des zones définies comme parasites[cite: 11] :
*   **En-têtes** : Les 130 derniers points en haut de la page (`page.Height - 130.0`)[cite: 11].
*   **Pieds de page** : Les 40 premiers points en bas de la page (`40.0`)[cite: 11].
*   **Marge gauche** : Les 50 premiers points sur la gauche (`50.0`)[cite: 11].

**Reconstruction des lignes :**
Il compare les coordonnées `X` et `Y` des mots successifs :
*   Si la différence de hauteur (`Y`) dépasse `5.0` points, il considère qu'il y a un saut de ligne (`sb.AppendLine`)[cite: 11].
*   Si l'écart horizontal (`X`) entre deux mots dépasse `2.0` points, il insère un espace[cite: 11].
*   Le texte final passe dans le service de masquage intelligent avant d'être retourné[cite: 11].

### 2. Extraction Spatiale (`ExtractWords`)
Contrairement à l'extraction rapide, cette méthode conserve les coordonnées exactes en retournant une liste de `PdfWordInfo`[cite: 11]. C'est essentiel pour pouvoir dessiner les surbrillances plus tard.

*   Elle applique les mêmes règles de filtrage spatial (en-têtes, pieds de page, marges)[cite: 11].
*   **Gestion des dates d'en-tête** : Si une date est détectée dans la zone d'en-tête, elle est mise en cache (`headerDatesToIgnore`) afin que le masque intelligent puisse l'ignorer dans le reste du document[cite: 11].
*   Elle enregistre les lettres individuelles (`word.Letters`) et le numéro de la page (`page.Number`)[cite: 11].

### 3. Détection de Textes Cachés (`IsHiddenOrWhiteWord`)
Cette méthode privée protège la comparaison contre le texte invisible souvent généré par les logiciels d'OCR[cite: 11].
*   Elle ignore les mots dont la taille de police (`PointSize`) est inférieure ou égale à `1.0`, ou dont la largeur est quasi nulle (`<= 0.1`)[cite: 11].
*   *Exception* : Les ponctuations simples (`.`, `,`, `-`, `'`) ne sont pas soumises à ce filtre pour ne pas casser la structure des phrases[cite: 11].

### 4. Normalisation (`NormalizePdfText`)
Une simple méthode relais (proxy) qui transmet le texte brut au `IPdfTextNormalizerService` pour uniformiser la typographie[cite: 11].