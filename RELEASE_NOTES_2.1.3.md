# PolyChrom 3MF 2.1.3 — import couleur et peinture précise

Cette version aligne l’affichage et la peinture sur le comportement attendu d’un slicer moderne, tout en réduisant les coûts mémoire des longs traits et des maillages denses.

## Import et fidélité des couleurs

- Conservation automatique des couleurs déjà présentes dans les fichiers 3MF.
- Lecture des filaments Bambu Studio et OrcaSlicer, ainsi que des ressources `basematerials` standard 3MF.
- Correction des indices de matériaux lorsque plusieurs ressources ou fragments 3MF utilisent les mêmes numéros locaux.
- Affichage fidèle des noirs, rouges et blancs, sans teinte bleue ni effet délavé.
- Import neutre pour les modèles sans couleur ; aucune palette n’est appliquée sans action de l’utilisateur.

## Outils de peinture

- Outils triangle, cercle, rectangle, lasso, sphère et plage de hauteur.
- Palette visuelle avec pastilles de couleur et codes hexadécimaux.
- Surbrillance avant application pour les triangles et les plages de hauteur visibles.
- Peinture triangle par triangle en maintenant le clic gauche.
- Gomme, pipette, remplissage d’îlot et réinitialisation vers les couleurs importées.
- Raccourcis `[` et `]` pour régler rapidement la taille du pinceau.
- La caméra reste à sa position après un trait ou une subdivision locale.

## Performances et stabilité

- Une seule sauvegarde d’annulation est créée par trait continu au lieu d’une copie complète par triangle.
- Le raffinement local respecte un budget adaptatif de 750 000 triangles et de 250 000 nouveaux triangles par opération.
- Les caches de sélection sont invalidés après chaque changement de topologie afin d’éviter les clics décalés ou inactifs.
- Les couleurs importées sont remappées pendant la subdivision pour garantir une réinitialisation et un export cohérents.
- Renforcement des contrôles sur les archives 3MF, projets, styles et projets de logos.

## Validation

- 134 tests automatisés réussis en configuration Release.
- Tests dédiés aux couleurs `#FFFFFF`, `#000000` et `#DE4343`.
- Tests de précision du pinceau, du lasso, de la plage de hauteur visible et du budget mémoire.
