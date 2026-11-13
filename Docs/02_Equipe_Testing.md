# 2. Équipe Testing & Boutons de Scan Automatique

En plus du bouton **Équipe Testing** (pré-configuré pour `\\sppdfc01\TIDES\Life`), vous pouvez créer et partager autant de boutons de scan automatique que nécessaire via **`Paramètres`**.

---

## Lancer un bouton de scan (`Équipe Testing` ou personnalisé)

1. Cliquez sur le bouton voulu en haut à droite (ex. **`Équipe Testing`**).  
   *La fenêtre s'ouvre directement en grand écran.*
2. Vérifiez le(s) dossier(s) affiché(s) (et la **Release** si activée), puis cliquez sur **`Scanner`**.
3. Cochez vos dossiers/produits en 1 clic (**`Cocher prêts`**) et cliquez sur **`Lancer la comparaison`**.
4. Besoin d'adapter ce bouton à la volée ? Cliquez sur **`Configurer`** directement en haut à droite de la fenêtre.

---

## Créer et partager vos propres boutons (`Paramètres`)

Ouvrez **`Paramètres`** ➔ onglet **`2. Boutons de Scan Automatique`** :

- **3 logiques de dossiers au choix** :
  1. **Par sous-dossier produit** (ex. `<Racine>\<Canal>\<Produit>\Source` vs `<Release>\Target`, avec ou sans canaux ni release).
  2. **Deux grands dossiers séparés** (un dossier *Source* global et un dossier *Cible* global comparés en miroir).
  3. **Découverte automatique** (trouve récursivement tous les couples `Source/Target`, `Attendu/Obtenu`, `Ref/Cand`, `Avant/Après`...).
- **Noms de PDF totalement différents supportés** : le mode **Intelligent auto** appaire automatiquement les bons PDF grâce à la similarité de leur contenu texte, même si leurs noms n'ont aucun point commun.
- **Règles dédiées & partage `.json`** : chaque bouton peut posséder ses propres zones/règles d'exclusion et être partagé en un fichier via **`Partager (.json)`** / **`Importer (.json)`**.
