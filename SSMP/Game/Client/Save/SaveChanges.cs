using System.Linq;
using SSMP.Util;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Class that handles incoming save changes that have an immediate effect in the current scene for the local player.
/// E.g. breakable walls that also break in another scene, tollgates that are being paid, stag station being bought.
/// </summary>
internal class SaveChanges {
    /// <summary>
    /// Apply a change in player data from a save update for the given name immediately. This checks whether
    /// the local player is in a scene where the changes in player data have an effect on the environment.
    /// For example, a breakable wall that also opens up in another scene or a stag station being bought.
    /// </summary>
    /// <param name="name">The name of the PlayerData entry.</param>
    public void ApplyPlayerDataSaveChange(string? name) {
        Logger.Debug($"ApplyPlayerData for name: {name}");

        if (name != null && IsGameMapVariable(name)) {
            _gameMapRefreshPending = true;
        }
    }

    /// <summary>
    /// Whether a change from another player received since the last <see cref="Update"/> changes what the game map
    /// shows.
    /// </summary>
    private bool _gameMapRefreshPending;

    /// <summary>
    /// Whether the given PlayerData entry changes what the game map shows: an area map, the quill, the rooms mapped
    /// or visited, or a pin. The game reads them in <c>GameMap.SetupMap</c>, which it only runs on its own events
    /// (buying a map, mapping at a bench, entering a new room); a value received from another player is otherwise
    /// only drawn after one of these.
    /// </summary>
    /// <param name="name">The name of the PlayerData entry.</param>
    /// <returns>true if the game map has to be set up again, false otherwise.</returns>
    private static bool IsGameMapVariable(string name) {
        // Same rule as PlayerData.MapBoolList for the area maps
        if (name.StartsWith("Has") && name.EndsWith("Map") && name != "HasAnyMap") {
            return true;
        }

        return name.StartsWith("hasPin") || name is "mapAllRooms" or "hasQuill" or "scenesMapped" or "scenesVisited"
            or "scenesEncounteredBench" or "scenesEncounteredCocoon";
    }

    /// <summary>
    /// Sets the game map up again, once for all the map changes received since the last call, as the game does
    /// for the player who bought the map (<c>ShopItem</c> calls <c>GameManager.UpdateGameMap</c>). Only the display
    /// is refreshed: it does not map the local player's visited rooms, which stays the bench's job.
    /// </summary>
    public void Update() {
        if (!_gameMapRefreshPending) {
            return;
        }

        var gm = global::GameManager.instance;
        if (!gm || !gm.gameMap) {
            // No map yet (menu, loading): the game sets it up from PlayerData when it creates it
            return;
        }

        _gameMapRefreshPending = false;
        try {
            gm.gameMap.SetupMap();
            Logger.Info("[MapSync] Map changed by another player, game map set up again");
        } catch (System.Exception e) {
            Logger.Warn($"[MapSync] Could not set up the game map again: {e}");
        }
    }

    /// <summary>
    /// Apply a change in persistent values from a save update for the given name immediately. This checks whether
    /// the local player is in a scene where the changes in player data have an effect on the environment.
    /// For example, a breakable wall that also opens up in another scene or a stag station being bought.
    /// </summary>
    /// <param name="itemKey">The persistent item key containing the ID and scene name of the changed object.</param>
    public void ApplyPersistentValueSaveChange(PersistentItemKey itemKey) {
        Logger.Debug($"ApplyPersistent for item data: {itemKey}");

        var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (itemKey.SceneName != currentScene) {
            return;
        }

        UnlockTollMachine(itemKey);
    }

    /// <summary>
    /// Opens a toll machine (bell bench toll, etc.) that another player paid in the scene the local player is in.
    /// Its 'Unlock Behaviour' FSM only checks the persistent bool when entering the scene, so without this the
    /// machine stays closed until the scene is reloaded. Plays the same retract animation as paying it, which also
    /// sets the player data that unlocks what the machine guards.
    /// </summary>
    /// <param name="itemKey">The persistent item key of the changed object.</param>
    private static void UnlockTollMachine(PersistentItemKey itemKey) {
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
            if (fsm.FsmName != "Unlock Behaviour" || fsm.gameObject.name != itemKey.Id ||
                fsm.gameObject.scene.name != itemKey.SceneName) {
                continue;
            }

            // Only from the idle state: in any other state it is already open, or the local player is paying it
            if (fsm.ActiveStateName != "Inert") {
                Logger.Info($"Toll machine '{itemKey.Id}' paid by another player, but in state '{fsm.ActiveStateName}'");
                return;
            }

            Logger.Info($"Toll machine '{itemKey.Id}' paid by another player, opening it");
            fsm.SetState("Retract Animation");
            return;
        }
    }

    /// <summary>
    /// Whether the local player is currently in a toll machine dialogue prompt that has claimed control of the
    /// character.
    /// </summary>
    /// <param name="currentStateName">The name of the current state of the dialogue FSM.</param>
    /// <returns>true if the player is in dialogue, false otherwise.</returns>
    private bool IsInTollMachineDialogue(string currentStateName) {
        string[] outOfDialogueStateNames = [
            "Out Of Range", "In Range", "Can Inspect?", "Cancel Frame", "Pause", "Activated?", "Paid?", "Get Price", "Init", 
            "Regain Control"
        ];

        return !outOfDialogueStateNames.Contains(currentStateName);
    }

    /// <summary>
    /// Hide the currently active dialogue box by setting the state of the 'Dialogue Page Control' FSM of the 'Text YN'
    /// game object. Needs to be amended if this method should also hide dialogue boxes of other dialogue types.
    /// </summary>
    // private void HideDialogueBox() {
    //     var gc = GameCameras.instance;
    //     if (gc == null) {
    //         Logger.Warn("Could not find GameCameras instance");
    //         return;
    //     }
    //
    //     var hudCamera = gc.hudCamera;
    //     if (hudCamera == null) {
    //         Logger.Warn("Could not find hudCamera");
    //         return;
    //     }
    //
    //     var dialogManager = hudCamera.gameObject.FindGameObjectInChildren("DialogueManager");
    //     if (dialogManager == null) {
    //         Logger.Warn("Could not find dialogueManager");
    //         return;
    //     }
    //
    //     void HideDialogueObject(string objectName, string heroDmgState) {
    //         var obj = dialogManager.FindGameObjectInChildren(objectName);
    //         if (obj != null) {
    //             var dialogueBox = obj.GetComponent<DialogueBox>();
    //             if (dialogueBox == null) {
    //                 Logger.Warn($"Could not find {objectName} DialogueBox");
    //                 return;
    //             }
    //
    //             var hidden = ReflectionHelper.GetField<DialogueBox, bool>(dialogueBox, "hidden");
    //             if (hidden) {
    //                 return;
    //             }
    //         
    //             var pageControlFsm = obj.LocateMyFSM("Dialogue Page Control");
    //             if (pageControlFsm == null) {
    //                 Logger.Warn($"Could not find {objectName} DialoguePageControl FSM");
    //                 return;
    //             }
    //             pageControlFsm.SetState(heroDmgState);
    //         }
    //     }
    //     
    //     HideDialogueObject("Text YN", "Hero Damaged");
    //     HideDialogueObject("Text", "Pause");
    // }
}
