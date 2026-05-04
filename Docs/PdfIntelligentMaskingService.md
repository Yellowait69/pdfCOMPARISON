# PdfIntelligentMaskingService

Le `PdfIntelligentMaskingService` est un composant crucial pour réduire le bruit lors de la comparaison de documents contractuels ou administratifs[cite: 13].

Souvent, deux versions d'un même contrat sont identiques sur le fond, mais diffèrent parce que la date d'édition a changé en pied de page, ou parce que le nom du client a été regénéré[cite: 13]. Le but de ce service est de repérer ces données variables (dates répétées, noms de clients) et de les remplacer par des balises génériques (ex: `[DATE_IGNORE]`) avant que l'algorithme de différence ne soit lancé[cite: 13].

---

## 🔍 Logique de Détection (Expressions Régulières)

Le service s'appuie sur plusieurs Regex compilées pour identifier des motifs spécifiques[cite: 13] :

*   **`DateRegex`** : Cherche tous les formats de date standards (`JJ/MM/AAAA`, `JJ.MM.AA`, `JJ-MM-AAAA`)[cite: 13].
*   **`ClientKeywordRegex`** : Cherche des mots-clés spécifiques aux contrats selon plusieurs langues (ex: *Auftraggeber* en Allemand, *Opdrachtgever* en Néerlandais, *Souscripteur* en Français)[cite: 13].
*   **`DynamicTextNameRegex`** & **`UpperWordRegex`** : Cherchent des mots entièrement en majuscules (typiquement des noms de famille ou de sociétés) situés juste après ces mots-clés ou suivis d'une virgule[cite: 13].

---

## 🚀 Fonctionnement du Masquage

Le service propose deux méthodes principales qui effectuent le même type d'analyse, mais sur des structures de données différentes.

### 1. Masquage sur Texte Brut (`MaskRepeatingTextElements`)
Cette méthode prend une chaîne de caractères continue (`string`) et modifie le texte directement[cite: 13].

**Pour les dates :**
*   Le service liste toutes les dates du document et normalise leur format en interne (en remplaçant les `/` et `-` par des points `.`) pour pouvoir les comparer[cite: 13].
*   Si une **même date** apparaît 2 fois ou plus dans le texte, elle est jugée comme étant une "date système" (comme une date d'impression) et toutes ses occurrences sont remplacées par `[DATE_IGNORE]`[cite: 13].

**Pour les noms :**
*   Le service cherche si un mot-clé client (ex: "Souscripteur") est suivi d'un nom en majuscules[cite: 13].
*   S'il trouve ce nom, il remplace **toutes** les occurrences de ce nom exact dans le reste du texte par `[NOM_IGNORE]`[cite: 13].

### 2. Masquage sur Mots Spatiaux (`MaskRepeatingWordElements`)
Cette méthode est plus complexe car elle doit agir sur la liste d'objets `PdfWordInfo` sans casser les coordonnées (X, Y) des lettres pour l'affichage visuel[cite: 13].

*   Elle reconstruit un texte continu virtuel en gardant une trace exacte de l'index de chaque caractère (`charToWord`)[cite: 13].
*   Elle applique les mêmes règles de détection (dates répétitives ou présentes dans l'en-tête, noms de clients)[cite: 13].
*   Lorsqu'un élément doit être masqué (ex: une date étalée sur 3 mots "12 / 2023"), le service :
    1. Regroupe les propriétés spatiales (`Letters`) des 3 mots dans le premier mot[cite: 13].
    2. Change le texte du premier mot en `[DATE_IGNORE]`[cite: 13].
    3. Vide le contenu des autres mots, qui sont ensuite supprimés de la liste (`RemoveAll`)[cite: 13].

---

## 🎯 Bénéfice
Grâce à ce service, l'orchestrateur compare des textes qui ressemblent à :
> "Contrat signé le `[DATE_IGNORE]` par M. `[NOM_IGNORE]`"

Cela garantit que les vraies modifications juridiques ressortent, sans que l'utilisateur ne soit pollué par de faux positifs liés à la date d'impression du PDF.