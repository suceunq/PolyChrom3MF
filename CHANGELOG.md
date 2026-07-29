# Changelog

## 2.1.1

- Aperçu fidèle du fichier transparent directement dans le cadre de placement 3D.
- Conservation automatique du rapport largeur/hauteur d’origine des logos.
- Correction de l’échantillonnage qui colorait auparavant tout un triangle lorsqu’un seul point touchait le motif.
- Couverture moyenne des faces pour conserver des contours propres et éviter les bavures.
- Subdivision locale adaptative jusqu’à six niveaux sous l’empreinte complète du logo, y compris autour des zones transparentes.
- Préservation des lettres, trous et traits fins dans le modèle coloré et dans l’export 3MF.
- Suppression du recalcul trompeur et saccadé du maillage pendant le déplacement : le fond reste visuellement stable.
- Budget de subdivision adapté à la taille du modèle pour préserver les performances.
- 126 tests automatisés réussis, dont de nouveaux tests de non-régression sur les motifs fins et les triangles partiellement couverts.

## 2.1.0

- Reconstruction complète du module d’images et de logos dans une bibliothèque isolée et testable.
- Import PNG, JPG, JPEG, WebP et SVG avec transparence conservée et SVG nettoyé des contenus externes ou exécutables.
- Détourage automatique du fond relié aux bords, suppression de la couleur dominante, tolérance, gomme et restauration.
- Placement ancré sur le maillage avec projections plane, cylindrique et adaptée à la courbure.
- Transformations non destructives : déplacement, taille libre, rotation, inclinaison, miroir, relief et répétitions espacées.
- Instances indépendantes, modifiables, duplicables, masquables et supprimables sans modifier la coloration de fond.
- Subdivision locale uniquement sous les logos lorsqu’elle apporte un gain de précision.
- Sauvegarde des images, calques, ancrages et transformations dans les projets `.poly3mf`.
- Export 3MF en flux continu avec couleurs standards et métadonnées de peinture des principaux slicers.
- Validation réelle sur 2 983 116 et 9 585 474 triangles ; export du modèle extrême ramené à environ 33 secondes avec un pic d’environ 2 Go pendant l’export.
- 124 tests automatisés réussis, incluant formats, sécurité SVG, images corrompues, annulation, surfaces courbes, mémoire, projets et export.

## 2.0.15

- Retrait complet des accès utilisateur au module expérimental d’importation et de placement d’images/logos.
- Suppression des boutons, menus, transformations, édition de calques image et aide associée.
- Les anciens projets restent lisibles et conservent leurs affectations de couleurs déjà enregistrées.
- Les styles contenant un ancien motif appliquent uniquement leur palette dans cette version stable.
- Conservation des fonctions stables : import 3MF/STL, palettes, peinture, calques, profils d’impression, projets et exports slicers.
- Préparation d’une reconstruction isolée du module avant toute future réintégration.

## 2.0.13

- Nouvel assistant d’export basé sur les profils réellement installés de Snapmaker Orca, OrcaSlicer, Bambu Studio et PrusaSlicer.
- Export de l’imprimante, du processus, de la buse, des emplacements, des profils de filament, des matériaux et des couleurs.
- Suppression complète des anciennes métadonnées machine du fichier source pour éviter qu’un projet Snapmaker reste identifié comme une Bambu Lab X1 Carbon.
- Profils d’export mémorisables, réutilisables et configurables comme valeur par défaut.
- Recherche instantanée des filaments avec prise en charge des espaces, tri des profils Generic et suppression des doublons.
- Nom de fichier transmis au slicer nettoyé et complété avec l’imprimante sélectionnée.
- Validation structurelle des métadonnées générées et configuration native supplémentaire pour PrusaSlicer.

## 2.0.12

- Version de mise à jour publique de l’atelier motif intégré et des optimisations de fluidité.
- Numéro distinct de la préversion locale 2.0.11 afin de garantir la détection automatique de la mise à jour.

## 2.0.11

