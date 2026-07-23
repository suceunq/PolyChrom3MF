# Rapport de sécurité

Audit de code actualisé pour la version 1.7.1 : chemins ZIP traversants et absolus refusés, taille et nombre d’entrées limités, DTD et résolveurs XML externes désactivés, projets portables et réglages PNG validés, décodage d’image plafonné à 16 mégapixels et téléchargement des mises à jour interrompu dès que la taille annoncée est dépassée. Le lien de soutien est limité aux pages de don HTTPS officielles PayPal et ne s’ouvre qu’à la demande de l’utilisateur.

`dotnet list package --vulnerable --include-transitive` ne signale aucun paquet NuGet vulnérable connu. Aucun secret ni aucune télémétrie n’est présent. Le réseau automatique sert uniquement à interroger et télécharger les Releases GitHub officielles en HTTPS, avec contrôle de taille et SHA-256 ; PayPal ne s’ouvre qu’après un clic explicite.

Risque résiduel : le parseur 3MF simplifié ne couvre pas toutes les extensions propriétaires et une empreinte publiée sur le même compte GitHub que l’installateur ne remplace pas une signature Authenticode indépendante.
