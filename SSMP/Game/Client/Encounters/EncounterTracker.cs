using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;
using PmFsm = HutongGames.PlayMaker.Fsm;

namespace SSMP.Game.Client.Encounters;

/// <summary>
/// The encounters of the loaded scenes (<see cref="Encounter"/>), and what every game observes about them:
/// <list type="bullet">
/// <item>the gates of boss encounters closed and opened by the game's FSMs (hook on <c>Fsm.DoTransition</c>), which
/// the scene host turns into the start and the end of the encounter;</item>
/// <item>the local end of an arena (<c>BattleScene.DoEndBattle</c>, also the <c>[BattleDiag]</c> logs);</item>
/// <item>the end of an encounter in this game, announced once by <see cref="Ending"/> before the game plays its end
/// sequence when it can (a downed player comes back on the spot to live it);</item>
/// <item>the entry point of the local hero in the scene (entry and progression gates).</item>
/// </list>
/// </summary>
internal static class EncounterTracker {
    /// <summary>The events that close a gate.</summary>
    private static readonly HashSet<string> CloseEvents = ["BG CLOSE", "BG QUICK CLOSE"];

    /// <summary>The events that open a gate.</summary>
    private static readonly HashSet<string> OpenEvents = ["BG OPEN", "BG QUICK OPEN"];

    /// <summary>The encounters, by <see cref="BattleScene"/> or by the first gate of a boss encounter.</summary>
    private static readonly Dictionary<Object, Encounter> ByKey = new();

    /// <summary>The boss encounter of each boss host object.</summary>
    private static readonly Dictionary<GameObject, Encounter> ByBoss = new();

    /// <summary>The gates of the boss encounters, for the transition hook.</summary>
    private static readonly Dictionary<PlayMakerFSM, Encounter> ByGate = new();

    /// <summary>The gates closed in this game (last event: close).</summary>
    private static readonly HashSet<PlayMakerFSM> ClosedGates = [];

    private static readonly List<Hook> Hooks = [];

    /// <summary>Depth of the gate operations done by SSMP itself in this game: not decisions of the game's FSMs.</summary>
    private static int _localGateOperations;

    /// <summary>Called once per encounter when it ends in this game, before the end sequence plays when possible.</summary>
    public static event Action<Encounter>? Ending;

    /// <summary>Where the local hero entered the current scene, once placed (set by <see cref="ArenaWait"/>).</summary>
    public static Vector2? EntryPoint { get; set; }

    #region Hooks