- Nouvel atelier de placement intégré à la fenêtre principale pour les motifs PNG/JPEG.
- L’interface normale est remplacée temporairement par la vue 3D et les réglages du motif.
- Navigation inchangée : clic gauche pour tourner, clic droit pour déplacer et molette pour zoomer.
- Aperçu asynchrone adaptatif et rendu LOD pour conserver une interface fluide sur les gros modèles.
- Reconstruction automatique du cache LOD après subdivision ou application définitive d’un motif.
- Caméra réutilisée pendant la rotation afin de supprimer les allocations à chaque mouvement de souris.
- Le moteur GPU masqué ne reçoit plus de mises à jour inutiles pendant le rendu compatible.
- Bouton « Appliquer le motif » fixé et toujours visible.
- Verrouillage des commandes incompatibles pendant l’aperçu pour protéger le projet.

## 2.0.10

- Bouton « Appliquer les filaments » fixé en bas de la fenêtre et toujours accessible.
- Fusion périodique des bords pour supprimer la couture blanche des projections répétées et cylindriques.
- Préservation des traits des motifs monochromes lors des transitions triplanaires.
- Test de non-régression dédié aux deux côtés de la couture cylindrique.

## 2.0.9

- Répétition miroir continue des images pour éviter les lignes de raccord sur le modèle.
- Projection triplanaire adoucie par mélange des axes et échantillonnage bilinéaire.
- Gestion groupée de 2 à 32 couleurs de filament dans une seule fenêtre.
- Choix PLA ou PETG pour chaque couleur et présélection du matériau dans les slicers compatibles.

## 2.0.2

- Plusieurs motifs PNG/JPG indépendants peuvent désormais coexister dans un même projet, chacun ciblant un objet différent ou toute la figurine.
- Le calque actif permet de transformer ou retirer uniquement le motif choisi sans effacer les autres.
- Les projets `.poly3mf` embarquent et restaurent séparément toutes les images de motifs.
- L’objet sélectionné est clairement indiqué et surligné dans la vue 3D ; il devient la cible proposée par défaut lors du prochain import de motif.
- Le workflow GitHub Actions utilise maintenant un chemin de publication stable, indépendant du numéro de version.

## 2.0.1

- Subdivision locale adaptative non destructive sous les motifs, logos, textes et zones peintes.
- Système de calques complet avec ordre, visibilité, verrouillage, duplication, renommage, fusion, texte et effets.
- Sélection intelligente par îlot, angle, couleur, caméra, pièce, rectangle, lasso et détection sémantique assistée.
- Assistant multicolore avec détection des profils imprimante/filaments et estimation des couches et changements.
- Aperçu 3D solide, chargement en arrière-plan, LOD automatique et mémoire fortement réduite sur les très gros fichiers.
- Galerie de styles partageables `.polystyle` et mode débutant.
- Transformation directe des motifs, redimensionnement libre, copies espacées et miroirs.
- Recentrage automatique après import, coloration et application d’un motif ou logo.
- Suppression du rendu pointillé/translucide sur les modèles de plus de 1,2 million de triangles.

## 1.7.4

- Nouvelle option activée par défaut pour répéter le motif et couvrir toute la pièce, même sur les figurines composées de nombreuses parties.
- Modification directe de chaque couleur d’une proposition par clic droit sur sa pastille.
- Nombre de couleurs désormais réglable de 2 à 32 dans les propositions, les projets et l’export 3MF.

## 1.7.3

- Nouveau mode « Logo monochrome » pour conserver uniquement la forme d’un symbole noir ou blanc et laisser le reste du modèle inchangé.
- Choix direct de la couleur de filament du logo, inversion clair/sombre et seuil de détection réglable avec aperçu en direct.
- Suppression des petites taches multicolores produites par la conversion des contours anticrénelés d’un logo.
- Aperçu adaptatif cohérent pour les maillages de plusieurs millions de faces, avec conservation des indices source pour la sélection et l’export.
- Import STL et affichage des modèles très denses accélérés, avec une consommation mémoire fortement réduite.
- Calcul des motifs, des limites du modèle et du plateau optimisé pour les très grandes figurines.

## 1.7.2

- Aperçu en direct du motif sur la figurine pendant le réglage de la projection, de la taille, de la rotation et des décalages.
- Annulation automatique des anciens calculs d’aperçu pour conserver des curseurs réactifs sur les modèles complexes.
- Restauration exacte de la coloration précédente lorsque la fenêtre du motif est annulée.
- Nouveau pinceau fluide avec tracé instantané et calcul géométrique différé au relâchement.
- Sélection limitée à la surface visible afin de ne pas peindre l’arrière de la figurine.
- Correction de l’erreur WPF inter-thread lors de l’application du pinceau.
- Amélioration de l’assemblage, du recentrage et de la rotation à 360° des modèles 3MF complexes.

