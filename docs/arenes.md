# Arènes, boss et morts : architecture

État de la zone après la revue d'architecture de nfloch/ssmp-bugs#8 (14/11). Les règles voulues sont dans
`PRINCIPES.md`, section « Boss et arènes ». Ce document dit **où** et **comment** elles sont faites. Un correctif de
la zone part d'ici : il dit quelle pièce il change, et ce qu'il remplace.

## L'idée centrale : une « rencontre »

Une **rencontre** (`Encounter`) est un combat de la salle : une arène, ou un boss. Son état est **décidé par l'hôte
de salle** et envoyé à tous :

| État | Sens |
|---|---|
| `Idle` | pas commencée chez l'hôte de salle |
| `Started` | en cours chez l'hôte de salle |
| `Ended` | finie chez l'hôte de salle (gagnée, ou terminée par le FSM du boss) |

C'est **la seule source** de « combat en cours ». L'attente des joueurs, la mise à terre, le retour dans l'arène, la
fin du combat chez les clients et la musique la lisent toutes au même endroit.

Il y a deux sortes de rencontres :

| Sorte | Quoi | Zone | Portes | Exemples |
|---|---|---|---|---|
| `Battle` | un `BattleScene` du jeu : arène à vagues, ou boss de ses vagues qui la lance depuis son FSM | le collider du `BattleScene`, même désactivé | les enfants de `BattleScene.gates` | Bone_01, Ant_05b, Mossbone Mother (Tut_03) |
| `BossGates` | un boss (`"boss": true` dans `entity-registry.json`) qui n'est pas dans un `BattleScene` et dont les FSM ferment et rouvrent les portes de sa salle | de la porte la plus à gauche à la plus à droite, hauteur des portes ± 3 | les FSM de la salle qui réagissent à `BG CLOSE` **et** `BG OPEN`, hors `BattleScene`, au moins deux, qui encadrent le boss | Bell Beast (`Bone_05_boss`) |

Un boss sans arène ni portes à lui n'a pas de rencontre : la mort y reste celle du jeu (voir « Non couvert »).

## Les pièces

