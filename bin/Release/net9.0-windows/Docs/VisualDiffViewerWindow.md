# VisualDiffViewerWindow (Inspecteur Visuel Interactif)

## Rôle
`VisualDiffViewerWindow` est une fenêtre d'inspection visuelle dédiée permettant d'analyser page par page les différences entre le document PDF Attendu (Source) et Obtenu (Cible) directement dans l'application.

## Modes d'Inspection Intégrés
1. **Côte à Côte (`A | B`)** : Affiche simultanément le rendu normalisé de la page Source et de la page Cible avec zoom synchronisé.
2. **Superposition Calque (Pelure d'oignon)** : Superpose la page Cible sur la page Source avec un curseur d'opacité continu (`0%` à `100%`) pour contrôler l'alignement typographique et graphique.
3. **Mode Blink A/B (Bascule Rapide)** : Alterne automatiquement toutes les `650 ms` entre la page Attendue et la page Obtenue afin que l'œil humain repère instantanément le moindre décalage ou changement de chiffre.
4. **Carte Thermique (`Heatmap SSIM`)** : Estompe en gris clair les zones identiques et colore en rouge/magenta vif uniquement les pixels en écart perceptuel réel, encadrés par leurs boîtes englobantes SSIM.
