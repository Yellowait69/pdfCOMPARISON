# 1. Démarrage Rapide

**PDF Comparator** compare uniquement le **texte réellement visible à l'œil nu** entre vos documents attendus (**Source**) et obtenus (**Cible**).

---

## Comparer en 3 étapes

1. **Renseignez Source et Cible**  
   Glissez-déposez (ou collez le chemin / cliquez sur l'icône dossier à droite du champ) :
   - soit **deux dossiers** contenant plusieurs PDF,
   - soit **deux fichiers PDF** unitaires.  
   *La détection Dossier / PDF est 100 % automatique.*
2. **Cliquez sur `Comparer`**  
   L'appairage et l'analyse démarrent immédiatement.
3. **Consultez les résultats**  
   Les différences identiques présentes sur plusieurs PDF d'un même code produit sont **regroupées sur une seule ligne** pour éviter toute répétition.

---

## Un tableau visuel par menus déroulants (zéro surcharge)

Les documents conformes et les PDF manquants ne s'affichent jamais dans le tableau principal afin que vous ne voyiez que les vrais écarts :

- **Hiérarchie déroulante claire** : **Canal** (`Bank` / `Broker`, détecté automatiquement depuis le chemin du fichier) ➔ **Produit** (nom exact du dossier contenant `Source` et le dossier de release `2026_Rxx`) ➔ **Différences regroupées**.
- **Mêmes différences regroupées** : lorsqu'une même différence (ou une zone scramblée située au même endroit) apparaît dans plusieurs PDF d'un produit, elle est regroupée sur **une seule ligne** avec un menu déroulant **`X PDF regroupés ▾`** listant chaque PDF concerné.
- **Bouton `Liste des PDF`** *(en haut à droite du bandeau résultats)* : ouvre la liste complète filtrable en 1 clic entre **`Tous les PDF`**, **`Avec erreurs / différences`**, **`Compatibles`** et **`Manquants`**.

---

## Ce qui est filtré automatiquement

Sans aucune configuration de votre part, l'outil ignore déjà les faux positifs techniques :

- **En-tête (130 pt), pied de page (40 pt), marge technique gauche (28 pt) et pistes verticales**
- **Dates dynamiques répétées** (ex. `13/11/2026`, `13.11.2026`, même découpées par le moteur PDF)
- **Noms et prénoms clients** (*Preneur / Souscripteur / Assuré / Verzekeringnemer / Versicherungsnehmer*)
- **Mentions d'exemplaire multilingues en bas de page** (`exemplaire(s)...`, `exemplaar...`, `exemplar...`, `ausfertigung...`, avec ou sans code `/1JQ3B/FIB16`, même si la Cible comporte une page en plus ou en moins)
- **Filigranes** (`SPECIMEN`) et **textes invisibles** écrits en blanc sur fond blanc