| Pièce | Fichier | Rôle |
|---|---|---|
| `Encounter` | `SSMP/Game/Client/Encounters/Encounter.cs` | une rencontre : sorte, état de l'hôte, **zone** (`Contains`, seule géométrie de la zone), **portes** (`Gates`, tri entrée/progression `ClassifyGates`), actions locales : `LockLocally` (fermer derrière soi), `OpenForReentry` (rouvrir les portes d'entrée), `EndLocally` (finir l'arène dans ce jeu) |
| `EncounterTracker` | `SSMP/Game/Client/Encounters/EncounterTracker.cs` | les rencontres des salles chargées ; ce que chaque jeu observe : portes de boss fermées/ouvertes par le jeu (hook `Fsm.DoTransition`), fin locale d'une arène (hook `BattleScene.DoEndBattle`, logs `[BattleDiag]`), point d'entrée du héros ; l'événement `Ending` |
| `EncounterComponent` | `SSMP/Game/Client/Entity/Component/EncounterComponent.cs` | côté réseau : porté par les entités d'une rencontre (ennemis des vagues, entité qui appelle `StartBattle`, boss) ; l'hôte de salle décide et envoie l'état, les clients l'appliquent. Remplace `BattleComponent` |
| `ArenaWait` | `SSMP/Game/Client/ArenaWait.cs` | règle 1 : retenir le départ tant qu'il manque quelqu'un, message, portes de progression |
| `DownedManager`, `DownedOverlay` | `SSMP/Game/Client/Downed/` | règles 2, 3 et 8 : mise à terre, retour, vraie mort de tous |
| `BossHealth` | `SSMP/Game/Client/Entity/BossHealth.cs` | règle 6 : PV des boss multipliés à l'apparition (indépendant de la rencontre) |
| Victoires communes | `SSMP/Resource/save-data.json` (`defeated*`, `encountered*` en `Server`), logs `[Victory]` dans `SaveManager` | règle 5 |
| `MusicComponent` | `SSMP/Game/Client/Entity/Component/MusicComponent.cs` | musique de boss jouée par le FSM d'une entité, gardée pour les retardataires ; remise à « aucune » à la fin d'une rencontre (`ResetHostMusic`) |
| `LayerComponent` | `SSMP/Game/Client/Entity/Component/LayerComponent.cs` | couche physique des ennemis (sortis du décor) pour les retardataires ; pas lié à la rencontre |
| Rejeu `SendEventToRegister` | `SSMP/Game/Client/Entity/Action/EntityFsmActions.Fsm.cs` | rejoue chez les clients l'événement global de fin d'un boss (`BEAST DEFEATED`) : c'est lui qui lance la fin du boss dans chaque jeu |
| `ReleaseHostFromBattle` | `HealthManagerComponent` | chez un client, retire l'objet hôte d'un ennemi mort de sa vague : les vagues locales restent au même point que chez l'hôte, pour qu'un client qui devient hôte continue le combat |
| Fin par la sauvegarde (correctif 7) | `SaveManager`, objets persistants `BattleScene` | une arène marquée terminée dans la sauvegarde commune pendant qu'on est dans la salle se finit par `Encounter.EndLocally` ; seul chemin pour une arène sans entité synchronisée |

## Le flux : hôte de salle → serveur → clients

```
Hôte de salle (son jeu simule)                Serveur                      Clients de la salle
─────────────────────────────                ───────                      ───────────────────
EncounterComponent (propriétaire)
  Battle : BattleScene.started / completed
  BossGates : portes fermées par le jeu,
              boss morts, portes rouvertes
        │  état changé
        ▼
  donnée d'entité « Encounter » (1 octet)  ──► gardée (type > Death) ──►  EncounterComponent.ApplyState
                                              renvoyée à l'entrée          Started : l'arène démarre quand le
                                              dans la salle                héros est dedans ; boss : portes
                                                                           fermées après 1 s dedans
                                                                           Ended : Ending, puis EndLocally
```

- Un seul porteur envoie et applique l'état par rencontre : le premier `EncounterComponent` (`Carriers[0]`), pas un
  par ennemi des vagues.
- La donnée est stockée par le serveur : un joueur qui entre en cours de combat (retard, reconnexion, retour après
  une vraie mort) reçoit l'état actuel (`(entering late)` dans les logs).
- Une arène sans entité synchronisée n'a pas de porteur : son état est celui du jeu local (chaque jeu la joue
  seul) et sa fin arrive par la sauvegarde commune (correctif 7).
- Quand l'hôte de salle part, un client devient hôte (`InitializeHost`) : il garde l'état reçu, recompte les
  ennemis de ses vagues et décide à son tour.

## Cycle de vie d'une rencontre

1. **Chargement de la salle.** Les entités sont créées ; chaque entité d'une arène, ou boss avec ses portes, crée son
   `EncounterComponent`, qui crée la rencontre. 1 s après l'arrivée, `ArenaWait` note le point d'entrée
   (`EncounterTracker.EntryPoint`) et ferme les **portes de progression** des arènes sauvegardées et non terminées.
2. **Attente** (`Idle`, règle 1). Tant qu'un joueur connecté n'est pas dans la zone de la rencontre :
   - le déclencheur `BattleScene.OnTriggerEnter2D` est retenu ;
   - la transition FSM qui réveille le boss est refusée (`Fsm.DoTransition`) et rejouée plus tard, y compris quand
     le départ passe par d'autres FSM (première rencontre de la Bell Beast : `Special Detector` → `DESTROY` aux
     lianes → `FREE` à `Beast Anim` → `Land` ferme les portes) ;
   - tout autre `StartBattle` est différé ;
   - le message « Nul fil ne doit être laissé en arrière » s'affiche.
   La zone retenue est celle de la rencontre : pour un déclencheur de boss (`Return Battle`, `Special Detector`),
   c'est la zone de la rencontre qui le contient (entre les portes), sinon le collider du déclencheur.
