using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Encounters;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Client;
using SSMP.Util;
using TeamCherry.Localization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;
using PmFsm = HutongGames.PlayMaker.Fsm;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op story rule for arenas and bosses (PRINCIPES.md, "Boss et arènes", rule 1): an arena only starts once all
/// connected players are inside its area. Alone on the server, a player starts it as in the base game.
/// <list type="bullet">
/// <item>Wave arenas (<see cref="BattleScene"/>): the trigger of the local hero is held, and the arena starts once
/// nobody is missing. On a scene client the trigger is ignored anyway: the scene host's arena rules
/// (<see cref="EncounterComponent"/>).</item>
/// <item>Bosses that wake from their own FSM: the FSM transition into a state that starts the battle is refused
/// while someone is missing, and replayed (<c>Fsm.SetState</c>) once everyone is in. Two kinds of states are held:
/// a state that calls <c>BattleScene.StartBattle</c> (Mossbone Mother), and a state entered from a trigger state
/// (<c>Trigger2dEvent*</c>) that closes gates or sends <c>BATTLE START</c> within a few <c>FINISHED</c> steps (the
/// <c>Return Battle</c> zone of the Bell Beast). The area is the encounter's (<see cref="Encounter.Contains"/>), or
/// else the trigger's collider.</item>
/// <item>Any other <c>BattleScene.StartBattle</c> call while someone is missing is deferred: gates stay open.</item>
/// <item>Progression gates: the gates of an arena not completed that are not on the side the local hero came from
/// stay closed (see <see cref="CloseProgressionGates"/>).</item>
/// <item>A player inside an arena that waits sees an area title: "No thread shall be left behind".</item>
/// </list>
/// The decision of starting is taken by the game that runs the arena: the scene host for synced arenas. Each game
/// knows the other players of its scene and their positions, so nothing is sent over the network. Whether an
/// encounter is already underway comes from the shared notion (<see cref="Encounter.IsUnderwayOrDone"/>).
/// </summary>
internal class ArenaWait {
    /// <summary>The key given to the area title FSM, answered by <see cref="OnGetLanguageString"/>.</summary>
    private const string TitleKey = "SSMP_ARENA_WAIT";

    /// <summary>A player whose position is this far outside the area still counts as inside (trigger edges).</summary>
    private const float AreaMargin = 1.5f;

    /// <summary>Seconds between two messages while the list of missing players does not change.</summary>
    private const float MessageCooldown = 30f;

    /// <summary>Minimum seconds between two messages when the local hero enters the area again.</summary>
    private const float ReenterCooldown = 8f;

    /// <summary>Minimum seconds between two messages when the list of missing players changes.</summary>
    private const float ChangedCooldown = 4f;

    /// <summary>Seconds after entering a scene before the progression gates are evaluated.</summary>
    private const float GateEvaluationDelay = 1f;

    /// <summary>Seconds an arena stays completed before the gates closed by us are opened if the game did not.</summary>
    private const float GateReopenDelay = 5f;

    /// <summary>The events that mark a state as starting a battle (closing gates, waking a boss).</summary>
    private static readonly HashSet<string> BattleStartEvents = ["BG CLOSE", "BATTLE START"];

    /// <summary>How many states a held transition may look ahead for the start of a battle.</summary>
    private const int StartSearchDepth = 3;

    /// <summary>The instance whose hooks are registered, for the static hook methods.</summary>
    private static ArenaWait? _instance;

    private readonly NetClient _netClient;
    private readonly EntityManager _entityManager;
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;
    private readonly List<Hook> _hooks = [];

    /// <summary>The arenas of the current scene, by <see cref="BattleScene"/> or trigger collider.</summary>
    private readonly Dictionary<Object, Arena> _arenas = new();

    /// <summary>The arena each FSM transition starts, or null: computed once per transition.</summary>
    private readonly Dictionary<FsmTransition, Arena?> _transitionArenas = new();