## 1.7.1

- Nouvelle fenêtre de bienvenue avec présentation claire des fonctions de PolyChrom 3MF.
- Ajout d’un encart facultatif pour soutenir le développement via PayPal.
- Accès permanent au soutien depuis le menu Aide, même lorsque la bienvenue est masquée au démarrage.
- Validation stricte de l’adresse : seules les pages de don HTTPS officielles `paypal.com` et `paypal.me` peuvent être ouvertes.

## 1.7.0

- Import d'un PNG avec transparence, taille, rotation, décalage et ciblage d'un objet ou de toute la figurine.
- Quatre projections imprimables : frontale, cylindrique, répétée et triplanaire, réduites vers la palette de 4 à 32 filaments.
- Intégration du PNG et de ses réglages dans les projets portables `.poly3mf`.
- Coloration manuelle : sélection cumulative de petites, moyennes ou grandes zones puis application d'une couleur de la palette.
- Nouveau pinceau « Une seule face » et rayons fortement réduits pour les zones très petites et petites.
- Surlignage des triangles sélectionnés et restauration par annuler/rétablir.
- Correction du chargement direct d'un modèle ou projet fourni sur la ligne de commande.
- Durcissement du décodage PNG, des projets partagés et du téléchargement des mises à jour face aux fichiers malveillants ou anormalement volumineux.
- Réinitialisation sûre de l’historique et des sélections lors d’un changement de modèle.

## 1.6.11

- Durcissement de la lecture des projets partagés face aux réglages absents, aux affectations invalides et aux fichiers inattendus dans l'archive.
- Ouverture directe d'un projet `.poly3mf` sans redemander le nombre de couleurs afin de restaurer immédiatement le partage à l'identique.
- Texte de secours garanti lorsque GitHub ne fournit pas de résumé exploitable pour une mise à jour.
- Mise à niveau complète de la chaîne de tests afin de supprimer deux dépendances transitives signalées comme vulnérables.

## 1.6.10

- Suppression de l'effet transparent sur les modèles dépassant 500 000 triangles : aucune face n'est désormais retirée de l'aperçu.
- Construction du rendu multicolore en un seul parcours des triangles et gel des ressources WPF pour conserver de bonnes performances.
- Correction du bouton « Fermer » coupé dans la fenêtre « À propos » avec hauteur automatique et défilement de secours.
- Les projets `.poly3mf` sont désormais entièrement portables : ils embarquent le modèle 3MF/STL, les quatre propositions, les palettes, les motifs et la vue.
- Ouverture d'un projet partagé par double-clic, par glisser-déposer ou depuis le menu Fichier ; les anciens projets restent compatibles.
- La proposition de mise à jour affiche maintenant un résumé des changements avant le téléchargement.

## 1.6.9

- Prise en charge des grands fragments XML 3MF dépassant 100 millions de caractères.
- Limite XML ajustée à la taille réelle de chaque fragment tout en conservant le plafond global anti-décompression abusive.
- Compatibilité validée avec `samraii-frogggg.3mf` sans modifier le fichier original.

## 1.6.8

- Correction du numéro de version intégré aux métadonnées de l'installateur Windows.
- Publication consolidée du nouveau système de mise à jour automatique.

## 1.6.7

- Recherche automatique des mises à jour au démarrage.
- Demande de confirmation avant téléchargement et installation.
- Téléchargement en arrière-plan avec barre de progression et pourcentage.
- Vérification de la taille et de l'empreinte SHA-256 publiée par GitHub.
- Installation silencieuse, fermeture propre et redémarrage automatique de PolyChrom 3MF.

## 1.6.6

- Correction de la fenêtre de sélection du nombre de couleurs dont le bouton inférieur pouvait être coupé.
- Hauteur calculée automatiquement selon le contenu et la mise à l'échelle Windows.
- Ajout d'un défilement vertical de secours pour les petits écrans.

## 1.6.5

- Activation de la recherche de mises à jour depuis les Releases GitHub officielles.
- Validation HTTPS et contrôle du numéro de version avant de proposer un téléchargement.
- Ajout d'une automatisation GitHub Actions pour compiler et publier les futurs installateurs versionnés.

## 1.6.4