3. **Départ** (`Started`). Chez l'hôte de salle, l'arène démarre (déclencheur ou FSM) ou le jeu ferme les portes du
   boss ; `EncounterComponent` envoie `Started`. Chaque client démarre son arène quand son héros est dans la zone
   (murs, caméra, musique de l'arène), et ignore son propre déclencheur. Pour un boss, les FSM locaux de chaque jeu
   ferment ses portes ; à défaut (retardataire), `EncounterComponent` les ferme après 1 s dans la zone.
4. **Combat.** Les ennemis suivent l'hôte de salle (positions, FSM, PV, actions rejouées), `LayerComponent` et
   `MusicComponent` couvrent les retardataires. Mort du héros dans la zone d'une rencontre `Started` : voir
   « Morts ».
5. **Fin.** Chez l'hôte, l'arène se termine (`DoEndBattle`) ou les boss sont morts, ou le jeu rouvre les portes du
   boss ; `EncounterComponent` envoie `Ended` et remet la musique gardée à « aucune ». Dans chaque jeu, la fin est
   annoncée **une fois** par `EncounterTracker.Ending`, avant que la séquence de fin ne se joue quand c'est
   possible :
   - arène : juste avant `BattleScene.DoEndBattle` ;
   - boss : quand l'état `Ended` arrive, ou quand la copie du boss meurt dans ce jeu.
   Chez un client, l'arène se termine par `Encounter.EndLocally` : `DoEndBattle` si elle a démarré ici, sinon
   `BattleCompleted` (portes rouvertes en `BG QUICK OPEN`). **Une fin venue des vagues locales d'un client est
   refusée** tant que l'hôte n'a pas fini (log `would end from its local waves on a scene client`). La fin d'un boss
   se joue dans chaque jeu par son propre FSM, lancée par le rejeu de `SendEventToRegister` (`BEAST DEFEATED`).
6. **Remise à zéro.** Quand tous les joueurs de la rencontre sont morts (règle 3), ils meurent pour de vrai et
   quittent la salle ; la salle vide, le serveur efface ses données d'entité (`HandlePlayerLeaveScene`) et la
   rencontre repart de zéro au prochain chargement. Aucun code de plus.

## Morts (règles 2, 3 et 8)

`DownedManager.OnHeroDie`, appelé à la place de `HeroController.Die` :
- pas connecté, ou hors d'une rencontre `Started` (`EncounterTracker.FindInProgressAt`) : mort du jeu (règle 8) ;
- mort « non létale » (mémoires) : mort du jeu ;
- aucun coéquipier vivant dans la zone : vraie mort ;
- sinon : **à terre** 20 s, caché, ignoré des ennemis, écran gris, caméra sur un coéquipier.

Ensuite :
- **20 s passées, combat toujours en cours** : retour soigné à côté de l'arène (point de réapparition après danger,
  dernière position hors des rencontres, porte d'entrée), dans la même salle. `Encounter.OpenForReentry` ouvre dans
  son jeu seulement les portes d'entrée (pas celles de progression) ; de retour dans la zone, `LockLocally` les
  referme (arène : `LockInBattle` ; boss : chaque porte, même depuis un état qui n'écoute plus `BG CLOSE`, comme
  l'état `Open` des portes de la Bell Beast). Ces ouvertures et fermetures locales ne comptent jamais comme début
  ou fin du combat du boss.
- **Victoire pendant qu'il est à terre** (décision du 14/11) : sur `Ending`, il revient tout de suite, soigné,
  **là où il est tombé**, sans fondu, avant la fin du combat dans son jeu. Il vit la fin comme les autres et a ses
  propres récompenses (cœur de soie, chapelets).
- **Plus personne de vivant dans la zone depuis 0,5 s** (à terre, ou revenu dehors sans être rentré) : vraie mort
  pour chacun (règle 3).

L'état de mort (`Alive` / `Downed` / `Dead`) part par le paquet `PlayerDeath` vers les joueurs de la salle, qui
cachent un joueur à terre.

## Où chaque règle de PRINCIPES est faite

| Règle (« Boss et arènes ») | Où |
|---|---|
| 1. Tous dans l'arène pour qu'elle démarre ; seul en ligne, départ normal | `ArenaWait.MustWait` (zone : `Arena.Contains` → `Encounter.Contains`), hooks déclencheur / `StartBattle` / `Fsm.DoTransition`, `Release` |
| 1. Murs d'entrée ouverts tant qu'il manque quelqu'un | rien ne démarre, donc rien ne ferme |
| 1. Portes de progression fermées avant la fin | `ArenaWait.CloseProgressionGates` (tri `Encounter.ClassifyGates`), réouverture par la fin de l'arène ou `UpdateClosedGates` ; arènes `Battle` sauvegardées seulement |
| 1. Message « Nul fil… » / « En attente de : … » | `ArenaWait.UpdateMessage`, `ShowTitle`, `OnGetLanguageString` |
| 2. À terre 20 s, puis retour à côté, murs ouverts pour lui | `DownedManager` (`StartDowned`, `Respawn`, `UpdateWaiting`), `Encounter.OpenForReentry` / `LockLocally` |
| 2 bis. Victoire pendant qu'il est à terre : retour sur place avant la fin | `DownedManager.OnEncounterEnding`, `EncounterTracker.Ending` |
| 3. Tous à terre ou dehors : vraie mort, combat remis à zéro | `DownedManager.CheckNobodyAlive`, `RealDeath` ; remise à zéro par le serveur (salle vide) |
| 4. Monstres synchronisés | entités SSMP (hôte de salle) |
| 5. Victoires communes | `save-data.json`, `SaveManager` (`[Victory]`) |
| 6. PV des boss multipliés | `BossHealth`, réglage `BossHealthMultiplier` |
| 7. L'arène suit l'hôte ; un retardataire retrouve l'état | `EncounterComponent` (état stocké par le serveur), `EncounterTracker` (fin locale d'un client retenue), `LayerComponent`, `MusicComponent` |
| 8. Mort hors arène : jeu normal | `DownedManager.OnHeroDie` (`Death outside a battle in progress`) |

## Logs

| Préfixe | Quoi |
|---|---|
| `[Encounter]` | rencontre de boss trouvée (`Boss encounter '…': gates …, area …`), portes fermées/ouvertes par le jeu, état envoyé (`… started/ended as scene host, sending it`) ou reçu (`'…' is Started on the scene host (entering late)`), fin annoncée (`Encounter '…' is ending in this game (…)`), fin locale (`… ending it locally`), fin locale retenue, retardataire enfermé, nouvel hôte |
| `[BattleDiag]` | `StartBattle` / `DoEndBattle` d'un `BattleScene` dans ce jeu |
| `[ArenaWait]` | attente, transitions retenues/rejouées, message, portes de progression |
| `[Downed]` | mise à terre, retour, vraie mort, état des autres joueurs |
| `[BossHp]` | multiplicateur de PV |
| `[Victory]` | victoires communes envoyées / reçues |

Restent sans préfixe, pour les listes de test existantes : `Battle '…' is in progress on the scene host …, starting
it once inside the arena`, `Starting battle '…' started by the scene host`, `Ignoring the trigger of battle '…' as
scene client`, `Battle scene '…' completed by scene host, ending it locally`, `Broadcasting event register '…' from
scene host`, `Entity '…' died, removing it from the enemies of battle '…'`.

## Non couvert, à savoir avant de toucher

- **Boss sans arène et sans portes** (Moorwing, Skarrgard… à vérifier boss par boss) : pas de rencontre, donc pas
  d'attente par la zone (seulement si une transition FSM est reconnue), pas de mise à terre.
- **Boss qui ferment leurs portes sans `BG CLOSE` / `BG OPEN`**, ou dont les portes n'encadrent pas le boss : pas
  de rencontre. Le log `has no gates of its own, no encounter` le dit au chargement.
- **Zone d'un boss `BossGates`** : un rectangle entre les portes, hauteur des portes ± 3. Une arène plus haute que
  ses portes (plafond loin) a une zone trop basse.
- **Portes de progression** : seulement pour les arènes `Battle` sauvegardées. Rien pour les portes d'un boss
  `BossGates`.
- **Première rencontre de la Bell Beast** : seul le jeu qui frappe les lianes libère la bête de son décor
  (`Beast Anim` n'est pas une entité). Chez les autres, la bête enchaînée reste visible et la musique de
  `Beast Anim` ne part pas ; les portes se ferment par `EncounterComponent` (retardataire).
- **Musique d'un boss jouée par un FSM d'une autre entité** (Bell Beast : `Beast Anim`, `Return Battle`) : jouée
  dans chaque jeu par son propre FSM, pas gardée pour les retardataires.
- **Hôte qui change pendant le combat alors que son arène locale n'a pas démarré** (héros dehors) : la rencontre
  reste `Started` jusqu'à ce que son arène démarre (héros dedans) puis se termine chez lui.
