using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Shows the game's ability tutorial (the screen presenting the ability and its controls) to the local player when
/// another player acquires a shared ability during the session. The game spawns it from the FSM of the shrine, boss
/// or NPC that gives the ability, which only runs for the player who acquires it. Tutorials wait in a queue until the
/// local player is free: in game, standing on the ground, not in a cutscene, menu, dialogue, transition or another
/// message.
/// </summary>
internal class AbilityTutorials {
    /// <summary>
    /// Prefab of the Weaver abilities message (PowerUpGetMsg), spawned by the action 'SpawnPowerUpGetMsg' of the
    /// shrines ('Shrine Weaver Ability', FSM 'Inspection', state 'Powerup Msg') and of the Widow ('Spinner Boss').
    /// </summary>
    private const string PowerUpMsgAsset = "Assets/Prefabs/UI/Hornet UI/Ancestral_Art_Get_Prompt.prefab";

    /// <summary>
    /// Prefab of the silk skills message (SkillGetMsg), spawned by the action 'SpawnSkillGetMsg'.
    /// </summary>
    private const string SkillMsgAsset = "Assets/Prefabs/UI/Hornet UI/Silk_Skill_Get_Prompt.prefab";

    /// <summary>
    /// Prefab of the item message driven by its FSM 'Msg Control', spawned by the action 'CreateUIMsgGetItem' for
    /// the cloaks and the needle strike. Its string variable 'Item' selects the texts.
    /// </summary>
    private const string ItemMsgAsset = "Assets/Prefabs/UI/Messages/UI Msg Get Item.prefab";

    /// <summary>
    /// How long the local player must be free before a queued tutorial is shown, so it does not appear in the first
    /// frames after a scene transition or the end of a cutscene.
    /// </summary>
    private const float FreeDelay = 1f;

    /// <summary>
    /// How a tutorial is shown.
    /// </summary>
    private enum Kind {
        PowerUp,
        Skill,
        Item
    }

    /// <summary>
    /// A tutorial: the power up of PowerUpGetMsg, the ToolItemSkill name for SkillGetMsg, or the 'Item' value and
    /// icon sprite name for 'UI Msg Get Item'.
    /// </summary>
    private readonly record struct Tutorial(
        Kind Kind,
        PowerUpGetMsg.PowerUps PowerUp = default,
        string? Name = null,
        string? Icon = null
    );

    /// <summary>
    /// The tutorial of each shared ability flag, taken from the game's FSMs:
    /// - Weaver shrines, FSM 'Inspection', states 'Set *' then 'Powerup Msg' or 'Skill Msg';
    /// - silk skills given elsewhere: 'Phantom' (organ_01), 'Silk Needle Spell Get' (cradle_03_destroyed),
    ///   'Memory Control' (memory_first_sinner), all 'SpawnSkillGetMsg';
    /// - 'Seamstress' (bone_east_umbrella), 'DJ Get Sequence' (peak_08b), 'Pinstress Interior Ground Sit'
    ///   (room_pinstress): 'CreateUIMsgGetItem' with 'Item' set to 'Brolly', 'Double Jump', 'ChargeSlash'.
    /// </summary>
    private static readonly Dictionary<string, Tutorial> Tutorials = new() {
        { "hasDash", new Tutorial(Kind.PowerUp, PowerUpGetMsg.PowerUps.Sprint) },
        { "hasWalljump", new Tutorial(Kind.PowerUp, PowerUpGetMsg.PowerUps.WallJump) },
        { "hasHarpoonDash", new Tutorial(Kind.PowerUp, PowerUpGetMsg.PowerUps.HarpoonDash) },
        { "hasNeedolin", new Tutorial(Kind.PowerUp, PowerUpGetMsg.PowerUps.Needolin) },
        { "hasSuperJump", new Tutorial(Kind.PowerUp, PowerUpGetMsg.PowerUps.SuperJump) },
        { "hasNeedleThrow", new Tutorial(Kind.Skill, Name: "Silk Spear") },
        { "hasThreadSphere", new Tutorial(Kind.Skill, Name: "Thread Sphere") },
        { "hasSilkCharge", new Tutorial(Kind.Skill, Name: "Silk Charge") },
        { "hasSilkBomb", new Tutorial(Kind.Skill, Name: "Silk Bomb") },
        { "hasSilkBossNeedle", new Tutorial(Kind.Skill, Name: "Silk Boss Needle") },
        { "hasParry", new Tutorial(Kind.Skill, Name: "Parry") },
        { "hasBrolly", new Tutorial(Kind.Item, Name: "Brolly", Icon: "prompt_hornet_umbrella") },
        { "hasDoubleJump", new Tutorial(Kind.Item, Name: "Double Jump", Icon: "Hornet_Double_Jump_Prompt") },
        { "hasChargeSlash", new Tutorial(Kind.Item, Name: "ChargeSlash", Icon: "prompt_nail_art") }
    };

