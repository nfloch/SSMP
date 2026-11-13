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

## Dépôt GitHub (nfloch/SSMP)

- Public, seul `nfloch` (admin) peut pousser. Issues, wiki et discussions désactivés.
- Interactions limitées aux collaborateurs (pas de PR, d'issue ni de commentaire du public) : **expire le
  12/05/2027**, à renouveler (`gh api -X PUT repos/nfloch/SSMP/interaction-limits -f limit=collaborators_only
  -f expiry=six_months`).
- Workflows des contributeurs externes : approbation obligatoire pour tous (`all_external_contributors`).
- `Build DLL` ne se déclenche que sur `push` : ne pas y ajouter `pull_request`, il tourne sur le runner du cluster.

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

### Après la partie du 13/11 (Bonetown → Bone_01, arène des Bone Goomba)

5. **Écran qui tremble quand l'autre joueur tape** : ses coups sont rejoués chez nous, et les secousses de
   coup/casse/mort passaient. Correctif : hooks sur `CameraManagerReference.DoShake` et `DoShakeInRange`, ignorés
   pendant le traitement d'une attaque distante (même portée que la suppression du recul).
   - Non couvert : les secousses envoyées par FSM (`cameraShakeFSM.SendEvent`, ex. `EnemyKillShake`).
6. **Ennemis déjà tués par l'hôte vivants et invincibles pour le joueur qui arrive** : le serveur ne stocke pas les
   morts (`ServerManager` ne garde que les données de type `> Death`), seulement la dernière vie. Le client recevait
   des ennemis à 0 PV qu'il n'a pas le droit de tuer (« HealthManager Die was called on client entity », 59 fois chez
   Jojo). Correctif : `HealthManagerComponent.UpdateHealth` cache l'ennemi si l'instantané d'arrivée donne ≤ 0 PV.
   - Log attendu : `is already dead on the scene host, hiding it`.
7. **Murs de fin d'arène restés fermés chez le client** : la fin du combat arrivait (`Battle Scene, Bone_01, True`)
   mais `SetValueOverride` ne prévient pas `BattleScene`. Correctif : `SaveManager` appelle `DoEndBattle()` (ou
   `BattleCompleted()` si l'arène est inactive). Log attendu : `completed by scene host, ending it locally`.
8. **`NullReferenceException` en boucle** (`OnUpdatePersistents` → `PersistentBoolItem.LookForMyFSM`, 136 000 fois
   chez Papaours, log de 132 Mo) : on lisait chaque frame des objets persistants détruits (géo ramassé). Correctif :
   `PersistentFsmData.Owner`, les données dont l'objet est détruit sont ignorées.

9. **La brute au crâne (`Bone Goomba Large`) ne charge pas vers Jojo** : son FSM `Control` vise la variable globale
   `Hero` (héros local de l'hôte) dans `FaceObjectV2`, `GetDistance` et `CheckPassedTarget`. Les deux premières étaient
   déjà réorientées par SSMP, pas `CheckPassedTarget` : si l'hôte est derrière la brute, elle se croit « passée » dès
   le départ (`Charge` → `PASS` → `No Turn` → `Skid`). Correctif : `CheckPassedTarget` ajouté à
   `action-registry.json` (`targeted_fsm`).
10. **Ennemis d'arène « en fond de salle », sans collision, chez le client** : au début du combat la brute passe en
    couche 2 (`BG Hiding`), puis 14 et enfin 11 (ennemis) en émergeant, avec une teinte d'arrière-plan retirée par
    `Tk2dSpriteSetColor` et un `Hidden Group` trouvé par `FindNamedChild` puis désactivé. Ces trois actions n'étaient
    pas rejouées chez le client, qui gardait une copie sans collisions et teintée. Correctif : synchro de `SetLayer`,
    `Tk2dSpriteSetColor` et `FindNamedChild` dans `EntityFsmActions`. Explique sans doute aussi le « on se passe à
    travers » de la première entrée de Jojo dans l'arène.

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
- Log de diagnostic ajouté (`GamePatcher.BreakableDiagnostics.cs`, à retirer une fois le bug compris) : chaque
  coup sur un `Breakable`, n'importe où dans le jeu, écrit une ligne `[BreakableDiag]` dans `LogOutput.log` avec le
  résultat (`response`), la condition qui a ignoré le coup (`reason`), `isBroken`, `wasAlreadyBroken` et l'état de
  `breakableRange`. Si un mur reste incassable ailleurs, chercher sa ligne : `reason` désigne la piste.
  Aucune ligne pour un mur frappé : `Breakable.Hit` n'est même pas appelé, le problème est en amont.
- Autre étape possible : lire la structure de Tut_02 dans
  `Hollow Knight Silksong_Data/StreamingAssets/aa/StandaloneWindows64/scenes_scenes_scenes/tut_02.bundle`
  (UnityPy, à installer : `python3 -m venv` a échoué faute d'ensurepip, essayer `uv` ou `apt install python3-venv`).
  Sinon, ajouter un log de diagnostic sur `Breakable.Hit` pour ce mur : `isBroken`, `breakableRange.IsInside`,
  `requireBroken`.

### À vérifier au prochain test

- Focus des ennemis sur les joueurs autres que l'hôte : world-sync a du code pour ça (`GamePatcher.EnemyTargeting`,
  `PlayerTargetRegistry`), mais il n'écrit rien dans les logs. À vérifier visuellement.
- Les correctifs 3 et 4 ci-dessus.

## Outils

- Lire un FSM du jeu : `uv` via mise (`ssmp-build/mise.toml`), venv `ssmp-build/unitypy-venv` avec UnityPy.
  Il faut `UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"`. Les scènes sont dans
  `StreamingAssets/aa/StandaloneWindows64/scenes_scenes_scenes/<scene>.bundle` ; les paramètres d'action se décodent
  avec l'enum `ParamDataType` de `PlayMaker.dll`.

- Jeu décompilé : `~/claude-projects/ssmp-build/game-src`, produit avec `ilspycmd`.
  - Lancer `ilspycmd` avec `PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet`.
- Sous zsh, `grep --include=*.cs` échoue : utiliser `--exclude-dir=obj --exclude-dir=bin`.
