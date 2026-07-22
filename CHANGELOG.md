# Changelog

## 1.6.10

- Suppression de l'effet transparent sur les modèles dépassant 500 000 triangles : aucune face n'est désormais retirée de l'aperçu.
- Construction du rendu multicolore en un seul parcours des triangles et gel des ressources WPF pour conserver de bonnes performances.
- Correction du bouton « Fermer » coupé dans la fenêtre « À propos » avec hauteur automatique et défilement de secours.

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
