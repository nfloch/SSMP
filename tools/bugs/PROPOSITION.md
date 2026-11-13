# Sessions de debug autonomes : proposition

Mis en place le 13/11 : `tools/bugs/`, skill `/ssmp-debug`, dépôt privé `nfloch/ssmp-bugs`.

Objectif : Papaours et Jojo jouent et appuient sur F8. Claude repère seul les nouveaux logs, les analyse, prépare
les correctifs et tient un suivi visible, sans récap à écrire.

## 1. Repérer les nouveaux logs

- Un script `tools/bugs/attendre-logs.sh` interroge l'API Gokapi toutes les 2 minutes. Il tourne en arrière-plan
  et ne consomme aucun quota pendant l'attente. Dès qu'un nouvel envoi de Jojo apparaît, le script se termine, ce
  qui réveille la session Claude.
- Claude récupère alors le log de Jojo (`recuperer-logs.sh`). Pour celui de Papaours, il **attend que son jeu soit
  fermé** (le script surveille le processus Silksong), puis l'envoie avec `envoyer-logs.ps1` et le récupère.
- Ensuite Claude relance le script d'attente, jusqu'à ce que Papaours dise que la session est finie.
- Contraintes :
  - La session Claude doit rester ouverte dans l'application pendant la partie.
  - Une routine dans le cloud ne marcherait pas : elle n'a accès ni au PC ni au log de Papaours.
- Doublons : un log envoyé en pleine partie n'est pas supprimé et revient dans l'envoi suivant. Chaque marqueur est
  donc identifié par sa date, son heure et son numéro, et ceux déjà traités sont ignorés (fichier d'état local,
  hors dépôt).

## 2. Traiter les logs

1. **Découpage par script, sans modèle.** Un script Python extrait chaque `[BUG #n]`. Pour chacun, il garde les
   deux instantanés et une fenêtre d'environ 90 secondes des deux logs, débarrassée du bruit (paquets Steam, mises à
   jour de sauvegarde). Il y ajoute les anomalies connues, même sans marqueur : exceptions répétées, morts refusées
   en boucle, ennemis sans mises à jour.
   Résultat : un petit dossier par bug, au lieu de deux logs de 50 000 lignes.
2. **Un agent par bug, en parallèle (3 à la fois au plus), chacun dans son worktree git.** Chaque agent reçoit
   uniquement le dossier de son bug, le dépôt et SUIVI.md. Dans son worktree, sur une branche `bug/<n>`, il peut
   corriger, compiler et commiter sans gêner les autres. Il rend une cause avec ses preuves et un niveau de
   confiance, et commente l'issue (voir 3).
3. **Intégration dans la session principale.** Claude relit chaque rapport et vérifie les preuves. Il reprend les
   bons commits sur `world-sync` (cherry-pick) et compile l'ensemble. Il règle lui-même les conflits quand deux
   correctifs touchent le même fichier. Pas de push ni d'installation sans le feu vert de Papaours.

La procédure tient dans un skill du projet, `.claude/skills/ssmp-debug/`, lancé avec `/ssmp-debug`. Toute session
l'applique de la même façon. Le script d'attente n'en est que le déclencheur.

## 3. Suivi : une issue GitHub par bug

- Chaque marqueur devient une issue : titre `[BUG #3] Ennemi en double (Bone_12)`. Le corps donne qui l'a signalé,
  dans quelle salle et à quelle heure, et un résumé des instantanés.
- Labels de statut : `nouveau`, `en-analyse`, `correctif-pret`, `a-retester`, plus `sans-suite`. L'issue est fermée
  une fois le correctif vérifié en jeu, et rouverte si le bug revient (`/bug #3 encore`).
- L'agent commente sa cause et ses preuves. Le commit cite l'issue (`Fixes nfloch/SSMP#12`). La fermeture
  automatique ne joue que sur la branche par défaut, donc Claude ferme l'issue à la main.
- Consultable partout, téléphone compris, avec l'historique complet. SUIVI.md peut alors devenir un simple index.

Point d'attention : `nfloch/SSMP` est **public**. Ses issues le seraient aussi, alors que les logs contiennent des
identifiants Steam, des noms d'utilisateur Windows et des chemins. Deux options :

- **Dépôt privé dédié au suivi (recommandé)**, par exemple `nfloch/ssmp-bugs`. Les logs bruts et les extraits peuvent
  y être joints sans risque. Les commits de `nfloch/SSMP` y renvoient avec `nfloch/ssmp-bugs#12`.
- **Issues sur `nfloch/SSMP`** (à réactiver), avec seulement des résumés expurgés et aucun extrait brut. Le dépôt
  limite déjà les interactions aux collaborateurs.

## Recommandation

Mettre en place les trois volets, dans cet ordre :

1. Le skill et le script de découpage : c'est ce qui rend l'analyse fiable et économe.
2. Le script d'attente des logs.
3. Les issues GitHub, dans un dépôt privé dédié.

Pendant une partie, la seule chose à faire est de garder cette session ouverte et de dire « session finie » à la
fin. Le reste se fait seul.