    public static void RegisterHooks() {
        AddHook(typeof(PmFsm), "DoTransition", OnFsmDoTransition);
        AddHook(typeof(BattleScene), nameof(BattleScene.StartBattle), OnBattleSceneStartBattle);
        AddHook(typeof(BattleScene), nameof(BattleScene.DoEndBattle), OnBattleSceneDoEndBattle);
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    public static void DeregisterHooks() {
        foreach (var hook in Hooks) {
            hook.Dispose();
        }

        Hooks.Clear();
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }

    private static void AddHook(Type type, string methodName, Delegate detour) {
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) {
            Logger.Error($"[Encounter] Could not hook {type.Name}#{methodName}: method does not exist");
            return;
        }

        try {
            Hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"[Encounter] Could not hook {type.Name}#{methodName}:\n{e}");
        }
    }

    /// <summary>
    /// Hook on the private <c>Fsm.DoTransition</c>: follows the gates of the boss encounters, closed or opened by the
    /// game's FSMs (not by SSMP's own local gate operations).
    /// </summary>
    private static bool OnFsmDoTransition(
        Func<PmFsm, FsmTransition, bool, bool> orig,
        PmFsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        var done = orig(self, transition, isGlobal);
        if (!done || transition == null) {
            return done;
        }

        try {
            var eventName = transition.EventName;
            var closing = CloseEvents.Contains(eventName);
            if (!closing && !OpenEvents.Contains(eventName)) {
                return true;
            }

            var gate = self.Owner as PlayMakerFSM;
            if (gate == null) {
                return true;
            }

            if (closing) {
                ClosedGates.Add(gate);
                if (_localGateOperations > 0) {
                    OpenedLocally.Remove(gate);
                }
            } else {
                ClosedGates.Remove(gate);
            }

            if (_localGateOperations > 0 || !closing && OpenedLocally.Contains(gate) ||
                !ByGate.TryGetValue(gate, out var encounter)) {
                return true;
            }

            if (closing && !encounter.GatesClosedByGame) {
                Logger.Info($"[Encounter] Gate '{gate.name}' of boss encounter '{encounter.Name}' closed by the game");
                encounter.GatesClosedByGame = true;
            } else if (!closing && encounter.GatesClosedByGame && !encounter.GatesOpenedByGame) {
                Logger.Info($"[Encounter] Gate '{gate.name}' of boss encounter '{encounter.Name}' opened by the game");
                encounter.GatesOpenedByGame = true;
                RaiseEnding(encounter, "gates opened by the game");
            }
        } catch (Exception e) {
            Logger.Warn($"[Encounter] Could not follow transition '{transition.EventName}': {e.Message}");
        }

        return true;
    }

    /// <summary>Hook on <see cref="BattleScene.StartBattle"/>: diagnostic only.</summary>
    private static void OnBattleSceneStartBattle(Action<BattleScene> orig, BattleScene self) {
        try {
            Logger.Info(
                $"[BattleDiag] StartBattle '{GetPath(self.transform)}' in '{self.gameObject.scene.name}': " +
                $"started={self.started}, completed={self.completed}, wave={self.currentWave}, " +
                $"startEvent='{self.battleStartEventRegister}', endEvent='{self.battleEndEventRegister}'"
            );
        } catch (Exception e) {
            Logger.Warn($"Battle diagnostic failed on start: {e.Message}");
        }

        orig(self);
    }

    /// <summary>
    /// Hook on <see cref="BattleScene.DoEndBattle"/>: the arena ends in this game. On a scene client of a synced
    /// arena, the end follows the scene host (PRINCIPES "Boss et arènes" 7): an end from the local waves (the client
    /// copies die after the scene host's enemies, but the local count can be off) is refused until the scene host's
    /// arena is over, which ends it through <see cref="Encounter.EndLocally"/>. Announces the end before the game
    /// plays it.
    /// </summary>
    private static void OnBattleSceneDoEndBattle(Action<BattleScene> orig, BattleScene self) {
        try {
            Logger.Info(
                $"[BattleDiag] DoEndBattle '{GetPath(self.transform)}' in '{self.gameObject.scene.name}': " +
                $"started={self.started}, completed={self.completed}, wave={self.currentWave}"
            );

            var encounter = ForBattle(self);
            if (!self.completed && !_endingFromHost && encounter.IsSynced && encounter.Carriers[0].IsControlled &&
                encounter.State != EncounterState.Ended) {
                if (!encounter.LoggedRefusedEnd) {
                    Logger.Info(
                        $"[Encounter] Battle '{self.name}' would end from its local waves on a scene client, " +
                        "waiting for the scene host's end"
                    );
                    encounter.LoggedRefusedEnd = true;
                }

                return;
            }

            if (!self.completed) {
                RaiseEnding(encounter, "arena ending in this game");
            }
        } catch (Exception e) {
            Logger.Warn($"Battle diagnostic failed on end: {e.Message}");
        }

        orig(self);
    }

    /// <summary>Whether <see cref="EndFromHost"/> is ending an arena.</summary>
    private static bool _endingFromHost;

    /// <summary>Runs the end of an arena decided by the scene host (or the shared save) in this game.</summary>
    public static void EndFromHost(BattleScene battle) {
        _endingFromHost = true;
        try {
            battle.DoEndBattle();
        } finally {
            _endingFromHost = false;
        }
    }

    private static string GetPath(Transform transform) {
        var path = transform.name;
        for (var parent = transform.parent; parent != null; parent = parent.parent) {
            path = parent.name + "/" + path;
        }

        return path;
    }

    #endregion

    #region Encounters

    /// <summary>The encounter of the given arena, created if needed.</summary>
    public static Encounter ForBattle(BattleScene battle) {
        if (ByKey.TryGetValue(battle, out var encounter)) {
            return encounter;
        }

        encounter = Encounter.ForBattle(battle);
        ByKey[battle] = encounter;
        return encounter;
    }

    /// <summary>
    /// The encounter of a boss that is not part of an arena, created if needed: the gates of its room are the FSMs of
    /// the scene that listen to both <c>BG CLOSE</c> and <c>BG OPEN</c>, outside any <see cref="BattleScene"/>, and
    /// at least two of them must frame the boss horizontally. Null if there are none: the fight has no area, deaths
    /// there stay vanilla.
    /// </summary>
    public static Encounter? ForBoss(GameObject boss) {
        if (ByBoss.TryGetValue(boss, out var known)) {
            return known;
        }

        var gates = FindBossGates(boss);
        if (gates.Count < 2) {
            Logger.Info($"[Encounter] Boss '{boss.name}' in '{boss.scene.name}' has no gates of its own, no encounter");
            return null;
        }

        var key = gates.OrderBy(g => g.GetInstanceID()).First();
        if (!ByKey.TryGetValue(key, out var encounter)) {
            var area = Encounter.GateSpan(gates);
            encounter = Encounter.ForBossGates(boss, gates, area);
            ByKey[key] = encounter;
            foreach (var gate in gates) {
                ByGate[gate] = encounter;
            }

            Logger.Info(
                $"[Encounter] Boss encounter '{encounter.Name}': gates " +
                $"{string.Join(", ", gates.Select(g => $"'{g.name}'"))}, area {area}"
            );
        }

        encounter.Bosses.Add(boss);
        ByBoss[boss] = encounter;
        return encounter;
    }

    private static List<PlayMakerFSM> FindBossGates(GameObject boss) {
        var battleGates = new HashSet<Transform>();
        foreach (var battle in Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (battle.gates != null) {
                battleGates.Add(battle.gates.transform);
            }
        }

        var gates = new List<PlayMakerFSM>();
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
            if (fsm == null || fsm.gameObject.scene != boss.scene || fsm.Fsm == null ||
                fsm.transform.parent != null && battleGates.Contains(fsm.transform.parent) ||
                fsm.GetComponent<Collider2D>() == null || !HasEvent(fsm, "BG CLOSE") || !HasEvent(fsm, "BG OPEN")) {
                continue;
            }

            gates.Add(fsm);
        }

        if (gates.Count < 2) {
            return gates;
        }

        // The gates must frame the boss horizontally (it may start below the floor, as the Bell Beast)
        var span = Encounter.GateSpan(gates);
        var x = boss.transform.position.x;
        return x >= span.xMin && x <= span.xMax ? gates : [];
    }

    private static bool HasEvent(PlayMakerFSM fsm, string eventName) =>
        fsm.FsmGlobalTransitions.Any(t => t.EventName == eventName) ||
        fsm.FsmStates.Any(s => s.Transitions.Any(t => t.EventName == eventName));

    /// <summary>The live encounters.</summary>
    public static IEnumerable<Encounter> All => ByKey.Values.Where(e => !e.IsDestroyed);

    /// <summary>The encounter in progress whose area holds the given position, if any.</summary>
    public static Encounter? FindInProgressAt(Vector2 position) =>
        All.FirstOrDefault(e => e.IsInProgress && e.Contains(position));

    /// <summary>The boss encounter whose area holds the given position, if any (a boss's trigger zone).</summary>
    public static Encounter? FindBossEncounterAt(Vector2 position) =>
        All.FirstOrDefault(e => e.Kind == EncounterKind.BossGates && e.Contains(position));

    /// <summary>Whether the given position is inside the area of a synced encounter, in progress or not.</summary>
    public static bool IsInsideAny(Vector2 position) => All.Any(e => e.IsSynced && e.Contains(position));

    /// <summary>Whether the given gate is closed in this game (last gate event seen).</summary>
    public static bool IsGateClosed(PlayMakerFSM gate) => ClosedGates.Contains(gate);

    /// <summary>
    /// Opens a gate in this game for SSMP's own reasons (re-entry of a downed player). The gate stays marked until
    /// SSMP closes it again, so its opening is never taken for the end of the boss fight, even if the gate's FSM
    /// handles the event later.
    /// </summary>
    public static void OpenGateLocally(PlayMakerFSM gate) {
        OpenedLocally.Add(gate);
        using var _ = LocalGateOperation();
        gate.SendEvent("BG OPEN");
    }

    /// <summary>Records a gate closed by SSMP through a state change (no transition to see).</summary>
    public static void MarkGateClosedLocally(PlayMakerFSM gate) {
        ClosedGates.Add(gate);
        OpenedLocally.Remove(gate);
    }

    /// <summary>The gates opened by SSMP itself and not closed again since.</summary>
    private static readonly HashSet<PlayMakerFSM> OpenedLocally = [];

    /// <summary>
    /// Marks the gate operations done by SSMP in this game (re-entry of a downed player, late arrival): they are not
    /// the start or the end of the boss encounter.
    /// </summary>
    public static IDisposable LocalGateOperation() {
        _localGateOperations++;
        return new LocalGateScope();
    }

    private sealed class LocalGateScope : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _localGateOperations--;
        }
    }

    /// <summary>
    /// Announces the end of the encounter in this game, once: a downed player comes back on the spot before the end
    /// sequence (rewards, gates) plays.
    /// </summary>
    public static void RaiseEnding(Encounter encounter, string reason) {
        if (encounter.EndingRaised) {
            return;
        }

        encounter.EndingRaised = true;
        Logger.Info($"[Encounter] Encounter '{encounter.Name}' is ending in this game ({reason})");
        try {
            Ending?.Invoke(encounter);
        } catch (Exception e) {
            Logger.Warn($"[Encounter] Ending handler failed: {e}");
        }
    }

    /// <summary>Forgets a carrier (entity destroyed), and the encounter with it if it has no object left.</summary>
    public static void Forget(GameObject? boss) {
        if (boss != null) {
            ByBoss.Remove(boss);
        }
    }

    /// <summary>
    /// Every frame: forgets the encounters and gates of unloaded scenes. The gates of a boss encounter are not opened
    /// here at its end: the boss's own end sequence opens them in each game (the Bell Beast's only once the silk
    /// heart is taken), from the replayed <c>SendEventToRegister</c>.
    /// </summary>
    private static void OnUpdate() {
        if (ByKey.Count == 0) {
            return;
        }

        foreach (var pair in ByKey.ToList()) {
            if (pair.Key == null || pair.Value.IsDestroyed) {
                ByKey.Remove(pair.Key!);
            }
        }

        ClosedGates.RemoveWhere(g => g == null);
        OpenedLocally.RemoveWhere(g => g == null);

        foreach (var gate in ByGate.Keys.Where(g => g == null).ToList()) {
            ByGate.Remove(gate);
        }

        foreach (var boss in ByBoss.Keys.Where(b => b == null).ToList()) {
            ByBoss.Remove(boss);
        }
    }

    #endregion
}
