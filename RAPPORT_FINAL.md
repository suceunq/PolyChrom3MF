# Rapport final — PolyChrom 3MF 1.6.11

Les projets `.poly3mf` sont portables et peuvent être partagés comme un fichier unique contenant le modèle 3D et tous les réglages de coloration.

- Snapmaker Orca détecté automatiquement par son exécutable officiel, ses dossiers d'installation, le registre Windows ou un chemin choisi manuellement.
- Version installée du slicer affichée lorsqu'elle est disponible.

Application WPF native française, compilée en Release et publiée en autonome win-x64. Choix de 4 à 32 couleurs au démarrage, import sécurisé, visualisation 3D, quatre motifs fun ou classiques, détection du slicer préféré, projets et export contrôlé sont opérationnels.

Validation : 31 tests automatisés réussis sur 31. Eniac reçoit huit couleurs réparties sur ses 500 000 triangles ; l’export reste manifold et conserve ses dimensions dans PrusaSlicer 2.9.6. Limites : les motifs sont géométriques et procéduraux, ils ne reconnaissent pas sémantiquement les yeux ou les vêtements d’un maillage fusionné ; le STL est interprété en millimètres.
