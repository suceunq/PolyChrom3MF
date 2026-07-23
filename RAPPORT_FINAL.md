# Rapport final — PolyChrom 3MF 1.7.1

Les projets `.poly3mf` sont portables et peuvent être partagés comme un fichier unique contenant le modèle 3D, les propositions, les affectations par triangle et, le cas échéant, le PNG avec ses réglages.

- Snapmaker Orca détecté automatiquement par son exécutable officiel, ses dossiers d'installation, le registre Windows ou un chemin choisi manuellement.
- Version installée du slicer affichée lorsqu'elle est disponible.
- Motifs PNG projetés selon quatre méthodes puis convertis vers 4 à 32 couleurs imprimables.
- Sélection cumulative d’une face précise ou de petites zones pour appliquer manuellement une couleur.
- Bienvenue modernisée avec soutien PayPal facultatif et adresse officielle strictement contrôlée.

Application WPF native française, compilée en Release et publiée en autonome win-x64. Choix de 4 à 32 couleurs au démarrage, import sécurisé, visualisation 3D, motifs procéduraux ou PNG, retouches manuelles, détection du slicer préféré, projets partageables et export contrôlé sont opérationnels.

Validation : 48 tests automatisés réussis sur 48. Eniac (500 000 triangles) et Samurai (1 440 460 triangles) ont été importés, colorés en huit couleurs, exportés puis relus avec dimensions identiques. Limites : les motifs ne reconnaissent pas sémantiquement les yeux ou les vêtements d’un maillage fusionné ; le STL est interprété en millimètres.
