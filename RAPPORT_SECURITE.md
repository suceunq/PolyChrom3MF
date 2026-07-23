# Rapport de sécurité

Audit de code effectué pour la version 1.7.0 : chemins ZIP traversants et absolus refusés, taille et nombre d’entrées limités, DTD et résolveurs XML externes désactivés, projets portables et réglages PNG validés, décodage d’image plafonné à 16 mégapixels et téléchargement des mises à jour interrompu dès que la taille annoncée est dépassée.

`dotnet list package --vulnerable --include-transitive` ne signale aucun paquet NuGet vulnérable connu. Aucun secret ni aucune télémétrie n’est présent. Le réseau sert uniquement à interroger et télécharger les Releases GitHub officielles en HTTPS, avec contrôle de taille et SHA-256.

Risque résiduel : le parseur 3MF simplifié ne couvre pas toutes les extensions propriétaires et une empreinte publiée sur le même compte GitHub que l’installateur ne remplace pas une signature Authenticode indépendante.