- Conservation des affectations triangle par triangle dans les projets `.poly3mf`.
- Export atomique : le fichier final n'est remplacé qu'après écriture et validation réussies.
- Possibilité d'exporter sur le fichier 3MF source sans erreur ni corruption.
- Nom d'export adapté au nombre réel de couleurs choisi.
- Assainissement des paramètres locaux et validation renforcée des projets corrompus.
- Confirmation avant de remplacer un travail modifié lors d'un import, d'un glisser-déposer ou de l'ouverture d'un projet.
- Messages d'état corrigés lorsque la vérification après export est désactivée.

## 1.6.3

- Détection de Snapmaker Orca, y compris le chemin officiel `Snapmaker_Orca` et l'exécutable `snapmaker-orca.exe`.
- Affichage de la version installée des slicers lorsqu'elle est disponible.
- Reconnaissance améliorée des noms de logiciels contenant des espaces, tirets ou traits de soulignement dans le registre Windows.

## 1.6.2

- Nouvelle fenêtre « À propos ».
- Ajout de la mention « Sur une idée de 3D TER ».
- Ajout d’un lien TikTok cliquable vers le compte officiel fourni.

## 1.6.1

- Barre de menus et menus déroulants remis au style Windows standard.
- Suppression des encadrements personnalisés autour de chaque commande de menu.
- Couleurs, survol, séparateurs et raccourcis gérés par le thème système.

## 1.6.0

- Demande du nombre de couleurs au démarrage.
- Choix de 4 à 32 couleurs, avec raccourcis 4, 6, 8, 12, 16, 24 et 32.
- Modification possible à tout moment depuis la barre d’outils.
- Palettes étendues et motifs procéduraux généralisés au nombre choisi.
- Nombre de couleurs conservé dans les paramètres, les projets et annuler/rétablir.
- Export 3MF multicolore vérifié avec huit couleurs réelles.

## 1.5.0

- Détection automatique des slicers installés via le registre, les App Paths, le PATH et les dossiers habituels.
- Prise en charge de PrusaSlicer, OrcaSlicer, Bambu Studio, Cura, SuperSlicer, ideaMaker, Creality Print, Anycubic Slicer, QIDI Studio, Simplify3D et FlashPrint.
- Sélection du slicer préféré dans les paramètres, avec redétection et sélection manuelle possible.
- Bouton direct « Ouvrir dans [slicer préféré] » dans la barre d’outils.
- Proposition d’ouverture dans le slicer choisi après chaque export.

## 1.4.1

- Infobulles de couleur sombres et lisibles dans les thèmes clair et sombre.
- Nom de chaque couleur affiché directement sous son échantillon, avec son code hexadécimal.

## 1.4.0

- Nouveau mode « Motifs fun » activé par défaut.
- Quatre rendus procéduraux : aurore ondulée, camouflage organique, double personnalité et graffiti pop.
- Régénération de nouvelles variantes sans modifier le maillage.
- Conservation du mode fun dans les projets et dans annuler/rétablir.
- Export fun validé sur les 500 000 triangles d’Eniac dans PrusaSlicer.
- 28 tests automatisés réussis.

## 1.3.0

- Exactement quatre couleurs dans chacune des quatre propositions.
- Coloration par triangles pour les modèles constitués d’un seul objet.
- Quatre répartitions : horizontale, angulaire, verticale et mosaïque.
- Régénération de nouvelles palettes et nouvelles répartitions à chaque clic.
- Export des propriétés de couleur par triangle dans le 3MF standard.

## 1.2.0

- Lecture des maillages répartis dans plusieurs fragments 3MF Production.
- Correction de l’import du fichier Eniac de 500 000 triangles.
- Import STL ASCII et binaire, avec export en 3MF coloré.
- Aperçu allégé uniquement au-delà de 500 000 triangles.
- Tests de conversion et validation réelle dans PrusaSlicer 2.9.6.

## 1.1.1

- Refonte complète des couleurs de contrôles pour garantir la lisibilité.
- Thèmes clair, sombre et système harmonisés.
- Contrastes vérifiés visuellement sur un modèle 3MF chargé.

## 1.1.0

- Import/export sécurisé et validation renforcée.
- Édition HEX, filaments, annuler/rétablir et projets.
- Caméra complète, thèmes, assistant illustré et identité visuelle.
- Validation PrusaSlicer et installateur utilisateur sans élévation.