    /// <summary>Whether we are replaying a held start ourselves: nothing is held during that.</summary>
    private bool _releasing;

    /// <summary>The small line of the area title currently shown.</summary>
    private string _titleSub = "";

    /// <summary>The coroutine that evaluates the progression gates after entering a scene.</summary>
    private Coroutine? _gateRoutine;

    public ArenaWait(NetClient netClient, EntityManager entityManager, Dictionary<ushort, ClientPlayerData> playerData) {
        _netClient = netClient;
        _entityManager = entityManager;
        _playerData = playerData;
    }

    #region Hooks

    public void RegisterHooks() {
        _instance = this;

        AddHook(typeof(BattleScene), "OnTriggerEnter2D", OnBattleSceneTrigger);
        AddHook(typeof(BattleScene), nameof(BattleScene.StartBattle), OnBattleSceneStartBattle);
        AddHook(typeof(PmFsm), "DoTransition", OnFsmDoTransition);
        AddHook(typeof(GetLanguageStringProcessed), nameof(GetLanguageStringProcessed.OnEnter), OnGetLanguageString);

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    public void DeregisterHooks() {
        foreach (var hook in _hooks) {
            hook.Dispose();
        }

        _hooks.Clear();
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        SceneManager.activeSceneChanged -= OnSceneChanged;

        ClearScene();
        if (_instance == this) {
            _instance = null;
        }
    }

    private void AddHook(Type type, string methodName, Delegate detour) {
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) {
            Logger.Error($"[ArenaWait] Could not hook {type.Name}#{methodName}: method does not exist");
            return;
        }

        try {
            _hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"[ArenaWait] Could not hook {type.Name}#{methodName}:\n{e}");
        }
    }

    /// <summary>
    /// Hook on <c>BattleScene.OnTriggerEnter2D</c>, the start of a wave arena by the local hero. On a scene client of
    /// a synced arena, the trigger is ignored (the scene host's arena rules). Otherwise, the start is held while
    /// someone is missing. Either way the arena is marked as triggered, to start once nobody is missing if this game
    /// runs it (also after becoming scene host).
    /// </summary>
    private static void OnBattleSceneTrigger(Action<BattleScene, Collider2D> orig, BattleScene self, Collider2D collision) {
        var instance = _instance;
        if (instance == null || collision == null || !collision.CompareTag("Player")) {
            orig(self, collision!);
            return;
        }

        var hero = collision.GetComponent<HeroController>();
        var wouldStart = hero != null && hero.isHeroInPosition && self.currentWave == 0 && !self.completed &&
                         !self.started && self.loopsUntilDeactivate <= 0;

        if (EncounterComponent.ShouldIgnoreTrigger(self)) {
            if (wouldStart) {
                instance.GetBattleArena(self).Triggered = true;
            }

            return;
        }

        if (wouldStart && instance.MustWait(instance.GetBattleArena(self), out var missing)) {
            var arena = instance.GetBattleArena(self);
            if (!arena.Triggered) {
                Logger.Info($"[ArenaWait] Holding the start of battle '{arena.Name}', waiting for: {Join(missing)}");
            }

            arena.Triggered = true;
            return;
        }

        orig(self, collision);
    }

    /// <summary>
    /// Hook on <see cref="BattleScene.StartBattle"/>: a start that was not held earlier (an FSM call that is not one
    /// of the held states, an enemy alert…) is deferred while someone is missing, so the gates stay open. Only on
    /// the game that runs the arena: a scene client starts its arena when the scene host's arena starts.
    /// </summary>
    private static void OnBattleSceneStartBattle(Action<BattleScene> orig, BattleScene self) {
        var instance = _instance;
        if (instance == null || instance._releasing || self.started || self.completed ||
            !instance.RunsArena(self)) {
            orig(self);
            return;
        }

        var arena = instance.GetBattleArena(self);
        if (!instance.MustWait(arena, out var missing)) {
            orig(self);
            return;
        }

        if (!arena.StartDeferred) {
            Logger.Info($"[ArenaWait] Deferring StartBattle of '{arena.Name}', waiting for: {Join(missing)}");
        }

        arena.StartDeferred = true;
    }

    /// <summary>
    /// Hook on the private <c>Fsm.DoTransition</c>: refuses a transition into a state that starts a battle while
    /// someone is missing. The FSM stays in its current state; the transition is replayed once nobody is missing.
    /// </summary>
    private static bool OnFsmDoTransition(
        Func<PmFsm, FsmTransition, bool, bool> orig,
        PmFsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        var instance = _instance;
        if (instance == null || instance._releasing || transition == null || !instance.HasOtherPlayers()) {
            return orig(self, transition!, isGlobal);
        }

        try {
            if (instance.TryHoldTransition(self, transition, isGlobal)) {
                return false;
            }
        } catch (Exception e) {
            Logger.Warn($"[ArenaWait] Could not check transition '{transition.EventName}': {e.Message}");
        }

        return orig(self, transition, isGlobal);
    }

    /// <summary>
    /// Hook on <see cref="GetLanguageStringProcessed.OnEnter"/>: gives the texts of our area title.
    /// </summary>
    private static void OnGetLanguageString(Action<GetLanguageStringProcessed> orig, GetLanguageStringProcessed self) {
        orig(self);

        var key = self.convName?.Value;
        if (key == null || !key.StartsWith(TitleKey) || self.storeValue == null) {
            return;
        }

        var french = IsFrench();
        self.storeValue.Value = key.Substring(TitleKey.Length) switch {
            "_MAIN" => french ? "Nul fil ne doit être laissé en arrière" : "No thread shall be left behind",
            "_SUB" => _instance?._titleSub ?? "",
            _ => ""
        };
    }

    #endregion

    #region Arenas

    /// <summary>
    /// An arena of the current scene: a <see cref="BattleScene"/>, or a trigger zone of a boss FSM.
    /// </summary>
    private class Arena {
        public required string Name { get; init; }

        /// <summary>The wave arena, if any.</summary>
        public BattleScene? Battle { get; init; }

        /// <summary>The object whose collider is the area when no encounter holds it: the trigger zone.</summary>
        public required Component AreaOwner { get; init; }

        /// <summary>
        /// The shared encounter: the arena's, or the boss encounter whose area holds the trigger zone (looked up
        /// each time, the boss entity may register after the zone).
        /// </summary>
        public Encounter? Encounter => Battle != null ? EncounterTracker.ForBattle(Battle) :
            AreaOwner != null ? EncounterTracker.FindBossEncounterAt(AreaOwner.transform.position) : null;

        /// <summary>Whether the position is inside the area: the encounter's, or the trigger zone's.</summary>
        public bool Contains(Vector2 position, float margin = 0f) {
            var encounter = Encounter;
            return encounter != null ? encounter.Contains(position, margin) :
                Encounter.AreaContains(AreaOwner, position, margin);
        }

        /// <summary>Whether nothing waits any more: the encounter is underway or over.</summary>
        public bool IsUnderwayOrDone => Encounter?.IsUnderwayOrDone ?? false;

        /// <summary>Whether the local hero entered the trigger of the wave arena while it could start.</summary>
        public bool Triggered { get; set; }

        /// <summary>Whether a <see cref="BattleScene.StartBattle"/> call is deferred.</summary>
        public bool StartDeferred { get; set; }

        /// <summary>The held transitions, by FSM: the state the FSM waits in and the state to enter.</summary>
        public Dictionary<PmFsm, (FsmState From, FsmState To)> Held { get; } = new();

        /// <summary>The gates closed by <see cref="CloseProgressionGates"/>.</summary>
        public List<PlayMakerFSM> ClosedGates { get; } = [];

        /// <summary>Since when the arena is completed while gates closed by us may still be closed.</summary>
        public float CompletedSince { get; set; } = -1f;

        /// <summary>Whether an FSM of the waves starts the arena (computed once).</summary>
        public bool? HasStarterFsm { get; set; }

        public bool LocalInside { get; set; }
        public string MissingKey { get; set; } = "";
        public float LastShown { get; set; } = float.NegativeInfinity;

        /// <summary>Whether this arena holds something that starts once nobody is missing.</summary>
        public bool IsHolding => Triggered || StartDeferred || Held.Count > 0;
    }

    private Arena GetBattleArena(BattleScene battleScene) {
        if (_arenas.TryGetValue(battleScene, out var arena)) {
            return arena;
        }

        arena = new Arena {
            Name = battleScene.name + " (" + battleScene.gameObject.scene.name + ")",
            Battle = battleScene,
            AreaOwner = battleScene
        };
        _arenas[battleScene] = arena;
        return arena;
    }

    private Arena GetZoneArena(Collider2D zone) {
        if (_arenas.TryGetValue(zone, out var arena)) {
            return arena;
        }

        arena = new Arena {
            Name = zone.name + " (" + zone.gameObject.scene.name + ")",
            AreaOwner = zone
        };
        _arenas[zone] = arena;
        return arena;
    }

    /// <summary>
    /// Whether this game runs the given wave arena: scene host of a synced arena, or any game for an arena without
    /// synced entities.
    /// </summary>
    private bool RunsArena(BattleScene battleScene) =>
        _entityManager.IsSceneHost || !EncounterTracker.ForBattle(battleScene).IsSynced;

    /// <summary>Whether other players are connected: alone, arenas start as in the base game.</summary>
    private bool HasOtherPlayers() => _netClient.IsConnected && _playerData.Count > 0;

    /// <summary>
    /// Whether the given arena must wait, and which players are missing: not in the scene, or outside the area.
    /// </summary>
    private bool MustWait(Arena arena, out List<string> missing) {
        missing = [];
        if (!HasOtherPlayers() || arena.AreaOwner == null) {
            return false;
        }

        if (arena.IsUnderwayOrDone) {
            return false;
        }

        var hero = HeroController.instance;
        if (hero == null || !arena.Contains(hero.transform.position, AreaMargin)) {
            missing.Add(LocalName);
        }

        foreach (var player in _playerData.Values) {
            var container = player.PlayerContainer;
            if (!player.IsInLocalScene || container == null || !container.activeInHierarchy ||
                !arena.Contains(container.transform.position, AreaMargin)) {
                missing.Add(player.Username);
            }
        }

        missing.Sort(StringComparer.OrdinalIgnoreCase);
        return missing.Count > 0;
    }

    /// <summary>The name of the local player in the logs (the message is only shown to players inside).</summary>
    private const string LocalName = "(local player)";

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    #endregion

    #region Held FSM transitions

    /// <summary>
    /// Holds the given transition if it starts a battle whose area is missing someone. Not while the local hero is
    /// in a cutscene or a dialogue (no control): holding there could freeze it. The deferred StartBattle still keeps
    /// the gates open in that case.
    /// </summary>
    private bool TryHoldTransition(PmFsm fsm, FsmTransition transition, bool isGlobal) {
        if (!_transitionArenas.TryGetValue(transition, out var arena)) {
            arena = FindStartedArena(fsm, transition, isGlobal);
            _transitionArenas[transition] = arena;
            if (arena != null) {
                Logger.Info(
                    $"[ArenaWait] Transition '{fsm.ActiveStateName}' -{transition.EventName}-> " +
                    $"'{transition.ToState}' of FSM '{fsm.Name}' on '{fsm.GameObjectName}' starts arena '{arena.Name}'"
                );
            }
        }

        if (arena == null || arena.AreaOwner == null || fsm.ActiveState == null || transition.ToFsmState == null) {
            return false;
        }

        var hero = HeroController.instance;
        if (hero != null && hero.controlReqlinquished) {
            return false;
        }

        if (!MustWait(arena, out var missing)) {
            return false;
        }

        if (!arena.Held.TryGetValue(fsm, out var held) || held.From != fsm.ActiveState ||
            held.To != transition.ToFsmState) {
            Logger.Info(
                $"[ArenaWait] Holding '{fsm.GameObjectName}' ({fsm.Name}) in state '{fsm.ActiveStateName}' " +
                $"instead of '{transition.ToState}' for arena '{arena.Name}', waiting for: {Join(missing)}"
            );
        }

        arena.Held[fsm] = (fsm.ActiveState, transition.ToFsmState);
        return true;
    }

    /// <summary>
    /// The arena that the given transition starts, if any:
    /// <list type="bullet">
    /// <item>its target state calls <c>BattleScene.StartBattle</c>: that arena;</item>
    /// <item>it leaves a trigger state and its target state (or the states after it through <c>FINISHED</c>)
    /// closes gates, sends <c>BATTLE START</c> or starts a <see cref="BattleScene"/>: the trigger's zone.</item>
    /// </list>
    /// </summary>
    private Arena? FindStartedArena(PmFsm fsm, FsmTransition transition, bool isGlobal) {
        var to = transition.ToFsmState;
        if (to == null) {
            return null;
        }

        var battleScene = FindStartBattleTarget(fsm, to);
        if (battleScene != null) {
            return GetBattleArena(battleScene);
        }

        var from = fsm.ActiveState;
        if (isGlobal || from == null) {
            return null;
        }

        foreach (var action in from.Actions) {
            if (action == null || !action.GetType().Name.StartsWith("Trigger2dEvent")) {
                continue;
            }

            var zoneObject = fsm.GameObject;
            if (action is Trigger2dEvent trigger && trigger.gameObject != null) {
                zoneObject = fsm.GetOwnerDefaultTarget(trigger.gameObject) ?? zoneObject;
            }

            var zone = zoneObject != null ? zoneObject.GetComponent<Collider2D>() : null;
            if (zone == null) {
                continue;
            }

            return StartsBattleWithin(fsm, to, StartSearchDepth) ? GetZoneArena(zone) : null;
        }

        return null;
    }

    /// <summary>The arena started by a <c>CallMethodProper BattleScene.StartBattle</c> action of the state.</summary>
    private static BattleScene? FindStartBattleTarget(PmFsm fsm, FsmState state) {
        foreach (var action in state.Actions) {
            if (action is not CallMethodProper call || call.behaviour?.Value != nameof(BattleScene) ||
                call.methodName?.Value != nameof(BattleScene.StartBattle)) {
                continue;
            }

            var target = fsm.GetOwnerDefaultTarget(call.gameObject);
            var battleScene = target != null ? target.GetComponent<BattleScene>() : null;
            if (battleScene != null) {
                return battleScene;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the state, or a state reached from it through <c>FINISHED</c> within the given depth, starts a battle.
    /// </summary>
    private static bool StartsBattleWithin(PmFsm fsm, FsmState state, int depth) {
        foreach (var action in state.Actions) {
            var sentEvent = action switch {
                SendEventByName a => a.sendEvent?.Value,
                SendEventByNameV2 a => a.sendEvent?.Value,
                SendEventByNameV3 a => a.sendEvent?.Value,
                _ => null
            };
            if (sentEvent != null && BattleStartEvents.Contains(sentEvent)) {
                return true;
            }
        }

        if (FindStartBattleTarget(fsm, state) != null) {
            return true;
        }

        if (depth <= 1) {
            return false;
        }

        foreach (var next in state.Transitions) {
            if (next.EventName == "FINISHED" && next.ToFsmState != null && next.ToFsmState != state &&
                StartsBattleWithin(fsm, next.ToFsmState, depth - 1)) {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Update: release, message, gates

    private void OnUpdate() {
        if (_arenas.Count == 0) {
            return;
        }

        foreach (var arena in _arenas.Values.ToList()) {
            try {
                UpdateArena(arena);
            } catch (Exception e) {
                Logger.Warn($"[ArenaWait] Update of arena '{arena.Name}' failed: {e.Message}");
            }
        }
    }

    private void UpdateArena(Arena arena) {
        if (arena.AreaOwner == null) {
            return;
        }

        UpdateClosedGates(arena);

        if (arena.IsUnderwayOrDone) {
            // Started some other way (or by the scene host): a held boss must not stay asleep
            if (arena.Held.Count > 0) {
                Release(arena);
            }

            arena.Triggered = false;
            arena.StartDeferred = false;
            arena.LocalInside = false;
            return;
        }

        var waiting = MustWait(arena, out var missing);
        if (!waiting) {
            if (arena.IsHolding) {
                Release(arena);
            }

            arena.LocalInside = false;
            arena.MissingKey = "";
            return;
        }

        UpdateMessage(arena, missing);
    }

    /// <summary>
    /// Replays what the arena held, once nobody is missing: the held FSM transitions, then the start of the wave arena
    /// (on the game that runs it).
    /// </summary>
    private void Release(Arena arena) {
        _releasing = true;
        try {
            foreach (var pair in arena.Held.ToList()) {
                var fsm = pair.Key;
                var (from, to) = pair.Value;
                if (fsm == null || fsm.Owner == null || fsm.ActiveState != from) {
                    Logger.Info($"[ArenaWait] Held FSM of arena '{arena.Name}' moved on by itself, not replaying it");
                    continue;
                }

                Logger.Info(
                    $"[ArenaWait] Everyone is in arena '{arena.Name}', '{fsm.GameObjectName}' ({fsm.Name}) " +
                    $"goes to state '{to.Name}'"
                );
                fsm.SetState(to.Name);
            }

            arena.Held.Clear();

            var battle = arena.Battle;
            if (battle != null && (arena.Triggered || arena.StartDeferred)) {
                if (!RunsArena(battle)) {
                    // Scene client: the scene host starts the arena and EncounterComponent follows it. The trigger stays
                    // marked, so the arena starts here if this game becomes the scene host
                    return;
                }

                if (!battle.started && !battle.completed && battle.isActiveAndEnabled &&
                    (arena.StartDeferred || battle.currentWave == 0)) {
                    Logger.Info($"[ArenaWait] Everyone is in arena '{arena.Name}', starting it");
                    battle.StartBattle();
                }
            }

            arena.Triggered = false;
            arena.StartDeferred = false;
        } finally {
            _releasing = false;
        }
    }

    /// <summary>
    /// Shows the area title to the local player inside an arena that waits: when entering the area, when the list
    /// of missing players changes, or after a cooldown.
    /// </summary>
    private void UpdateMessage(Arena arena, List<string> missing) {
        var hero = HeroController.instance;
        var inside = hero != null && arena.Contains(hero.transform.position);
        var entered = inside && !arena.LocalInside;
        arena.LocalInside = inside;

        // A wave arena shows the message while it can start (trigger active, a boss of its waves, or held); a zone
        // only while it holds
        var relevant = arena.IsHolding;
        if (!relevant && arena.Battle != null && arena.Battle.isActiveAndEnabled && arena.Battle.currentWave == 0) {
            arena.HasStarterFsm ??= HasStarterFsm(arena.Battle);
            relevant = arena.HasStarterFsm.Value || IsTriggerEnabled(arena.Battle);
        }

        if (!inside || !relevant) {
            return;
        }

        var key = Join(missing);
        var changed = key != arena.MissingKey;
        var now = Time.unscaledTime;
        var interval = changed ? ChangedCooldown : entered ? ReenterCooldown : MessageCooldown;
        if (now - arena.LastShown < interval) {
            return;
        }

        if (!ShowTitle(missing)) {
            // The area title is busy: retry on a next frame
            return;
        }

        Logger.Info($"[ArenaWait] Arena '{arena.Name}' waits, message shown, waiting for: {key}");
        arena.MissingKey = key;
        arena.LastShown = now;
    }

    private static bool IsTriggerEnabled(BattleScene battle) {
        var collider = battle.GetComponent<Collider2D>();
        return collider != null && collider.enabled;
    }

    /// <summary>
    /// Plays the game's area title (as <c>AreaTitleController.ForcePlay</c>, small "visited" title) with our key.
    /// </summary>
    private bool ShowTitle(List<string> missing) {
        var areaTitle = ManagerSingleton<AreaTitle>.Instance;
        if (areaTitle == null) {
            return false;
        }

        var titleObject = areaTitle.gameObject;
        if (titleObject.activeInHierarchy) {
            return false;
        }

        _titleSub = (IsFrench() ? "En attente de : " : "Waiting for: ") + Join(missing);

        titleObject.SetActive(true);
        var fsm = FSMUtility.GetFSM(titleObject);
        if (fsm == null) {
            titleObject.SetActive(false);
            return false;
        }

        FSMUtility.SetBool(fsm, "Visited", true);
        FSMUtility.SetBool(fsm, "NPC Title", false);
        FSMUtility.SetBool(fsm, "City Title", false);
        FSMUtility.SetBool(fsm, "Display Right", false);
        FSMUtility.SetString(fsm, "Area Event", TitleKey);
        return true;
    }

    private static bool IsFrench() {
        try {
            return Language.CurrentLanguage() == LanguageCode.FR;
        } catch (Exception) {
            return false;
        }
    }

    #endregion

    #region Scene

    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        ClearScene();
        _gateRoutine = MonoBehaviourUtil.Instance.StartCoroutine(EvaluateGatesAfterEntry());
    }

    private void ClearScene() {
        if (_gateRoutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_gateRoutine);
            _gateRoutine = null;
        }

        _arenas.Clear();
        _transitionArenas.Clear();
        _releasing = false;
        EncounterTracker.EntryPoint = null;
    }

    /// <summary>
    /// Waits for the local hero to be placed in the new scene (its entry point) and for the arenas to load their
    /// completion (save data, synced persistent values), then closes the progression gates.
    /// </summary>
    private IEnumerator EvaluateGatesAfterEntry() {
        var start = Time.time;
        while (HeroController.instance == null || !HeroController.instance.isHeroInPosition ||
               Time.time - start < GateEvaluationDelay) {
            yield return null;
        }

        _gateRoutine = null;
        Vector2 entry = HeroController.instance.transform.position;
        EncounterTracker.EntryPoint = entry;

        BattleScene[] battleScenes;
        try {
            battleScenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        } catch (Exception e) {
            Logger.Warn($"[ArenaWait] Could not list the arenas of the scene: {e.Message}");
            yield break;
        }

        foreach (var battleScene in battleScenes) {
            try {
                CloseProgressionGates(battleScene, entry);
            } catch (Exception e) {
                Logger.Warn($"[ArenaWait] Could not close the progression gates of '{battleScene.name}': {e.Message}");
            }
        }
    }

    /// <summary>
    /// Closes the progression gates of an arena that is not completed: the gates that lead further than the side the
    /// local hero came from, so nobody bypasses the arena before it starts. The rule:
    /// <list type="bullet">
    /// <item>only arenas whose completion is saved (<c>setPDBoolOnEnd</c> or a <see cref="PersistentBoolItem"/>),
    /// not completed and not started, with at least two gates;</item>
    /// <item>the entry gates are the gates closest to the entry point of the local hero in the scene (within
    /// a few units of the closest one, <see cref="Encounter.ClassifyGates"/>): they stay open;</item>
    /// <item>the other gates quick-close (<c>BG QUICK CLOSE</c>, or <c>BG CLOSE</c> if the gate does not have it);
    /// the end of the battle opens all gates (<c>BG OPEN</c>, or <c>BG QUICK OPEN</c> when completed elsewhere);</item>
    /// <item>nothing is closed if the hero entered the scene inside the arena area.</item>
    /// </list>
    /// Each game decides for its own hero: a gate only blocks the local hero.
    /// </summary>
    private void CloseProgressionGates(BattleScene battleScene, Vector2 entry) {
        if (battleScene == null || battleScene.completed || battleScene.started) {
            return;
        }

        // Known from now on, for the message
        var arena = GetBattleArena(battleScene);
        var encounter = EncounterTracker.ForBattle(battleScene);
        if (battleScene.gates == null || !encounter.IsSaved || !CanStartHere(battleScene)) {
            return;
        }

        if (encounter.Contains(entry)) {
            Logger.Info($"[ArenaWait] Entered the scene inside arena '{battleScene.name}', not closing its gates");
            return;
        }

        foreach (var (fsm, isEntry, distance, closest) in encounter.ClassifyGates(entry)) {
            if (isEntry) {
                Logger.Info($"[ArenaWait] Gate '{fsm.name}' of arena '{arena.Name}' is an entry gate, left open");
                continue;
            }

            var closeEvent = HasEvent(fsm, "BG QUICK CLOSE") ? "BG QUICK CLOSE" :
                HasEvent(fsm, "BG CLOSE") ? "BG CLOSE" : null;
            if (closeEvent == null) {
                Logger.Info($"[ArenaWait] Gate '{fsm.name}' of arena '{arena.Name}' cannot be closed, left as is");
                continue;
            }

            Logger.Info(
                $"[ArenaWait] Arena '{arena.Name}' not completed, closing progression gate '{fsm.name}' " +
                $"({distance:0.0} from the entry, entry gate at {closest:0.0})"
            );
            fsm.SendEvent(closeEvent);
            arena.ClosedGates.Add(fsm);
        }
    }

    /// <summary>
    /// Opens the progression gates closed by us if the arena is over and the game did not open them (an arena whose
    /// end does not open its gates, <c>openGatesOnEnd</c> off). The end itself is the encounter's.
    /// </summary>
    private void UpdateClosedGates(Arena arena) {
        var battle = arena.Battle;
        if (arena.ClosedGates.Count == 0 || battle == null) {
            return;
        }

        var done = battle.completed || arena.Encounter?.State == EncounterState.Ended;
        if (!done || battle.started) {
            arena.CompletedSince = -1f;
            return;
        }

        if (arena.CompletedSince < 0f) {
            arena.CompletedSince = Time.time;
            return;
        }

        if (Time.time - arena.CompletedSince < GateReopenDelay) {
            return;
        }

        foreach (var gate in arena.ClosedGates) {
            if (gate != null && gate.ActiveStateName is "Quick Close" or "Close 1" or "Close 2" or "Close Pause") {
                Logger.Info($"[ArenaWait] Arena '{arena.Name}' completed, opening progression gate '{gate.name}'");
                gate.SendEvent("BG QUICK OPEN");
            }
        }

        arena.ClosedGates.Clear();
    }

    /// <summary>
    /// Whether the arena can start now, so that closing its progression gates does not block a path the base game
    /// leaves open: its trigger is enabled (wave arena), or an FSM of its waves starts it (a boss of the arena, such
    /// as Mossbone Mother). An arena started by a story condition elsewhere is left alone.
    /// </summary>
    private static bool CanStartHere(BattleScene battleScene) =>
        IsTriggerEnabled(battleScene) || HasStarterFsm(battleScene);

    /// <summary>Whether an FSM of the arena's children (a boss of its waves) starts it.</summary>
    private static bool HasStarterFsm(BattleScene battleScene) {
        foreach (var fsm in battleScene.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm == null || fsm.Fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                if (FindStartBattleTarget(fsm.Fsm, state) == battleScene) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasEvent(PlayMakerFSM fsm, string eventName) =>
        fsm.FsmGlobalTransitions.Any(t => t.EventName == eventName) ||
        fsm.FsmStates.Any(s => s.Transitions.Any(t => t.EventName == eventName));

    #endregion
}
