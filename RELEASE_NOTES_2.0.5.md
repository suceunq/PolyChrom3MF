# PolyChrom 3MF 2.0.5

## Correction de l’export multicolore

- Correction de l’affichage monochrome dans Snapmaker Orca.
- Ajout de `paint_color` sur chaque triangle, avec le codage de filament attendu par Snapmaker Orca U1.
- Conservation simultanée de la peinture native Orca/Prusa/Bambu et des propriétés de matériaux 3MF standard.
- Export sous forme de projet 3MF natif : objets parents, volumes maillés, relations et paramètres de filaments.
- Conservation du profil d’imprimante et des paramètres du projet source lorsqu’ils sont disponibles.
- Le bouton **Ouvrir dans mon slicer** ouvre toujours la version colorée courante.

## Validation

- Test réel réussi dans Snapmaker Orca 2.3.5 avec un modèle de 769 471 triangles et quatre filaments.
- 87 tests automatisés réussis.
