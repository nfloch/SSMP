# Scripts Windows pour le deuxième joueur

Deux scripts à double-cliquer, pour Windows (PowerShell 5.1, installé par défaut) :

- **`installer-ssmp.cmd`** : télécharge le dernier build SSMP de la branche `world-sync` (pré-release
  `latest-world-sync`) et l'installe dans le profil Cogfly qui contient SSMP. Le jeu doit être fermé. L'ancienne
  version est gardée en `SSMP.dll.bak`, et le commit installé est écrit dans `SSMP-COMMIT.txt`.
- **`envoyer-logs.cmd`** : compresse `BepInEx\LogOutput.log` et l'envoie sur le Gokapi de Papaours, via la demande
  d'upload dont le lien est dans `ssmp-jojo.config`. Le log est supprimé une fois l'envoi réussi.

S'il y a plusieurs profils Cogfly avec SSMP, les scripts demandent lequel utiliser. On peut aussi le donner :
`installer-ssmp.cmd -Profil Alliance`.

## Préparer l'envoi (Papaours)

Le lien d'upload permet à n'importe qui d'envoyer des fichiers : il n'est pas dans le dépôt.

```bash
cp tools/jojo/ssmp-jojo.config.example tools/jojo/ssmp-jojo.config   # puis y coller le lien
tools/jojo/make-bundle.sh                                            # crée tools/jojo/dist/ssmp-jojo.zip
```

Envoyer `dist/ssmp-jojo.zip` en privé. Pour récupérer les logs reçus : `tools/gokapi/recuperer-logs.sh`.

## Signaler un bug en jouant

Appuyer sur **F8** dès qu'on voit un bug (ou taper `/bug <description>` dans le chat). Juste après F8, on peut préciser avec
`/bug <description>` (dans la minute). Pas de souci si on appuie deux fois, ou si les deux joueurs signalent le même
bug : c'est regroupé. Pour signaler un autre bug juste après : `/bug nouveau <description>`. Si un bug corrigé
revient : `/bug encore <numéro d'issue>`. Un marqueur `[BUG #n]`
apparaît dans le chat et un instantané est écrit dans les logs des deux joueurs. Les logs partent tout seuls quand on est
déconnecté ou qu'on quitte le jeu, si on a joué à plusieurs. `envoyer-logs.cmd` reste disponible en secours.
