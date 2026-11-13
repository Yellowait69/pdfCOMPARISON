# CompareStatus (Énumération)

`CompareStatus` est une énumération (Enum) fondamentale dans le modèle de données de l'application[cite: 18].

Elle représente le cycle de vie et l'état final de la comparaison pour une paire de documents donnée (`DocumentPair`)[cite: 18]. Cet état est essentiel à la fois pour la logique métier (l'orchestrateur s'en sert pour savoir ce qu'il doit traiter) et pour l'interface graphique (elle détermine la couleur du badge de statut dans le tableau de bord).

---

## 🚦 Les États Possibles

L'énumération définit cinq états distincts[cite: 18] :

### 1. `Pending` (En attente)
*   **Signification** : C'est l'état par défaut lorsqu'une paire est créée lors de la phase de correspondance (matching)[cite: 18]. Il indique que les fichiers sont prêts mais que l'analyse n'a pas encore commencé, ou que le traitement a été annulé par l'utilisateur avant d'atteindre ce document[cite: 18].

### 2. `Identical` (Identiques)
*   **Signification** : La comparaison s'est déroulée avec succès et aucune différence n'a été trouvée entre le document source et le document cible (après l'application des filtres de masquage intelligents et des normalisations)[cite: 18].
*   **Impact UI** : Généralement affiché avec un badge vert. Aucun rapport de différence n'est généré pour ne pas encombrer l'utilisateur.

### 3. `Different` (Différents)
*   **Signification** : L'analyse a détecté au moins une différence textuelle ou visuelle valide entre la source et la cible[cite: 18].
*   **Impact UI** : Généralement affiché avec un badge rouge. C'est le seul état qui garantit la génération d'un rapport PDF annoté disponible pour l'utilisateur.

### 4. `Error` (Erreur)
*   **Signification** : Un problème technique a empêché la comparaison d'aboutir[cite: 18]. Cela peut inclure des fichiers illisibles (nécessitant un OCR), des PDF protégés par mot de passe, ou des fichiers verrouillés par un autre processus (ex: ouverts dans Adobe Reader)[cite: 18].
*   **Impact UI** : Affiché avec un badge rouge foncé.

### 5. `MissingInTarget` (Manquant dans la cible)
*   **Signification** : Le fichier source a été trouvé, mais aucun fichier cible correspondant (basé sur la clé de nommage) n'existe dans le répertoire de destination[cite: 18].
*   **Impact UI** : Affiché avec un badge orange/jaune. Ces documents sont ignorés par l'orchestrateur lors du lancement de la comparaison.