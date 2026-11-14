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
| Victoires de boss (`defeated*`) | 14/11 (remplace « chacun sa victoire » du 13/11) |

**Compétence acquise en cours de partie** : quand un joueur obtient une compétence normalement, le tutoriel
d'obtention (l'écran qui présente la compétence et ses touches) s'affiche aussi chez tous les autres joueurs
connectés (Papaours, 14/11).

| Propre à chaque joueur | Décision |
|---|---|
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

## Boss et arènes (décision de Papaours du 14/11 : coop de l'histoire)

1. **Tous les joueurs connectés doivent être dans l'arène pour qu'elle démarre** (arène à vagues comme boss). Seul
   en ligne, un joueur la lance normalement.
   - Tant qu'il manque quelqu'un : les vagues ne partent pas et le boss ne se réveille pas. Les murs d'entrée
     restent ouverts.
   - **Les portes qui mènent plus loin restent fermées** si l'arène n'est pas terminée, même avant son démarrage :
     on ne contourne pas une arène qui bloque la progression.
   - Message affiché comme un titre de zone (grand texte qui apparaît puis s'efface, petite ligne dessous), quand un
     joueur est dans l'arène et qu'il manque quelqu'un, dans la langue du jeu :
     - FR : *Nul fil ne doit être laissé en arrière* / « En attente de : Jojo, KlayFouly »
     - EN : *No thread shall be left behind* / "Waiting for: Papaours"
2. **Mort pendant le combat** : pas de vraie mort (ni cocon, ni perte de chapelets). Pendant 20 s, l'écran est grisé,
   avec un compte à rebours, et la caméra suit un coéquipier encore en vie. Le joueur réapparaît ensuite à côté de
   l'arène, **dans la même salle** (l'hôte de salle continue de simuler le combat), et peut y rentrer : dans son jeu
   les murs le laissent passer, puis se ferment derrière lui.
3. **Tous les joueurs de l'arène morts (ou en attente) en même temps** : vraie mort pour tous, chacun réapparaît à
   son dernier banc ; le combat est remis à zéro.
   Un joueur revenu après ses 20 s mais encore dehors compte comme « en attente » : il meurt aussi (Papaours,
   14/11).
   **Victoire pendant qu'un joueur est à terre** : il revit tout de suite sur place, là où il est tombé, avant la
   séquence de fin du combat, pour la vivre comme les autres et recevoir les mêmes récompenses (Papaours, 14/11).
4. **Les monstres sont toujours synchronisés** (hôte de salle).
5. **Victoires communes** : battre un boss vaut pour tous (`defeated*` en commun). Remplace « chacun sa victoire »
   du 13/11.
6. **PV des boss** multipliés par défaut par le nombre de joueurs ; le multiplicateur (nombre à virgule) est
   réglable.
7. L'arène suit celle de l'hôte de salle, qu'elle démarre par sa zone de déclenchement ou par le FSM d'un boss
   (`BattleComponent`, nfloch/ssmp-bugs#4 et #2). Un joueur qui entre en cours de combat retrouve l'état actuel
   (murs, musique, ennemis).
8. Mort hors arène : comportement normal du jeu (banc, cocon).

## Ennemis

- Un ennemi doit pouvoir voir, viser et toucher **tous** les joueurs, pas seulement l'hôte de salle.
- Règle de choix de la cible : **à décider** (nfloch/ssmp-bugs#3 : le joueur qui le frappe, ou le plus proche avec
  une distance corrigée).

## Builds

- Tout le monde joue le même build de la branche `world-sync` (release `latest-world-sync`). Un nouveau type de
  donnée réseau (composant d'entité, champ de paquet) rend les builds incompatibles : il faut mettre tout le monde à
  jour. Le serveur avertit dans le chat si un joueur n'a pas le même build.
- Mise à jour depuis le menu du jeu (`SelfUpdater`), ou `tools/jojo/installer-ssmp.ps1`.

## Architecture : pas de correctif sur un correctif

Quand une nouvelle règle ou un nouveau correctif touche une zone déjà corrigée (arènes, morts, synchro de
sauvegarde…), on reprend la conception de la zone : le code devenu inutile ou redondant est remplacé ou supprimé,
pas contourné (Papaours, 14/11). Chaque correctif liste l'ancien code qu'il remplace ; après plusieurs correctifs
dans une même zone, une revue d'architecture de la zone est faite avant d'en ajouter d'autres.

## Débogage

- F8 ou `/bug` pendant la partie, logs envoyés tout seuls, une issue par bug dans le dépôt privé `nfloch/ssmp-bugs`
  (procédure : `.claude/skills/ssmp-debug/SKILL.md`). Les extraits de logs ne vont jamais dans le dépôt public.
- Papaours précise ou corrige dans les commentaires des issues, y compris sur un bug déjà corrigé.
