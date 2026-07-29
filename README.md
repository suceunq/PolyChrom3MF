# PolyChrom 3MF 2.1.1

Application Windows française et locale pour colorer, personnaliser et préparer des fichiers 3MF ou STL multicolores. PolyChrom 2 introduit la subdivision locale non destructive, les calques, la sélection intelligente, l’assistant d’impression et un aperçu solide optimisé pour les très gros modèles.

## Télécharger

- [Dernière version stable et installateur Windows](https://github.com/suceunq/PolyChrom3MF/releases/latest)
- [Code source et suivi du projet](https://github.com/suceunq/PolyChrom3MF)

## Fonctions

- Import 3MF et STL (ASCII ou binaire) par boîte de dialogue ou glisser-déposer.
- Coloration manuelle cumulative d’une face précise ou de zones de plusieurs tailles avant application de la couleur choisie.
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
- Choix de 2 à 32 couleurs au démarrage, modifiable ensuite depuis la barre d’outils.
- Palettes et motifs réellement étendus au nombre choisi, avec export de tous les matériaux 3MF.
- Menus Fichier, Édition, Affichage et Aide au style Windows standard.
- Détection automatique des nouvelles versions au démarrage, téléchargement vérifié avec barre de progression, installation silencieuse et redémarrage automatique.
- Fenêtre « À propos » créditant 3D TER avec un lien TikTok cliquable.
- Fenêtre de bienvenue facultative et lien de soutien PayPal sécurisé accessible depuis le menu Aide.
- Annuler/rétablir et projets portables `.poly3mf` réunissant dans un fichier partageable le modèle 3D, les propositions, les palettes et la vue exacte.
- Ouverture des projets `.poly3mf` par double-clic, glisser-déposer ou menu Fichier, avec compatibilité des anciens projets.
- Export 3MF standard `basematerials`, géométrie source préservée et validation par réouverture.
- Calques non destructifs : visibilité, verrouillage, ordre, renommage, duplication, fusion et effets.
- Sélection par îlot, angle, couleur, caméra, pièce, rectangle, lasso et régions sémantiques assistées.
- Aperçu WPF solide avec cache, LOD automatique et maillage allégé pour les très gros modèles.
- Recentrage automatique après import et coloration.
- Assistant d’export lisant les profils réellement installés de Snapmaker Orca, OrcaSlicer, Bambu Studio et PrusaSlicer : machine, processus, buse, nombre d’emplacements, matériaux, couleurs et filaments sont mémorisables dans plusieurs profils réutilisables.
- Les métadonnées privées d’un ancien slicer sont remplacées à l’export afin qu’un projet Bambu, par exemple, puisse être rouvert avec le profil Snapmaker choisi sans conserver par erreur la X1 Carbon d’origine.
- Galerie portable `.polystyle` et mode débutant.
- Module de logos reconstruit et isolé : import PNG, JPG, JPEG, WebP et SVG, transparence conservée, suppression du fond relié aux bords ou de la couleur dominante, gomme et restauration manuelles.
- Placement non destructif sur une surface plane, cylindrique ou courbe avec aperçu, taille libre, rotation, inclinaison, miroirs, relief local et duplication espacée horizontale, verticale ou circulaire.
- Chaque occurrence reste indépendante dans le panneau **Logos et images** : modification, duplication, visibilité et suppression n’altèrent jamais les couleurs de fond.
- Les projets `.poly3mf` embarquent les images normalisées, les calques, les ancrages et les transformations afin de restituer le même résultat sur un autre PC.
- Export 3MF en flux continu et validation géométrique sans seconde copie du maillage en mémoire, vérifiés sur des modèles de 2,98 et 9,58 millions de triangles.

## Compilation

```powershell
dotnet build PolyChrom3MF.sln -c Release
dotnet test PolyChrom3MF.sln -c Release
dotnet publish PolyChrom3MF.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
ISCC.exe installer.iss
```

Les livrables sont dans `LIVRAISON_FINALE`. PrusaSlicer 2.9.6 a validé l’export fun d’Eniac : 1 objet, 500 000 triangles, géométrie manifold et dimensions conservées.

## Remarque STL

STL ne stocke ni unité, ni objets, ni matériaux : ses coordonnées sont interprétées en millimètres et il est importé comme un objet. Les extensions 3MF privées sont préservées.