    /// <summary>
    /// How long a PowerUpGetMsg or SkillGetMsg may go without its coroutine running before the watchdog considers it
    /// stopped (an exception in the game's code, or the message disabled), in unscaled seconds. The coroutine runs
    /// every frame while it waits for the player, and at most a few seconds pass between its animation steps.
    /// </summary>
    private const float StallTimeout = 5f;

    /// <summary>
    /// How long any tutorial may stay on screen before the watchdog closes it, in unscaled seconds. The messages wait
    /// for the player to press a button, this only ends one that never reacts.
    /// </summary>
    private const float MaxShowTime = 120f;

    /// <summary>
    /// The ability flags whose tutorial is waiting for the local player to be free.
    /// </summary>
    private readonly Queue<string> _queue = new();

    /// <summary>
    /// The ability flag of the tutorial on screen, or null if none is shown.
    /// </summary>
    private string? _showing;

    /// <summary>
    /// Incremented for each tutorial shown, so a late callback of an earlier message does nothing.
    /// </summary>
    private int _generation;

    /// <summary>
    /// The message instance on screen.
    /// </summary>
    private GameObject? _msg;

    /// <summary>
    /// The object given to HeroController.AddInputBlocker for the message on screen.
    /// </summary>
    private object? _blocker;

    /// <summary>
    /// The PowerUpGetMsg or SkillGetMsg on screen, which registers itself as a world rumble preventer and pauses
    /// pickups and the inventory input while it runs (UIMsgProxy.SetIsInMsg). Null for 'UI Msg Get Item'.
    /// </summary>
    private UIMsgProxy? _proxy;

    /// <summary>
    /// The FSM 'Msg Control' of the 'UI Msg Get Item' on screen, which does not call back when it closes.
    /// </summary>
    private PlayMakerFSM? _itemMsgFsm;

    /// <summary>
    /// The unscaled time the tutorial on screen was shown, and the last time its coroutine ran.
    /// </summary>
    private float _shownAt;

    private float _lastStep;

    /// <summary>
    /// The values of PlayerData.isInvincible and PlayerData.disablePause before the tutorial was shown, restored
    /// after it.
    /// </summary>
    private bool _wasInvincible;

    private bool _wasDisablePause;

    /// <summary>
    /// The unscaled time since which the local player is free, or null if they are not.
    /// </summary>
    private float? _freeSince;

    /// <summary>
    /// The prefabs, found once.
    /// </summary>
    private PowerUpGetMsg? _powerUpPrefab;

    private SkillGetMsg? _skillPrefab;

    private GameObject? _itemPrefab;

    public void RegisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    public void DeregisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        SceneManager.activeSceneChanged -= OnSceneChanged;

