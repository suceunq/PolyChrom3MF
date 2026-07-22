# Compatibilité 3MF

Le moteur conserve le conteneur et tous ses fragments XML, puis ajoute un groupe `basematerials` standard dans chaque fragment contenant un maillage. Les dimensions, unités, transformations, relations et entrées privées restent inchangées. Les STL ASCII et binaires sont convertis vers un conteneur 3MF standard.

Test réel : **PrusaSlicer 2.9.6**, ouverture des exemples, du fichier Eniac exporté (500 000 facettes manifold) et d’un STL converti en 3MF. Géométrie et dimensions conservées. Bambu Studio, OrcaSlicer et Cura n’étaient pas installés et ne sont pas annoncés comme testés.
