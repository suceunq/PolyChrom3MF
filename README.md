# PolyChrom 3MF 1.6.5

Application Windows française et locale pour analyser, visualiser et colorer des fichiers 3MF ou STL sans modifier leur géométrie. Au démarrage, l’utilisateur choisit de 4 à 32 couleurs ; l’application génère quatre propositions, permet les ajustements manuels et contrôle automatiquement chaque export.

## Fonctions

- Import 3MF et STL (ASCII ou binaire) par boîte de dialogue ou glisser-déposer.
- Prise en charge des conteneurs 3MF multiparties utilisant l’extension Production.
- Analyse sécurisée ZIP/XML des objets, composants, unités et matériaux.
- Visualiseur WPF 3D : rotation, déplacement, zoom, sélection, vues normalisées, perspective et plateau.
- Quatre propositions utilisant chacune exactement quatre couleurs, réparties sur les triangles même pour un objet fusionné.
- Mode **Motifs fun** activé par défaut : aurore ondulée, camouflage organique, double personnalité et graffiti pop.
- Retour possible aux quatre styles classiques grâce à la case du panneau droit.
- Régénération illimitée de quatre nouveaux styles et palettes.
- Couleurs de filament locales, saisie HEX et sélecteur Windows.
- Nom et code hexadécimal visibles sous chaque couleur, avec infobulle sombre au survol.
- Détection automatique des slicers installés, dont Snapmaker Orca, affichage de leur version et bouton direct d’ouverture dans le slicer préféré.
- Choix de 4 à 32 couleurs au démarrage, modifiable ensuite depuis la barre d’outils.
- Palettes et motifs réellement étendus au nombre choisi, avec export de tous les matériaux 3MF.
- Menus Fichier, Édition, Affichage et Aide au style Windows standard.
- Recherche des nouvelles versions depuis les Releases GitHub officielles, accessible dans Aide > Rechercher une mise à jour.
- Fenêtre « À propos » créditant 3D TER avec un lien TikTok cliquable.
- Annuler/rétablir, projets `.poly3mf`, thèmes clair/sombre et assistant de bienvenue.
- Export 3MF standard `basematerials`, géométrie source préservée et validation par réouverture.

## Compilation

```powershell
dotnet build PolyChrom3MF.sln -c Release
dotnet test PolyChrom3MF.sln -c Release
dotnet publish PolyChrom3MF.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
ISCC.exe installer.iss
```

Les livrables sont dans `LIVRAISON_FINALE`. PrusaSlicer 2.9.6 a validé l’export fun d’Eniac : 1 objet, 500 000 triangles, géométrie manifold et dimensions conservées.

## Limites

Les motifs fun sont géométriques et procéduraux : ils décorent même une pièce fusionnée, mais ne reconnaissent pas sémantiquement les yeux ou les vêtements. STL ne stocke ni unité, ni objets, ni matériaux : ses coordonnées sont interprétées en millimètres et il est importé comme un objet. Les extensions 3MF privées sont préservées.
