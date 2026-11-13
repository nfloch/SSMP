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

## Scripts pour Jojo et récupération des logs

- [`tools/jojo/`](tools/jojo/README.md) : `installer-ssmp.cmd` (installe le dernier build dans son profil Cogfly) et
  `envoyer-logs.cmd` (envoie son `LogOutput.log` compressé sur le Gokapi). Le lien d'upload est dans
  `tools/jojo/ssmp-jojo.config`, hors dépôt ; `tools/jojo/make-bundle.sh` prépare le zip à lui envoyer.
- `tools/gokapi/recuperer-logs.sh [-l] [-k] [-n <nombre>|all]` : télécharge (ou liste avec `-l`) les derniers logs
  reçus, rangés dans `ssmp-logs` des Téléchargements Windows et préfixés par leur id Gokapi, puis les supprime de
  Gokapi une fois la copie locale en place (`-k` pour les garder). Config dans `~/.config/ssmp/gokapi.env`
  (adresse, id de la demande, clé API d'administration avec les droits « view », « download » et « delete »).

## Sessions de debug : signaler un bug en jouant

- **F8** quand on voit un bug, sans pause ni texte. Ou dans le chat : `/bug <description courte>` (facultatif).
- Le serveur numérote le marqueur et l'annonce dans le chat : `[BUG #3] by Jojo (id 1) in Bone_12`. Chaque jeu
  écrit alors dans son log un instantané de son propre état : salle, hôte de la salle, joueur local, et pour chaque
  entité ses objets hôte et client (actif, visible, collider, PV, états de FSM, position).
- En fin de session, un seul « go » : Claude envoie le log de Papaours, récupère ceux de Gokapi, liste les marqueurs
  (`grep "\[BUG #"`), analyse chaque bug autour de son heure et fait un rapport. Il ne demande des précisions que
  si un marqueur reste ambigu.

## Logs

- Papaours : `%APPDATA%\Cogfly\profiles\SSMP\BepInEx\LogOutput.log`.
- Jojo et Papaours : `envoyer-logs` les envoie sur Gokapi (Claude le lance pour Papaours, jeu fermé) puis supprime
  le log ; `recuperer-logs.sh` les récupère. Noms : `LogOutput-<utilisateur>-<profil>-<date>-<heure>`.
- Depuis `4030ab8`, chaque ligne SSMP commence par l'heure locale (`HH:mm:ss.fff`) : chercher directement les heures
  notées pendant la partie.

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

### Après la partie du 13/11 après-midi (Marrow, Mosstown, Bell Beast)

11. **Banc payant et interrupteur actionnés par le joueur non hôte perdus** (`bell_toll_machine, Bone_01c`,
    `Bone Lever, Mosstown_01`) : pour un objet persistant, seul l'hôte de la salle envoie le changement, sauf
    `IgnoreSceneHost`. Correctif : `IgnoreSceneHost: true` pour 129 entrées de `save-data.json` qui ne changent que
    par un joueur (leviers, interrupteurs, plaques de pression, machines à péage).
    - Limite : chez les autres, seule la valeur sauvegardée change ; l'objet peut ne bouger qu'en rentrant dans la
      salle (même mécanisme que l'arène, à traiter au cas par cas).

### Inventaire de Jojo à moitié remis à zéro (partie du 13/11 soir)

12. **Outils, objets, quêtes… perdus à chaque connexion du joueur distant** : avec la synchro complète, il repart
    d'une partie vierge à laquelle SSMP applique la sauvegarde gardée par le serveur. Les structures complexes de
    PlayerData (`Tools`, `ToolEquips`, `Collectables`, `Relics`, `QuestCompletionData`, `EnemyJournalKillData`…) sont
    en `Sync: false` : ni envoyées ni stockées. Décision : **à la connexion, copie de celles de l'hôte, puis chacun
    les siens pendant la session.** Correctif : `LoginSaveCopy` (JSON du jeu via `JsonUtility`, compressé), ajouté
    par `ModServerManager.AddLoginSaveData` à la sauvegarde envoyée au login et appliqué par `SaveManager`.
    - Logs attendus : `Copying N inventory/progression structures` chez l'hôte, `Applied host copy of 'Tools' at
      login` chez le client.
    - Ce que le client gagne pendant la session est perdu à sa prochaine connexion (il reprend la copie de l'hôte).

### Analyse des sessions du 13/11 (à tester en jeu)

13. **Tous les joueurs déconnectés d'un coup (`Ant_04_mid`, Jojo hôte)** : les gros messages Steam fiables (plus de
    1 200 octets, ~20 ennemis avec données de FSM) étaient lus dans un tampon fixe de 1 200 octets et tronqués. Le
    paquet incomplet restait dans un tampon de restes **commun à tous les clients**, qui collait ensuite les données
    des autres (dont le client local de Papaours) : cadrage perdu, `Could not read packet view…`, puis `Client N timed
    out` pour tout le monde. Correctif : tampon de restes par client (`NetServer`), lecture du message Steam en entier
    (`SteamEncryptedTransport[Server]`).
14. **Skarrgard (`Bone Hunter Throw`, salle `Ant_02` et non `Bone_08`) qui reste sous terre** : sa zone `Guard Range`
    est détachée à la racine de la scène par son FSM et porte son propre FSM ; SSMP la prenait pour son propre
    propriétaire, ne lui trouvait jamais de cible et répondait toujours « joueur hors de portée ». Le boss s'enterrait
    et ne remontait jamais, même pour un hôte seul. Correctif : propriétaire retrouvé via `AlertRange.initialParent`.
15. **Ennemis qui se téléportent, Jojo ultra rapide (15h53)** : l'interpolation supposait 50 ms entre deux positions
    reçues, alors qu'elles arrivent toutes les ~17 ms (ou en rafales sur une connexion moyenne) : vitesse fausse d'un
    facteur 3, retard puis recalage brutal. Correctif : délai réel entre paquets (`PredictiveInterpolation`).
16. **Écran qui tremble et soie gagnée quand l'autre frappe des lianes de soie (16h04, Mosstown_02)** : les lianes,
    objets cassables et leviers envoient la secousse directement au FSM de la caméra (`SendEventByName`,
    `cameraShakeFSM.SendEvent`), hors `CameraManagerReference`, et les lianes appellent `HeroController.AddSilk`.
    Correctif : ignorés pendant le traitement d'une attaque distante. Limite : une secousse jouée après un délai
    dans le FSM passerait encore.
17a. **Ennemi invisible qui frappe après un changement d'hôte (18h17, `Ant_04_mid`)** : en devenant hôte, SSMP
    rejoue l'initialisation des FSM (dont `SetMeshRenderer` off d'un état `Dormant`). Correctif : `Entity.MakeHost`
    recopie l'état `MeshRenderer`/`SpriteRenderer` de la copie client (log `became host with … restoring on`).
17. **Flood de mises à jour `FisherWalkerTimer`/`FisherWalkerIdleTimeLeft`** (des milliers par visite de `Bone_07`,
    sur le canal fiable) : passés en `Sync: false`.

## Bugs ouverts

### Boss : chacun sa victoire, mais on peut rejoindre un combat en cours (décision du 13/11)

- Les victoires restent propres à chaque joueur (`defeatedBellBeast` en `SyncType: Player`). Un joueur mort pendant
  le combat doit pouvoir revenir dans l'arène pendant que l'autre se bat : y entrer, ne plus pouvoir en sortir, et
  voir le boss synchronisé.
- Bell Beast (`bone_05_boss.bundle`), lu avec `tools/fsm` :
  - Murs `Threaded Bell Gate` : ouverts au chargement, fermés par `BG CLOSE`, rouverts par `BG OPEN`, sans état
    sauvegardé. Celui qui revient trouve donc les murs ouverts et peut entrer.
  - Zone `Return Battle` (active si `encounteredBellBeast`) : objet local, non synchronisé. En y entrant, le jeu du
    joueur ferme ses propres murs (`Gate 1`, `Gate 2`), bloque la caméra et envoie `BATTLE START` au boss.
  - Mort du boss : état `Death Broadcast` → `SendEventToRegister("BEAST DEFEATED")`, qui fait avancer `Battle End`
    (murs rouverts, cœur de soie, `SetPlayerDataBool`). Cette action n'était pas rejouée chez les clients : celui
    qui avait rejoint serait resté enfermé. Correctif : synchro de `SendEventToRegister` dans `EntityFsmActions`
    (log `Broadcasting event register 'BEAST DEFEATED' from scene host`).
- À vérifier en jeu : un joueur qui meurt et revient pendant le combat de l'autre. Si un joueur a déjà battu le
  boss, il ne voit pas celui de l'autre : c'est la conséquence de « chacun la sienne ».
- **Règle de jeu : c'est l'hôte de la salle (premier arrivé) qui doit entrer le premier dans l'arène.** Le boss ne
  se réveille qu'avec le `BATTLE START` envoyé par la zone `Return Battle`, objet local. SSMP ne transmet les actions
  que de l'hôte vers les clients : si un client entre en premier, ses murs se ferment mais le boss de l'hôte reste
  endormi jusqu'à ce que l'hôte entre à son tour. Transfert client → hôte du lancement de combat : non prévu pour
  l'instant (décision du 13/11).
- Hôte ayant déjà battu le boss : le boss n'existe pas dans son jeu, donc la copie de l'autre joueur resterait figée
  et intouchable. Piste : laisser un joueur contrôler lui-même les ennemis absents chez l'hôte (non fait).
- Cloches qui tombent (`BoneBeast DropBell …`, `BoneBeast AtkBell …`) : objets de décor avec leur propre FSM, que le
  boss déclenche par `SendEventByName`. Chaque jeu les fait tomber à sa façon, d'où des cloches différentes.

### Pistes restantes des sessions du 13/11

Pas de nouvelle sauvegarde pour reproduire les bugs des zones déjà passées (décision du 13/11) : la corde, la baie de
mousse et les cloches de la Bête-Cloche restent classées sans suite, sauf si un cas semblable revient plus loin. Les
autres peuvent arriver n'importe où et seront repris avec les prochains logs (heures et `[Inventory]`).

- **Corde coupée à 16h03** : probablement le `Bone Lever` de `Mosstown_01` (`mosstown01_shortcut`, `SyncType:
  Player`). Seule la valeur sauvegardée voyage, pas l'animation : l'autre ne voit rien bouger. Pas de correctif.
- **Baie de mousse en l'air (16h14)** : portée par un `Aspid Collector`, libérée à sa mort par FindChild,
  ActivateGameObject puis SetParent. Cause non trouvée dans les logs.
- **Cloches de la Bête-Cloche différentes** : les cloches (`BoneBeast DropBell`/`AtkBell`) ne sont pas des entités ;
  chaque jeu tire sa propre position et son rebond au hasard. Piste : les déclarer comme entités dans
  `entity-registry.json` (patch proposé, non appliqué : boss déjà battu).
- **Ennemi figé ou intouchable quand Jojo est hôte (15h55/15h58)** : l'hypothèse « ennemi absent chez l'hôte » est
  écartée (mêmes entités des deux côtés dans toutes les salles). Plutôt une copie reçue dans un état dormant ou caché
  (collider coupé). À diagnostiquer avec les heures dans les logs.
- **Dégâts invisibles (16h00)** : sans doute `Bone_11`, où Jojo est entré le premier. Non prouvé.
- **Mob buggé (18h02)** : pas d'ennemi précis identifié. Piste : erreur à la création de la copie client de
  `Bone Goomba Large` et de `Bone Crawler` (`WaitForTk2dAnimatorClipFinish` lors de `ProcessClientFsm`), copie peut-être
  à moitié initialisée.
- **`Shakra` jamais créé chez le client** (`EntitySpawner` : `NotImplementedException`).
- **Réseau, à voir après test** : un paquet contenant une donnée fiable part entier sur le canal Steam fiable (une
  perte retarde aussi les positions) ; le RTT mesuré sur Steam est faux, donc SSMP ne s'adapte pas à une connexion
  moyenne ; les positions n'ont pas de numéro de séquence.

### Partie du 13/11 soir, build `34ea7c8` (logs analysés, à tester)

18. **Ennemi en double et qui clignote (20h01, pèlerin `Pilgrim StaffWielder` de `Bone_12`, Papaours hôte)** : chez
    Jojo, l'objet hôte de l'ennemi a été détruit ; ensuite chaque mise à jour fiable levait une exception dans
    `UpdateHostFsmData` (5 500 fois), qui empêchait d'appliquer le reste de la mise à jour, dont la mort envoyée
    par l'hôte. Correctif : objet hôte détruit ignoré, et une erreur sur les données de FSM n'empêche plus le reste.
    - Pourquoi l'objet hôte de Jojo était actif et a été tué chez lui : pas élucidé.
19. **Carte : Jojo ne voyait pas Papaours (les deux avec la boussole)** : l'état « j'ai une icône » n'est envoyé
    qu'au changement, et le serveur ne le transmet pas aux joueurs qui se connectent après. Correctif : chaque joueur
    renvoie son icône quand un autre se connecte.
20. **Banc payé par Jojo resté fermé chez Papaours (20h06, `bell_toll_machine, Dock_01`)** : la valeur arrivait bien,
    mais l'application immédiate (`SaveChanges.ApplyPersistentValueSaveChange`) était un stub vide. Correctif : une
    machine à péage (FSM `Unlock Behaviour`) en attente dans la salle joue son ouverture (`Retract Animation`).
21. **Ennemi figé chez Jojo (20h08, `Bone Hunter Buzzer (1)` de `Bone_East_04`)** : tué par Papaours une seconde
    avant l'arrivée de Jojo. Le serveur ne garde que les derniers PV, pas la mort, et certaines morts laissent des PV
    au-dessus de 0. Correctif : l'hôte envoie 0 PV à la mort d'un ennemi.
22. **Papaours bloqué dans l'arène (20h11, `Ant_05b`, Jojo hôte)** : un `BattleScene` compte les ennemis restants
    comme les enfants de ses vagues. Chez le client, ce sont les objets hôtes inactifs, alors que la copie qui meurt
    est à la racine de la scène : le combat ne finissait jamais et les portes restaient fermées. Correctif : à la mort
    de la copie, l'objet hôte est retiré de sa vague et le combat recompte (log `removing it from the enemies of
    battle`).
- Logs : chaque joueur écrit maintenant son inventaire, son équipement et ses capacités (`[Inventory]`) en entrant
  dans la partie et à chaque banc.

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
- Correctifs 12 à 17 : copie de l'inventaire au login, Skarrgard dans `Ant_02`, plus de déconnexion générale avec Jojo
  hôte d'une salle chargée, ennemis plus fluides, plus de secousse ni de soie avec les lianes.

## Outils

- Lire un FSM du jeu (états, actions, cibles) : voir [`tools/fsm/`](tools/fsm/README.md).
- Jeu décompilé : `~/claude-projects/ssmp-build/game-src`, produit avec `ilspycmd`.
  - Lancer `ilspycmd` avec `PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet`.
- Sous zsh, `grep --include=*.cs` échoue : utiliser `--exclude-dir=obj --exclude-dir=bin`.
