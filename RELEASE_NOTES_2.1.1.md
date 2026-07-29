# PolyChrom 3MF 2.1.1 — fidélité des logos

Cette mise à jour corrective reconstruit la façon dont les logos sont prévisualisés et convertis en couleurs imprimables.

## Corrections

- Le logo visible sur la pièce respecte désormais exactement les proportions du fichier importé.
- L’aperçu transparent est affiché directement dans le cadre de placement, avec la couleur de filament choisie.
- Les lettres, les trous et les traits fins ne sont plus remplacés par de grands triangles irréguliers.
- Un seul pixel ou point de contact ne colore plus une face entière.
- Le fond et les motifs déjà présents restent inchangés pendant le déplacement du logo.

## Qualité et performances

- Subdivision locale adaptative jusqu’à six niveaux, uniquement sous l’empreinte du logo.
- Prise en compte de toute l’empreinte avant subdivision afin de ne perdre aucun détail fin.
- Budget de triangles adapté automatiquement à la taille du modèle.
- Export 3MF conservant les contours affinés et les couleurs du logo.

## Validation

- Compilation Windows sans avertissement ni erreur.
- 126 tests automatisés réussis.
- Tests dédiés aux traits fins, à la transparence, à la couverture partielle des triangles et à l’export 3MF.
