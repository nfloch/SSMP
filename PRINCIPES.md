# Principes du co-op (world-sync)

Les règles en vigueur pour la logique globale du mod : ce qu'on veut obtenir, pas l'historique (pour l'historique et
le détail des correctifs, voir `SUIVI.md`). Chaque règle cite d'où vient la décision. À lire avant tout correctif :
un correctif qui contredit une règle ne tranche pas seul, il le signale.

## Le but

Jouer à plusieurs (2, 3 ou plus) dans **le même monde**, sur la sauvegarde de l'hôte du serveur (Papaours). Ce
qu'un joueur fait dans le monde vaut pour tous. Chacun garde son propre personnage : vie, soie, position, mort.

## Ce qui est commun, ce qui est propre à chacun

Réglé dans `SSMP/Resource/save-data.json` : `SyncType: Server` = commun (stocké par le serveur, envoyé à tous et aux
joueurs qui se connectent), `SyncType: Player` = propre à chaque joueur.

| Commun à tous | Décision |
|---|---|
| Objets du monde : leviers, portes, ponts, coffres, ramassables, rochers de géo (`persistentBoolItems`, `geoRocks`…) | base de world-sync |
| Salles cartographiées et visitées (`scenesMapped`, `scenesVisited`) | base de world-sync |
| Cartes de zone, plume, épingles, marqueurs (`Has*Map`, `hasQuill`, `QuillState`, `hasPin*`, `hasMarker*`, `mapAllRooms`) | 14/11, nfloch/ssmp-bugs#1 |
| Compétences acquises : dash, cape, saut mural, double saut, grappin, envol de soie, attaque chargée, parade, Needolin et son amélioration, compétences de soie (`has*`) | 14/11, demande de Papaours |
| Bancs achetés, péages (machines à péage) | correctifs du 13/11 (SUIVI.md) |

**Compétence acquise en cours de partie** : quand un joueur obtient une compétence normalement, le tutoriel
d'obtention (l'écran qui présente la compétence et ses touches) s'affiche aussi chez tous les autres joueurs
connectés (Papaours, 14/11).

| Propre à chaque joueur | Décision |
|---|---|
| Victoires de boss (`defeated*`) | 13/11 : « chacun sa victoire » |
| Vie, soie, position, mort, cocon | base |
| Améliorations d'aiguille, niveau de soie, emplacements d'outils (`nailUpgrades`, `silkRegenMax`, `UnlockedExtra*Slot`) | à décider |
| Le reste de PlayerData (quêtes, PNJ, dialogues) : environ 1 200 entrées en `Player` | par défaut, à revoir au cas par cas |

**Inventaire** (outils et compétences de soie, blasons, objets, reliques, quêtes, journal) : **partagé en direct**,
objet par objet (`InventorySync`, 14/11, nfloch/ssmp-bugs#6). Ce qu'un joueur gagne arrive tout de suite chez les
autres ; la sauvegarde commune est celle de l'hôte, qui reçoit tout.
- Débloqué / ramassé / déposé : union. Objets (`Collectables`) : réserve commune, une dépense retire l'objet chez
  tous. Journal : maximum (pas de double comptage). Quêtes : acceptée/terminée au dernier changement.
- Propre à chaque joueur, gardé par le serveur pour sa reconnexion : outils équipés, disposition des blasons,
  emplacements supplémentaires, fioles, munitions, marqueurs « nouveau ».
- À décider : PNJ et dialogues liés aux quêtes (encore propres à chacun alors que les quêtes sont communes),
  emplacements de blason débloqués.

## Hôte du serveur, hôte de salle

- L'hôte du serveur (Papaours, id 0) garde la sauvegarde commune.
- Dans chaque salle, **l'hôte de salle est le premier joueur entré**. Son jeu simule les ennemis, boss et arènes ;
  les autres jeux affichent des copies qui suivent ses données (positions, FSM, PV, actions rejouées). Si l'hôte de
  salle part, un autre joueur présent prend le relais.
- Les actions vont de l'hôte de salle vers les clients. Ce qu'un client déclenche seul dans son jeu (zone de
  déclenchement locale, par exemple) n'atteint pas l'hôte, sauf transmission prévue exprès.

## Arriver en cours de route

**Un joueur qui entre dans une salle après les autres (arrivée tardive, retour après une mort, reconnexion) doit
retrouver l'état actuel de la salle**, pas l'état de départ (Papaours, 14/11 sur nfloch/ssmp-bugs#4).
- Les actions FSM ne sont transmises qu'aux joueurs présents au moment où elles se jouent. Tout état durable qui en
  découle (murs d'arène fermés, musique de boss, couche physique d'un ennemi sorti du décor…) doit aussi être envoyé
  comme donnée d'entité stockée par le serveur (type `> Death`, rejouée à l'entrée dans la salle), comme
  `LayerComponent` (nfloch/ssmp-bugs#5).
- Tout correctif d'une action de combat se pose la question : « et pour celui qui arrive après ? ».

## Boss et arènes

- Chacun sa victoire, mais **on peut toujours rejoindre un combat en cours** : un joueur mort pendant le combat
  revient dans l'arène, y est enfermé avec les autres, voit le boss synchronisé, entend la musique, et sort à la
  fin (13/11, puis 14/11).
- Le démarrage d'une arène (`BattleScene.StartBattle` / `LockInBattle`) et sa fin sont transmis par l'hôte de salle,
  quel que soit le boss (nfloch/ssmp-bugs#4). La musique de boss l'est aussi.
- L'arène suit celle de l'hôte de salle, qu'elle démarre par sa zone de déclenchement ou par le FSM d'un boss
  (`BattleComponent`, 14/11, nfloch/ssmp-bugs#4 et #2) : chez un client, elle démarre quand celle de l'hôte est en
  cours et que son héros est dans l'arène, et se termine quand celle de l'hôte se termine. La zone de déclenchement
  d'un client est ignorée.
- Règle de jeu actuelle : l'hôte de salle entre le premier dans l'arène. Un client qui y entre avant lui n'est pas
  enfermé, il attend que l'hôte lance le combat (décision du 13/11 ; conséquence du 14/11 à confirmer par Papaours).
- Un hôte qui a déjà battu le boss ne l'a plus dans son jeu : le combat d'un autre joueur ne marche pas dans ce cas
  (piste non faite : laisser ce joueur simuler les ennemis absents chez l'hôte).

## Ennemis

- Un ennemi doit pouvoir voir, viser et toucher **tous** les joueurs, pas seulement l'hôte de salle.
- Règle de choix de la cible : **à décider** (nfloch/ssmp-bugs#3 : le joueur qui le frappe, ou le plus proche avec
  une distance corrigée).

## Builds

- Tout le monde joue le même build de la branche `world-sync` (release `latest-world-sync`). Un nouveau type de
  donnée réseau (composant d'entité, champ de paquet) rend les builds incompatibles : il faut mettre tout le monde à
  jour. Le serveur avertit dans le chat si un joueur n'a pas le même build.
- Mise à jour depuis le menu du jeu (`SelfUpdater`), ou `tools/jojo/installer-ssmp.ps1`.

## Débogage

- F8 ou `/bug` pendant la partie, logs envoyés tout seuls, une issue par bug dans le dépôt privé `nfloch/ssmp-bugs`
  (procédure : `.claude/skills/ssmp-debug/SKILL.md`). Les extraits de logs ne vont jamais dans le dépôt public.
- Papaours précise ou corrige dans les commentaires des issues, y compris sur un bug déjà corrigé.
