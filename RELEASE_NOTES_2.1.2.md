# PolyChrom 3MF 2.1.2 — audit de stabilité et de sécurité

Cette mise à jour renforce la stabilité, la sécurité et la fiabilité du système de mise à jour et de traitement des fichiers sans modifier l’interface ni le comportement de coloration et d’export.

## Améliorations de sécurité et mise à jour

- Les mises à jour sont désormais décrites par un manifeste signé indépendamment de GitHub (ECDSA P-256 + SHA-256 sur payload canonique).
- Le téléchargement contrôle strictement : provenance HTTPS (github.com / release-assets), taille exacte, format PE Windows (MZ), empreinte SHA-256 du manifeste.
- Rollback automatique : une copie de récupération de la version précédente est conservée avant installation.
- Validation renforcée des réponses réseau et redirections.
- Les projets (.poly3mf), styles et archives 3MF bénéficient de validations renforcées contre les contenus malveillants (limites d’entrées ZIP, chemins sûrs sans .. /, tailles, signatures, etc.).
- Aucune modification inutile de l’interface ou du fonctionnement de coloration n’a été introduite.

## Stabilité et qualité

- Gestion d’erreurs améliorée dans les services critiques (3MF, projet, mise à jour).
- Limites de ressources pour prévenir les attaques par déni de service ou consommation excessive.
- Tests automatisés passent en Release.
- Compilation propre.

## Validation

- Compilation Windows Release sans erreur.
- Tests unitaires réussis.
- Vérifications manuelles des flux de mise à jour, import/export, projets portables.

Pour les notes complètes de la version 2.1.1 (fidélité logos), voir RELEASE_NOTES_2.1.1.md .
