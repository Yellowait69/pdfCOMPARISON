# PdfVisualRegressionService

Le `PdfVisualRegressionService` (`IPdfVisualRegressionService`) implémente le moteur de **Régression Visuelle Perceptuelle** de l'application sans nécessiter de binaire C++ externe.

---

## 🧠 Algorithmes Implémentés

1. **Rastérisation Déterministe Normalisée (`RenderNormalizedPageBitmap`)** :
   * Dessine les images embarquées du PDF (`page.GetImages()`) et la typographie filtrée/masquée à la résolution `VisualRenderDpi` tout en occultant en blanc les zones d'exclusion géométriques (`ExclusionZones`).
2. **Recalage Automatique Sub-Pixel / Translation (`FindOptimalTranslationShift`)** :
   * Recherche le vecteur de translation $(\Delta x, \Delta y) \in [-\text{MaxShift} .. +\text{MaxShift}]$ minimisant l'erreur absolue moyenne de luminance afin d'absorber les légers décalages de marges d'impression ou de moteur PDF.
3. **Indice de Similarité Structurelle SSIM en $O(1)$ par Images Intégrales (`BuildIntegralImages`)** :
   * Calcule la moyenne locale $\mu_1, \mu_2$, la variance $\sigma_1^2, \sigma_2^2$ et la covariance $\sigma_{12}$ sur une fenêtre $9 \times 9$ en temps constant par pixel grâce à 5 tables d'aires sommées (Integral Images).
4. **Distance Colorimétrique Perceptuelle YIQ & Détection d'Anti-Aliasing (`IsAntiAliasedPixel`)** :
   * Évalue la distance perceptuelle dans l'espace **YIQ** ($\Delta E_{\text{YIQ}}^2$) et applique l'algorithme de **Vysniauskas (Pixelmatch)** sur les 8 voisins pour ignorer les pixels d'anti-aliasing sur les contours de caractères.
5. **Filtrage Morphologique, Clustering BFS & Captures Annotées (`CreateAnnotatedSideBySideCrops`)** :
   * Élimine les pixels isolés, regroupe les différences en composantes connexes (`MinVisualClusterAreaPx`) et génère les vignettes PNG annotées (`SourceImage` et `TargetImage`) intégrées dans le `Global_Synthesis_Report.pdf`.
