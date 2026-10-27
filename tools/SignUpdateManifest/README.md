# SignUpdateManifest

Génère et signe `update.json`, le manifeste que PolyChrom 3MF télécharge pour proposer une mise à jour.
L'application refuse tout manifeste dont la signature ECDSA P-256 / SHA-256 ne correspond pas à sa clé publique intégrée
(`UpdateService.cs`) : sans cette étape, les utilisateurs déjà installés ne voient pas la nouvelle version.

> `latest.json` est l'ancien manifeste (versions ≤ 2.1.4, ancienne clé perdue). Il reste figé sur la 2.1.3 pour ne pas
> provoquer d'erreur chez ces utilisateurs, qui doivent installer la 2.1.5 une fois à la main.

## Publication automatique (cas normal)

Le workflow `.github/workflows/release.yml` fait tout lors d'un `git tag vX.Y.Z` suivi d'un `git push origin vX.Y.Z` :
tests, installateur, Release GitHub, puis signature et publication de `update.json` sur `main` avec la clé stockée dans le
secret GitHub `UPDATE_SIGNING_KEY`. Il suffit d'avoir créé `RELEASE_NOTES_X.Y.Z.md`.

## Signature manuelle (secours)

```powershell
dotnet run --project tools/SignUpdateManifest -- --version X.Y.Z --key C:\chemin\update-signing-private.pem
```

puis commiter `update.json` sur `main`. L'outil télécharge l'installateur de la Release pour calculer sa taille et son
empreinte SHA-256, et il s'arrête si la clé fournie ne correspond pas à la clé publique de l'application.

## Clé de signature

La clé privée (PEM, PKCS#8, ECDSA P-256) n'est **jamais** dans le dépôt. Elle est conservée dans le secret GitHub
`UPDATE_SIGNING_KEY` et dans une sauvegarde privée du propriétaire. Perdre toutes les copies oblige à refaire une rotation
de clé (nouvelle clé publique dans une nouvelle version à installer une fois à la main).
