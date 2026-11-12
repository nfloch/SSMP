# Suivi : build local SSMP world-sync (Papaours & Jojo)

Ce fork part de la branche `world-sync` d'Extremelyd1/SSMP (commit `4d7ca26`, PR #99 en amont).
On y ajoute nos correctifs au fil des parties tests.

## Compiler et installer

Il faut le SDK .NET 10, installé dans `~/.dotnet`, pour le mot-clé `field` de C# 14.

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PATH=$HOME/.dotnet:$PATH dotnet build SSMP/SSMP.csproj -c Release
```

- Sortie de la compilation : `SSMP/bin/Release/netstandard2.1/`.
- Installation chez Papaours : copier `SSMP.dll` et `BouncyCastle.Cryptography.dll` dans
  `%APPDATA%\Cogfly\profiles\SSMP\BepInEx\plugins\SSMP-SSMP\`.
  - Vérifier d'abord que le jeu est fermé : `tasklist.exe | grep -i silksong`.
- Installation chez Jojo : les mêmes fichiers, dans le profil Cogfly « Alliance ».
  - Ils sont aussi zippés dans `~/claude-projects/ssmp-world-sync-pour-jojo.zip`.
- Les deux joueurs doivent avoir exactement le même `SSMP.dll`.
- Build auto à chaque push (workflow `Build DLL`, runner `ssmp-runner` du cluster) : le zip de la branche est
  sur `https://github.com/nfloch/SSMP/releases/download/latest-<branche>/SSMP-<branche>.zip`,
  par exemple [world-sync](https://github.com/nfloch/SSMP/releases/download/latest-world-sync/SSMP-world-sync.zip).
  `COMMIT.txt` dans le zip donne le commit compilé.

## Logs

- Papaours : `%APPDATA%\Cogfly\profiles\SSMP\BepInEx\LogOutput.log`.
- Jojo : il envoie son `LogOutput.log`, que je dépose dans `Téléchargements`.

## Sauvegardes

- SSMP 0.3.1 et l'ancien addon SSMPEnemySync :
  - `~/claude-projects/backup-SSMP-0.3.1-20261112-0336` ;
  - la même copie dans `Cogfly\profiles\SSMP\backup-SSMP-0.3.1`.
- Sauvegardes du jeu : `~/claude-projects/backup-saves-20261112-0336`.

## Correctifs locaux (commités)

1. **`ModSettings.FullSynchronisation` forcé à `true`.** Aucun menu ne permet de l'activer, et la synchro des
   ennemis et du monde en dépend.
2. **`EntityManager` : try/catch autour de chaque entité** dans les boucles `InitializeHost`, `InitializeClient` et
   `MakeHost`. Une entité qui plante ne bloque plus les autres.
3. **Salle vide (Tut_01b)** : `OnSceneLoaded` pré-capture toute salle dont le nom commence par celui de la salle
   active (Tut_01 → Tut_01b). Or le constructeur d'`Entity` fait `Host.SetActive(false)`, et `ClearEntities` ne
   restaurait rien.
   - Correctif : `Entity.RestoreForSceneChange`, appelé dans `EntityManager.OnSceneChanged` avant `ClearEntities()`.
   - Log attendu : `Restored N pre-loaded entities entering 'Tut_01b'`.
4. **Animation de stun absente côté client** : le FSM modèle « Stun Control » est reconstruit au réveil, et les
   instances d'actions changent.
   - Correctif : `Entity.TryRebindAction` retrouve l'action par ses indices (FSM, état, action).
   - Log attendu : `re-bound rebuilt FSM action`.

Aucun de ces correctifs n'a encore été vérifié en jeu.

## Bugs ouverts

### Mur de lianes « One Way VineWall » (Tut_02) incassable

- Ni l'hôte ni le client ne peuvent le casser. Les deux ont tapé du bon côté, et aucun effet de coup ne s'affiche.
- Ce mur n'est pas une entité SSMP. `SaveManager` le trouve comme persistent bool : dans `save-data.json`, il est
  marqué `Sync: true`, `SyncType: Server`, `IgnoreSceneHost: false`.
- Les logs ne montrent aucune erreur et aucune mise à jour de sauvegarde pour ce mur.
- Sous SSMP 0.3.1 + addon, ce problème n'existait pas : il vient donc probablement de world-sync.
- `Breakable.Hit` (code du jeu) renvoie `None`, sans effet visible, dans ces cas :
  - `ignoreDamagers` ;
  - `breakableRange` (`TrackTriggerObjects`) actif mais `!IsInside` : c'est le mécanisme du sens unique ;
  - `isBroken` ;
  - un élément de `requireBroken` n'est pas cassé.
- Pistes :
  - a) `SaveManager` règle `breakable.isBroken = value` directement, sans `SetAlreadyBroken()`. Si le serveur
    renvoie `true`, le mur reste visuellement intact mais ignore les coups.
  - b) `breakableRange` ne voit pas le héros.
  - c) Le mur est piloté par un FSM : `GetHero` ou un hook world-sync (TinkEffect IL hook, EnemyTargeting) le
    perturbe.
- Prochaine étape : lire la structure de Tut_02 dans
  `Hollow Knight Silksong_Data/StreamingAssets/aa/StandaloneWindows64/scenes_scenes_scenes/tut_02.bundle`
  (UnityPy, à installer : `python3 -m venv` a échoué faute d'ensurepip, essayer `uv` ou `apt install python3-venv`).
  Sinon, ajouter un log de diagnostic sur `Breakable.Hit` pour ce mur : `isBroken`, `breakableRange.IsInside`,
  `requireBroken`.

### À vérifier au prochain test

- Focus des ennemis sur les joueurs autres que l'hôte : world-sync a du code pour ça (`GamePatcher.EnemyTargeting`,
  `PlayerTargetRegistry`), mais il n'écrit rien dans les logs. À vérifier visuellement.
- Les correctifs 3 et 4 ci-dessus.

## Outils

- Jeu décompilé : `~/claude-projects/ssmp-build/game-src`, produit avec `ilspycmd`.
  - Lancer `ilspycmd` avec `PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet`.
- Sous zsh, `grep --include=*.cs` échoue : utiliser `--exclude-dir=obj --exclude-dir=bin`.
