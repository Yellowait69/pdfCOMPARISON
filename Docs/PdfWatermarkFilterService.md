# PdfWatermarkFilterService

Le `PdfWatermarkFilterService` agit comme un bouclier anti-bruit pour le moteur d'extraction[cite: 13].

Très souvent, les documents contractuels contiennent d'énormes filigranes diagonaux (ex: "SPECIMEN", "TEST") ou des balises techniques invisibles (ancres de signature électronique comme `#SIGN_1#`)[cite: 13]. Si ces éléments ne sont pas ignorés, ils fausseront complètement l'analyse des différences. Ce service détecte et supprime ces anomalies à la volée[cite: 13].

---

## 🔍 Logique de Détection (Expressions Régulières)

Le service s'appuie sur un arsenal d'expressions régulières (`Regex`) compilées pour traquer les indésirables[cite: 13] :

*   **`SignatureAnchorRegex` (`#[A-Z0-9_]+#`)** : Détecte les balises d'ancrage utilisées par les logiciels de signature électronique (ex: DocuSign, Yousign)[cite: 13].
*   **`WatermarkTextRegex` & `WatermarkFragments`** : Ciblent toutes les déclinaisons et fautes de frappe possibles du mot "SPECIMEN" (ex: *SPECI*, *CIMEN*, *TOTEIN*, *TEST*)[cite: 13].
*   **`StampRegex`** : Supprime les tampons d'identification ("[ DOCUMENT SOURCE ]") qui auraient pu être ajoutés artificiellement lors de manipulations précédentes du PDF[cite: 13].
*   **`WatermarkCodeRegex`** : Filtre les codes de filigranes spécifiques de type `Q000`, `D000`, etc[cite: 13].

---

## 🛡️ Les Garde-Fous (`ProtectedDataRegex` & `SafeShortWords`)

L'enjeu d'un tel filtre est d'éviter les faux positifs (effacer un mot légitime). Le service inclut des protections strictes[cite: 13] :
*   **Protection des données (`ProtectedDataRegex`)** : Si le mot analysé est un nombre, une date ou une devise (`€`, `$`, `£`), il est immédiatement sanctuarisé et ne sera **jamais** considéré comme un filigrane[cite: 13].
*   **Les petits mots (`SafeShortWords`)** : À cause de l'espacement des lettres dans les PDF, un grand mot comme "S P E C I M E N" est souvent lu comme des lettres individuelles ("S", "P", "EN"). Le service connaît ces fragments, mais ne les supprime pas si leur taille de police est normale (inférieure à `15.0`) pour ne pas effacer les vrais mots ou prépositions[cite: 13].

---

## 🚀 Méthodes Principales

Le service propose deux approches complémentaires de nettoyage :

### 1. Nettoyage Textuel Brut (`CleanRawText`)
Cette méthode prend une chaîne de caractères complète et en retire purement et simplement tous les textes indésirables via une série de remplacements Regex (`Replace`)[cite: 13]. Elle est utilisée pour nettoyer les lignes complètes reconstruites.

### 2. Évaluation Spatiale (`IsWatermark`)
Cette méthode est utilisée au plus bas niveau, lors du parcours des mots via *PdfPig* (`Word`). Elle combine l'analyse textuelle avec l'**analyse typographique**[cite: 13] :
1.  Elle vérifie si le mot correspond à une ancre de signature ou s'il est protégé (date/nombre)[cite: 13].
2.  Elle vérifie la **taille de la police** (`PointSize`). Les filigranes sont généralement écrits en très gros. Si la taille maximale d'une lettre du mot dépasse `18.0` points, le mot est radicalement classé comme filigrane et sera ignoré par l'extracteur[cite: 13].
3.  Elle vérifie enfin si le texte correspond à l'un des fragments de filigrane connus (ex: "SPECIMEN")[cite: 13].

---

## 🎯 Bénéfice
Grâce à ce filtre, un énorme "SPECIMEN" gris clair écrit en diagonale sur la page entière d'un contrat ne sera même pas lu par le comparateur, évitant ainsi d'inonder le rapport final de différences factices.