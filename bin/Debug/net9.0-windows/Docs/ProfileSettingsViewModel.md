# ProfileSettingsViewModel

Le `ProfileSettingsViewModel` pilote la fenêtre de configuration dynamique des exclusions (`ProfileSettingsWindow`).

---

## 🛠️ Responsabilités

* **Édition des zones d'exclusion (`ExclusionZones`)** : Ajout, suppression et création directe de zones par sélection rectangulaire à la souris (`AddZoneFromCanvasLasso`).
* **Aperçu PDF interactif (`LoadSamplePdfCommand`, `RefreshVisualPreview`)** : Charge un document PDF témoin via `PdfPig`, projette les boîtes englobantes de tous les mots sur une feuille A4 virtuelle et colorie en temps réel en **rouge** les mots exclus et en **vert** les mots conservés.
* **Banc de test Regex en direct (`RunRegexPreviewCommand`)** : Permet de tester instantanément l'effet des règles `RegexMaskingRule` et du masquage de noms de clients sur un texte d'exemple.
