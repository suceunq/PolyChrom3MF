# Rapport de tests — PolyChrom 3MF 1.6.10

- Compilation Release : réussie, 0 erreur, 0 avertissement.
- Tests automatisés : **38/38 réussis**, 0 échec, incluant grands fragments 3MF, génération de huit couleurs distinctes, export sécurisé, projet portable relu après suppression du modèle source, détection de Snapmaker Orca et validation cryptographique des mises à jour GitHub.
- Projet partagé `.poly3mf` : modèle 3MF intégré, deux objets et toutes les affectations restaurés sans dépendre du fichier source original.
- Eniac à huit couleurs : 500 000 triangles répartis sur les huit indices ; dimensions inchangées et maillage manifold dans PrusaSlicer 2.9.6.
- Test d’intégration réel : PrusaSlicer 2.9.6 détecté automatiquement puis lancé depuis PolyChrom avec Eniac chargé.
- Contrôle visuel : noms et codes visibles sous les seize échantillons ; infobulle explicitement rendue avec fond sombre et texte blanc.
- Contrôle visuel : quatre menus Windows standards, texte noir lisible, fond système, séparateurs et survol natifs.
- Contrôle visuel : fenêtre « À propos », crédit 3D TER et lien TikTok visibles et lisibles.
- Eniac « Graffiti pop » : 500 000 triangles répartis en 198 408 / 65 159 / 191 439 / 44 994 triangles sur les quatre couleurs.
- Publication autonome win-x64 : réussie.
- Démarrage portable : réussi.
- PrusaSlicer 2.9.6 : exemples, STL converti et fichier Eniac exporté ouverts ; Eniac conserve 500 000 facettes et ses dimensions.
- Installateur : compilation, installation, démarrage, désinstallation, réinstallation et second démarrage réussis.
