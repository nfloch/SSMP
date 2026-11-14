using System;
using System.Collections.Generic;
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
    /// The ability flags whose tutorial is waiting for the local player to be free.
    /// </summary>
    private readonly Queue<string> _queue = new();

    /// <summary>
    /// The ability flag of the tutorial on screen, or null if none is shown.
    /// </summary>
    private string? _showing;

    /// <summary>
    /// The 'UI Msg Get Item' instance on screen, which does not call back when it closes.
    /// </summary>
    private GameObject? _itemMsg;

    /// <summary>
    /// The FSM 'Msg Control' of <see cref="_itemMsg"/>.
    /// </summary>
    private PlayMakerFSM? _itemMsgFsm;

    /// <summary>
    /// The value of PlayerData.isInvincible before the tutorial was shown, restored after it.
    /// </summary>
    private bool _wasInvincible;

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
            CheckItemMsgClosed();
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
            EndShowing();
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

                BeginShowing(name);
                PowerUpGetMsg.Spawn(_powerUpPrefab, tutorial.PowerUp, EndShowing);
                Logger.Info($"[AbilityTuto] Shown tutorial for '{name}' (power up {tutorial.PowerUp})");
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

                // The message only reads the tool's sprites and texts, it does not need the tool to be unlocked
                var unlocked = skill.IsUnlocked;
                BeginShowing(name);
                SkillGetMsg.Spawn(_skillPrefab, skill, EndShowing);
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
    /// Shows 'UI Msg Get Item' as the action 'CreateUIMsgGetItem' and the following 'SetFsmString' do. Unlike the
    /// two other messages, it neither blocks the hero's input nor hides the HUD: the NPC's FSM around it does.
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

        BeginShowing(name);
        _itemMsg = msg;
        _itemMsgFsm = fsm;
        GameCameras.instance.HUDOut();
        HeroController.instance.AddInputBlocker(msg);

        Logger.Info($"[AbilityTuto] Shown tutorial for '{name}' (item message '{tutorial.Name}', icon set: {iconSet})");
    }

    /// <summary>
    /// The FSM of 'UI Msg Get Item' ends in its state 'Done' (it sends 'GET ITEM MSG END' and hides itself).
    /// </summary>
    private void CheckItemMsgClosed() {
        if (_itemMsg == null && _itemMsgFsm is null) {
            return;
        }

        if (_itemMsg && _itemMsg!.activeInHierarchy && _itemMsgFsm && _itemMsgFsm!.ActiveStateName != "Done") {
            return;
        }

        EndShowing();
    }

    /// <summary>
    /// Makes the hero invincible while the tutorial is on screen, as the shrine does during its sequence: the hero
    /// cannot move while the message blocks input.
    /// </summary>
    private void BeginShowing(string name) {
        _showing = name;
        var pd = PlayerData.instance;
        _wasInvincible = pd.isInvincible;
        pd.isInvincible = true;
    }

    /// <summary>
    /// Called when the tutorial is closed (or could not be shown, or was destroyed by a scene change). PowerUpGetMsg
    /// and SkillGetMsg remove their input blocker themselves, but only bring the HUD back without a callback.
    /// </summary>
    private void EndShowing() {
        if (_showing == null) {
            return;
        }

        Logger.Info($"[AbilityTuto] Tutorial for '{_showing}' closed");
        _showing = null;
        PlayerData.instance.isInvincible = _wasInvincible;

        if (_itemMsg is not null) {
            if (HeroController.instance) {
                HeroController.instance.RemoveInputBlocker(_itemMsg);
            }

            if (_itemMsg) {
                Object.Destroy(_itemMsg);
            }

            _itemMsg = null;
            _itemMsgFsm = null;
        }

        if (GameCameras.instance) {
            GameCameras.instance.HUDIn();
        }
    }

    /// <summary>
    /// The messages are spawned in the active scene: a scene change destroys them before they call back.
    /// </summary>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        if (_showing != null) {
            Logger.Info($"[AbilityTuto] Scene changed while the tutorial for '{_showing}' was shown");
            EndShowing();
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
