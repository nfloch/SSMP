# Lire un FSM du jeu

Ces deux scripts lisent les FSM PlayMaker (comportement des ennemis, des arènes…) directement dans les fichiers du jeu
installé. Ils servent à comprendre ce qu'un ennemi fait et quelles actions SSMP doit synchroniser ou réorienter.

Aucun fichier du jeu n'est dans le dépôt : les extractions vont dans `out/`, ignoré par git.

## Installation

[mise](https://mise.jdx.dev) installe `uv`, qui installe Python et UnityPy au premier lancement :

```bash
cd tools/fsm
mise trust
mise install
```

## Utilisation

Les scènes sont dans le dossier du jeu, sous
`Hollow Knight Silksong_Data/StreamingAssets/aa/StandaloneWindows64/scenes_scenes_scenes/<scène>.bundle`.

1. Extraire le FSM d'un objet (ici le FSM `Control` de la brute au crâne de Bone_01) :

   ```bash
   mise exec -- uv run find_fsm.py "<dossier du jeu>/…/scenes_scenes_scenes/bone_01.bundle" "Bone Goomba Large" Control
   ```

   Sans nom de FSM, tous les FSM des objets trouvés sont extraits.

2. Voir les états, leurs actions et leurs transitions :

   ```bash
   mise exec -- uv run dump_actions.py out/Bone_Goomba_Large_Control_11864.json
   ```

3. Voir les paramètres des actions de certains états :

   ```bash
   mise exec -- uv run dump_actions.py out/Bone_Goomba_Large_Control_11864.json "Charge" "BG Hiding"
   ```

## Lire le résultat

- `{'useVariable': 1, 'name': 'Hero', …}` : le paramètre lit la variable `Hero`. Si elle ne fait pas partie des
  variables du FSM (`gameObjectVariables`), c'est la variable globale, c'est-à-dire le héros local. Une action qui la
  vise doit être réorientée par SSMP (`targeted_fsm` dans `SSMP/Resource/action-registry.json`).
- `{'ownerOption': 0, …}` : l'action agit sur l'objet qui porte le FSM.
- Une action absente de `SSMP/Game/Client/Entity/Action/EntityFsmActions.*.cs` n'est pas rejouée chez les clients.
