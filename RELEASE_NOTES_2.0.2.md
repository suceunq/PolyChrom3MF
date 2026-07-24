# PolyChrom 3MF 2.0.2

## Nouveautés

- Plusieurs motifs PNG/JPG indépendants peuvent coexister dans un même modèle.
- Chaque motif peut cibler un objet différent ou toute la figurine.
- Le calque actif permet de transformer ou retirer uniquement le motif choisi.
- Les projets `.poly3mf` conservent toutes les images et leurs réglages séparément.
- L’objet sélectionné est clairement indiqué et surligné dans la vue 3D.
- Le prochain import de motif propose automatiquement l’objet sélectionné comme cible.

## Corrections

- Les indices réels des objets sont désormais utilisés pour leur sélection et leur couleur.
- Le workflow GitHub Actions construit l’installateur depuis un chemin stable et indépendant du numéro de version.

## Validation

- 85 tests automatisés réussis.
- Compilation Release sans erreur ni avertissement.
- Aucun paquet NuGet vulnérable détecté.
- Publication autonome Windows et compilation Inno Setup vérifiées localement.
