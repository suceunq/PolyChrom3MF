# SignUpdateManifest

Génère et signe `latest.json`, le manifeste que PolyChrom 3MF télécharge pour proposer une mise à jour.
L'application refuse tout manifeste dont la signature ECDSA P-256 / SHA-256 ne correspond pas à sa clé publique intégrée
(`UpdateService.cs`) : sans cette étape, les utilisateurs déjà installés ne voient pas la nouvelle version.

## Publier une mise à jour

1. Pousser le tag `vX.Y.Z` : le workflow GitHub compile l'installateur et crée la Release.
2. Une fois la Release publiée, signer le manifeste avec la clé privée (jamais commitée) :

```powershell
dotnet run --project tools/SignUpdateManifest -- --version X.Y.Z --key C:\chemin\cle-privee.pem
```

3. Commiter le `latest.json` obtenu sur la branche `main` : les utilisateurs reçoivent la mise à jour au prochain contrôle.

L'outil télécharge l'installateur de la Release pour calculer sa taille et son empreinte SHA-256, et il s'arrête si la clé
fournie ne correspond pas à la clé publique de l'application. Les notes sont lues dans `RELEASE_NOTES_X.Y.Z.md`.
