# Sécurité

Les archives 3MF et les projets `.poly3mf` sont traités comme non fiables : extensions et chemins contrôlés, limites de taille et de nombre d’entrées, rejet des chemins `..`/absolus, XML sans DTD ni résolveur externe et extraction atomique dans un dossier local dédié.

Les PNG sont limités à 32 Mo et 16 mégapixels, puis décodés seulement après contrôle de leurs dimensions. Les réglages, palettes, affectations et textes intégrés aux projets partagés sont validés avant utilisation.

Le seul accès réseau automatique concerne la recherche de versions officielles sur GitHub. Une mise à jour n’est installée qu’après validation HTTPS, contrôle de la taille annoncée et comparaison SHA-256 en temps constant. Le bouton de soutien ouvre uniquement, après une action volontaire, une page de don HTTPS officielle `paypal.com` ou `paypal.me`. Aucune télémétrie ni aucun secret n’est stocké ou envoyé.
