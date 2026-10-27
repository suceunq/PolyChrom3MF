# PolyChrom 3MF 2.1.5 — chemin de mise à jour renouvelé

## Important : installation manuelle unique

Si vous utilisez la version **2.1.3 ou 2.1.4**, téléchargez et installez cette version **une seule fois** à la main (`PolyChrom3MF_Setup_x64.exe`, ci-dessous). Vos réglages et projets sont conservés.

Les versions précédentes ne peuvent pas recevoir cette mise à jour automatiquement : leur clé de signature des mises à jour n'est plus exploitable. À partir de la 2.1.5, chaque nouvelle version est annoncée par un manifeste signé avec une nouvelle clé et s'installe automatiquement depuis l'application.

## Mises à jour

- Nouvelle clé de signature des mises à jour intégrée à l'application.
- Le manifeste (`update.json`) est désormais signé et publié automatiquement à chaque version par le workflow GitHub.
- L'application refuse toujours tout manifeste ou installateur dont la signature, la taille ou l'empreinte SHA-256 ne correspondent pas.

## Interface

- Palette plus profonde, boutons, champs et listes redessinés (survol, focus clavier visible).
- Barres de défilement fines, info-bulles arrondies, barre de titre sombre sous Windows.
- Fenêtres qui s'ouvrent en fondu.

## Validation

- 134 tests automatisés réussis en configuration Release.
