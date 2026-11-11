# ConfigurationService

Le `ConfigurationService` (`IConfigurationService`) assure la persistance JSON, l'import/export et la mise en cache des expressions régulières compilées du profil de comparaison actif (`ComparisonProfile`).

---

## 🚀 Fonctionnalités Clés

* **Persistance automatique** : Sauvegarde de manière atomique (`.tmp` + `File.Move`) le profil actif dans `%AppData%\PDFComparisonPro\active_profile.json`.
* **Import / Export JSON** : Permet de partager des profils d'exclusion entre équipes QA (`ExportProfile`, `ImportProfile`).
* **Cache Thread-Safe de Regex (`TryGetCompiledRegex`)** : Compile chaque expression régulière une seule fois avec `RegexOptions.Compiled` et un timeout de sécurité de 2 secondes contre les attaques/blocages ReDoS lors des traitements parallèles `Parallel.ForEachAsync`.