        if (_showing != null) {
            EndShowing("SSMP hooks removed", failed: true);
        }
    }

    /// <summary>
    /// Called when a save update from the network turns a PlayerData bool from false to true during the session
    /// (not for the save data applied when joining). The server does not send a player's own updates back to them.
    /// Queues the tutorial if it is an ability that has one.
    /// </summary>
    /// <param name="name">The name of the PlayerData bool.</param>
    public void OnRemoteBoolAcquired(string name) {
        if (!Tutorials.ContainsKey(name)) {
            if (name.StartsWith("has") &&
                SaveDataMapping.Instance.PlayerDataVarProperties.TryGetValue(name, out var props) &&
                props.SyncType == SaveDataMapping.SyncType.Server) {
                Logger.Info($"[AbilityTuto] Received '{name}' from another player, the game has no tutorial for it");
            }

            return;
        }

        Logger.Info($"[AbilityTuto] Received '{name}' acquired by another player");

        if (_queue.Contains(name) || _showing == name) {
            return;
        }

        _queue.Enqueue(name);
        Logger.Info($"[AbilityTuto] Queued tutorial for '{name}' ({_queue.Count} waiting)");
    }

    private void OnUpdate() {
        if (_showing != null) {
            Watch();
            return;
        }

        if (_queue.Count == 0) {
            return;
        }

        var gm = global::GameManager.instance;
        if (gm && gm.GameState == GameState.MAIN_MENU) {
            Logger.Info($"[AbilityTuto] Back to the main menu, dropping {_queue.Count} queued tutorial(s)");
            _queue.Clear();
            _freeSince = null;
            return;
        }

        if (!IsPlayerFree()) {
            _freeSince = null;
            return;
        }

        _freeSince ??= Time.unscaledTime;
        if (Time.unscaledTime - _freeSince < FreeDelay) {
            return;
        }

        _freeSince = null;
        var name = _queue.Dequeue();
        try {
            Show(name);
        } catch (Exception e) {
            Logger.Warn($"[AbilityTuto] Could not show tutorial for '{name}':\n{e}");
            EndShowing("error while showing", failed: true);
        }
    }

    /// <summary>
    /// The tutorial blocks input and leaves the hero standing where they are, so only show it when nothing else
    /// controls the hero or the screen.
    /// </summary>
    private static bool IsPlayerFree() {
        var gm = global::GameManager.instance;
        if (!gm || gm.GameState != GameState.PLAYING || gm.IsInSceneTransition || gm.isPaused || gm.RespawningHero) {
            return false;
        }

        var hc = HeroController.instance;
        if (!hc || !GameCameras.instance) {
            return false;
        }

        var pd = PlayerData.instance;
        if (hc.cState.dead || hc.cState.isInCutsceneMovement || pd.disablePause || pd.isInventoryOpen) {
            return false;
        }

        // On the ground, idle or running, accepting input, not relinquished to a cutscene, not transitioning
        if (!hc.CanInteract() || hc.IsInputBlocked()) {
            return false;
        }

        return InteractManager.BlockingInteractable == null && !GenericMessageCanvas.IsActive;
    }

    private void Show(string name) {
        var tutorial = Tutorials[name];

        switch (tutorial.Kind) {
            case Kind.PowerUp: {
                _powerUpPrefab ??= FindPrefab(PowerUpMsgAsset)?.GetComponent<PowerUpGetMsg>();
                if (!_powerUpPrefab) {
                    Logger.Warn($"[AbilityTuto] Prefab '{PowerUpMsgAsset}' not found, skipping tutorial for '{name}'");
                    return;
                }

                // As PowerUpGetMsg.Spawn, and its callback selects the power up in the inventory
                PlayerData.instance.InvPaneHasNew = true;
                var powerUp = tutorial.PowerUp;
                var shown = ShowUIMsg(name, _powerUpPrefab!, powerUp, () => {
                    if (Enum.TryParse<InventoryCollectableItemSelectionHelper.SelectionType>(
                            powerUp.ToString(), out var selection
                        )) {
                        InventoryCollectableItemSelectionHelper.LastSelectionUpdate = selection;
                    }
                });
                if (!shown) {
                    return;
                }

                Logger.Info($"[AbilityTuto] Shown tutorial for '{name}' (power up {powerUp})");
                return;
            }
            case Kind.Skill: {
                if (ToolItemManager.GetToolByName(tutorial.Name) is not ToolItemSkill skill) {
                    Logger.Warn($"[AbilityTuto] Silk skill '{tutorial.Name}' not found, skipping tutorial for '{name}'");
                    return;
                }

                _skillPrefab ??= FindPrefab(SkillMsgAsset)?.GetComponent<SkillGetMsg>();
                if (!_skillPrefab) {
                    Logger.Warn($"[AbilityTuto] Prefab '{SkillMsgAsset}' not found, skipping tutorial for '{name}'");
                    return;
                }

                EnsureCrestSlotData();

                // The message only reads the tool's sprites and texts, it does not need the tool to be unlocked.
                // As SkillGetMsg.Spawn.
                var unlocked = skill.IsUnlocked;
                PlayerData.instance.ToolPaneHasNew = true;
                if (!ShowUIMsg(name, _skillPrefab!, skill, null)) {
                    return;
                }

                Logger.Info(
                    $"[AbilityTuto] Shown tutorial for '{name}' (silk skill '{tutorial.Name}', " +
                    $"tool unlocked locally: {unlocked})"
                );
                return;
            }
            case Kind.Item: {
                _itemPrefab ??= FindPrefab(ItemMsgAsset);
                if (!_itemPrefab) {
                    Logger.Warn($"[AbilityTuto] Prefab '{ItemMsgAsset}' not found, skipping tutorial for '{name}'");
                    return;
                }

                ShowItemMsg(name, tutorial);
                return;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// SkillGetMsg.Setup reads the equipped tools of the current crest (to show where the skill goes) without
    /// checking that the crest has slot data. A crest on which nothing was ever equipped has none: the game's
    /// shrines and NPCs equip the skill (action 'AutoEquipTool') before spawning the message, so the game never hits
    /// it, but a player who receives the skill from another player does. An empty slot list means the same as no
    /// slot data: nothing equipped.
    /// </summary>
    private static void EnsureCrestSlotData() {
        var crestId = PlayerData.instance.CurrentCrestID;
        var crest = ToolItemManager.GetCrestByName(crestId);
        if (!crest) {
            return;
        }

        var data = crest.SaveData;
        if (data.Slots != null) {
            return;
        }

        data.Slots = [];
        crest.SaveData = data;
        Logger.Info($"[AbilityTuto] Crest '{crestId}' had no slot data, set an empty one for the silk skill message");
    }

    /// <summary>
    /// Shows a PowerUpGetMsg or SkillGetMsg as their Spawn methods do (UIMsgBase.Spawn, then the HUD hidden and the
    /// hero's input blocked), but runs the game's coroutine 'DoMsg' inside <see cref="RunGuarded"/>, which notices
    /// when it throws: started by the game's Spawn, an exception in the coroutine (as in SkillGetMsg.Setup) was only
    /// logged by Unity, the message never called back and the hero stayed blocked.
    /// </summary>
    /// <returns>Whether the message is on screen: its first step, which runs at once, did not fail.</returns>
    private bool ShowUIMsg<T>(string name, UIMsgBase<T> prefab, T item, Action? onClosed) {
        var doMsg = typeof(UIMsgBase<T>).GetMethod("DoMsg", BindingFlags.Instance | BindingFlags.NonPublic);
        if (doMsg == null) {
            Logger.Warn($"[AbilityTuto] UIMsgBase.DoMsg not found, skipping tutorial for '{name}'");
            return false;
        }

        var msg = Object.Instantiate(prefab);
        var position = msg.transform.localPosition;
        msg.transform.localPosition = new Vector3(0f, 0f, position.z);

        var generation = BeginShowing(name, msg.gameObject, msg);
        _proxy = msg;

        Action afterMsg = () => {
            if (generation != _generation) {
                return;
            }

            try {
                onClosed?.Invoke();
            } catch (Exception e) {
                Logger.Warn($"[AbilityTuto] Error after the tutorial for '{name}' closed:\n{e}");
            }

            EndShowing("closed by the player", failed: false);
        };

        var routine = (IEnumerator) doMsg.Invoke(msg, [item, afterMsg]);
        msg.StartCoroutine(RunGuarded(generation, name, routine));
        return generation == _generation;
    }

    /// <summary>
    /// Runs the game's message coroutine step by step, records that it is alive for the watchdog, and restores
    /// everything if a step throws.
    /// </summary>
    private IEnumerator RunGuarded(int generation, string name, IEnumerator routine) {
        while (true) {
            object? current;
            try {
                if (!routine.MoveNext()) {
                    yield break;
                }

                current = routine.Current;
            } catch (Exception e) {
                Logger.Warn($"[AbilityTuto] The game's message for '{name}' failed:\n{e}");
                if (generation == _generation) {
                    EndShowing("error in the game's message", failed: true);
                }

                yield break;
            }

            if (generation == _generation) {
                _lastStep = Time.unscaledTime;
            }

            yield return current;
        }
    }

    /// <summary>
    /// Shows 'UI Msg Get Item' as the action 'CreateUIMsgGetItem' and the following 'SetFsmString' do. Unlike the
    /// two other messages, it neither blocks the hero's input nor hides the HUD: the NPC's FSM around it does, so
    /// <see cref="BeginShowing"/> does it here.
    /// </summary>
    private void ShowItemMsg(string name, Tutorial tutorial) {
        var msg = Object.Instantiate(_itemPrefab!, Vector3.zero, Quaternion.identity);
        var fsm = Array.Find(msg.GetComponents<PlayMakerFSM>(), f => f.FsmName == "Msg Control");
        if (!fsm) {
            Object.Destroy(msg);
            Logger.Warn($"[AbilityTuto] No FSM 'Msg Control' on '{ItemMsgAsset}', skipping tutorial for '{name}'");
            return;
        }

        // Set before the FSM starts, which happens on its first frame
        fsm.FsmVariables.GetFsmString("Item").Value = tutorial.Name;

        var iconSet = false;
        var sprite = FindSprite(tutorial.Icon!);
        var icon = msg.transform.Find("Icon");
        if (sprite && icon && icon.TryGetComponent<SpriteRenderer>(out var renderer)) {
            renderer.sprite = sprite;
            iconSet = true;
        }

        BeginShowing(name, msg, msg);
        _itemMsgFsm = fsm;

        Logger.Info($"[AbilityTuto] Shown tutorial for '{name}' (item message '{tutorial.Name}', icon set: {iconSet})");
    }

    /// <summary>
    /// Takes control while the tutorial is on screen: HUD hidden, hero's input blocked, and the hero invincible as the
    /// shrine makes them during its sequence, since they cannot move. From here, <see cref="EndShowing"/> is
    /// guaranteed to run: when the message closes, throws, disappears, stops, times out or the scene changes.
    /// </summary>
    /// <returns>The generation of this tutorial.</returns>
    private int BeginShowing(string name, GameObject msg, object blocker) {
        _generation++;
        _showing = name;
        _msg = msg;
        _blocker = blocker;
        _proxy = null;
        _itemMsgFsm = null;
        _shownAt = _lastStep = Time.unscaledTime;

        var pd = PlayerData.instance;
        _wasInvincible = pd.isInvincible;
        _wasDisablePause = pd.disablePause;
        pd.isInvincible = true;

        GameCameras.instance.HUDOut();
        HeroController.instance.AddInputBlocker(blocker);

        return _generation;
    }

    /// <summary>
    /// The watchdog, each frame while a tutorial is shown: ends it when the message is gone, its coroutine stopped
    /// running, 'UI Msg Get Item' reached its state 'Done', or it has been on screen too long.
    /// </summary>
    private void Watch() {
        var now = Time.unscaledTime;

        // 'UI Msg Get Item' hides itself in 'Done'
        if (_itemMsgFsm && _msg && _itemMsgFsm!.ActiveStateName == "Done") {
            EndShowing("closed by the player", failed: false);
            return;
        }

        if (!_msg || !_msg!.activeInHierarchy) {
            EndShowing("the message disappeared without closing", failed: true);
            return;
        }

        if (_itemMsgFsm is not null) {
            if (!_itemMsgFsm) {
                EndShowing("the message lost its FSM", failed: true);
                return;
            }
        } else if (Time.timeScale <= 0f) {
            // WaitForSeconds does not advance while the game is frozen
            _lastStep = now;
        } else if (now - _lastStep > StallTimeout) {
            EndShowing($"the message stopped running for {StallTimeout:0} s (watchdog)", failed: true);
            return;
        }

        if (now - _shownAt > MaxShowTime) {
            EndShowing($"still on screen after {MaxShowTime:0} s (watchdog)", failed: true);
        }
    }

    /// <summary>
    /// Ends the tutorial on screen and gives everything back: hero's input, HUD, invincibility. When the message did
    /// not close normally, also undoes what UIMsgProxy.SetIsInMsg(true) did (pause disabled, pickups and inventory
    /// input paused, world rumble prevented), fades the vibration back in, and destroys the message. Each step is
    /// guarded so one failing cannot skip the others.
    /// </summary>
    /// <param name="reason">Why the tutorial ends, for the log.</param>
    /// <param name="failed">Whether the message did not close normally.</param>
    private void EndShowing(string reason, bool failed) {
        if (_showing == null) {
            return;
        }

        var name = _showing;
        _showing = null;
        _generation++;

        if (failed) {
            Logger.Warn($"[AbilityTuto] Tutorial for '{name}' ended abnormally: {reason}, restoring control");
        } else {
            Logger.Info($"[AbilityTuto] Tutorial for '{name}' closed");
        }

        var msg = _msg;
        var blocker = _blocker;
        var proxy = _proxy;
        _msg = null;
        _blocker = null;
        _proxy = null;
        _itemMsgFsm = null;

        var ok = true;
        void Step(string what, Action action) {
            try {
                action();
            } catch (Exception e) {
                ok = false;
                Logger.Warn($"[AbilityTuto] Could not restore {what}:\n{e}");
            }
        }

        Step("the hero's input", () => {
            if (blocker != null && HeroController.instance) {
                HeroController.instance.RemoveInputBlocker(blocker);
            }
        });
        Step("invincibility", () => PlayerData.instance.isInvincible = _wasInvincible);
        Step("the HUD", () => {
            if (GameCameras.instance) {
                GameCameras.instance.HUDIn();
            }
        });

        if (failed) {
            if (proxy is not null) {
                Step("the message state", () => {
                    var cameras = GameCameras.SilentInstance;
                    if (cameras && cameras.worldRumbleManager) {
                        cameras.worldRumbleManager.RemovePreventer(proxy);
                    }

                    CollectableItemPickup.IsPickupPaused = false;
                    InventoryPaneInput.IsInputBlocked = false;
                });
                Step("the vibration", () => VibrationManager.FadeVibration(1f, 0.25f));
            }

            Step("the pause", () => PlayerData.instance.disablePause = _wasDisablePause);
        }

        Step("the message", () => {
            if (msg) {
                Object.Destroy(msg);
            }
        });

        if (failed) {
            Logger.Info(
                $"[AbilityTuto] Restored control, HUD and invincibility after the tutorial for '{name}'" +
                (ok ? "" : " (some steps failed, see above)")
            );
        }
    }

    /// <summary>
    /// The messages are spawned in the active scene: a scene change destroys them before they call back.
    /// </summary>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        if (_showing != null) {
            EndShowing("the scene changed", failed: true);
        }
    }

    /// <summary>
    /// Finds a message prefab in the addressable bundle 'prompts_assets_all', which the game keeps loaded for its
    /// global UI settings (they reference 'Tool Tutorial Msg' from the same bundle). Its loaded name may be a hash, so
    /// the bundle is found by its content.
    /// </summary>
    private static GameObject? FindPrefab(string assetPath) {
        // Make sure the global UI settings, and with them the prompts bundle, are loaded
        _ = GlobalSettings.UI.ToolTutorialMsgPrefab;

        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles()) {
            if (!bundle || bundle.isStreamedSceneAssetBundle || !bundle.Contains(assetPath)) {
                continue;
            }

            var prefab = bundle.LoadAsset<GameObject>(assetPath);
            if (prefab) {
                Logger.Info($"[AbilityTuto] Loaded prefab '{assetPath}' from bundle '{bundle.name}'");
                return prefab;
            }
        }

        // Otherwise a copy already in memory, from a scene that uses it
        var prefabName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>()) {
            // Prefab assets are in no scene, instances are
            if (gameObject.name == prefabName && !gameObject.scene.IsValid() && !gameObject.transform.parent) {
                Logger.Info($"[AbilityTuto] Found prefab '{prefabName}' in memory");
                return gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds an icon sprite of the 'prompts' sprite atlas. Its bundle has no addressable entry to load it by name, so
    /// it is only found if already in memory.
    /// </summary>
    private static Sprite? FindSprite(string spriteName) {
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>()) {
            if (sprite.name == spriteName) {
                return sprite;
            }
        }

        Logger.Info($"[AbilityTuto] Icon sprite '{spriteName}' not found, keeping the message's default icon");
        return null;
    }
}
