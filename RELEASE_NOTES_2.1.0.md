# PolyChrom 3MF 2.1.0 — logos et images reconstruits

Cette version réintroduit le placement de logos après le retrait préventif du module expérimental dans la version 2.0.15. L’ancien moteur reste inaccessible : le nouveau module a été reconstruit dans une bibliothèque isolée et couvert par des tests automatisés.

## Nouveautés

- Import des fichiers PNG, JPG, JPEG, WebP et SVG.
- Conservation de la transparence et aperçu sur damier avant validation.
- Suppression automatique du fond relié aux bords, suppression de la couleur dominante, tolérance réglable, gomme et restauration.
- Placement direct sur un objet et ancrage réel au maillage.
- Projections plane, cylindrique et adaptée aux surfaces courbes.
- Déplacement, redimensionnement uniforme ou libre, rotation, inclinaison, miroirs et relief.
- Répétitions horizontales, verticales ou circulaires avec espacement réglable.
- Chaque logo et chaque copie restent indépendants, modifiables, duplicables, masquables et supprimables.
- Subdivision locale uniquement sous les logos lorsque le maillage nécessite davantage de précision.
- Sauvegarde portable des images, calques, positions et transformations dans les projets `.poly3mf`.
- Export 3MF compatible multicolore, sans perte des affectations des logos.

## Fiabilité et performances

- Le fond du modèle est recomposé depuis une base immuable : déplacer ou supprimer un logo ne transforme plus les autres motifs.
- Les SVG sont analysés sans DTD, scripts ni ressources externes.
- Les images corrompues et projets incomplets sont refusés proprement.
- Les calculs sont annulables et parallélisés sur les maillages denses.
- L’export 3MF est désormais écrit et vérifié en flux continu, sans seconde copie XML géante.
- Essais réels validés sur un 3MF de 2 983 116 triangles et un STL de 9 585 474 triangles.
- 124 tests automatisés réussis sur les imports, le détourage, les projections, la duplication, le relief, la sauvegarde et l’export.
