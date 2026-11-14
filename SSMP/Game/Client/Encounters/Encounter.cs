using System.Collections.Generic;
using System.Linq;
using SSMP.Game.Client.Entity.Component;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Encounters;

/// <summary>
/// The state of an encounter, as decided by the scene host (docs/arenes.md).
/// </summary>
internal enum EncounterState : byte {
    /// <summary>Not started on the scene host.</summary>
    Idle = 0,

    /// <summary>In progress on the scene host.</summary>
    Started = 1,

    /// <summary>Over on the scene host (won, or ended by the boss's FSM).</summary>
    Ended = 2
}

/// <summary>
/// The two kinds of encounters.
/// </summary>
internal enum EncounterKind {
    /// <summary>
    /// A <see cref="BattleScene"/>: a wave arena, or a boss of its waves that starts it from its FSM (Mossbone
    /// Mother). Its area is the arena's collider, its gates the children of <see cref="BattleScene.gates"/>.
    /// </summary>
    Battle,

    /// <summary>
    /// A boss (<c>"boss": true</c> in the entity registry) that is not part of a <see cref="BattleScene"/> and whose
    /// own FSMs close and open the gates of its room (Bell Beast). Its area is the span of those gates.
    /// </summary>
    BossGates
}

/// <summary>
/// The shared notion of "encounter in progress" (nfloch/ssmp-bugs#8): one arena or boss fight of the current scene.
/// The scene host decides its state and syncs it (<see cref="EncounterComponent"/>); every rule of PRINCIPES.md
/// "Boss et arènes" reads it from here: <see cref="ArenaWait"/> (hold the start), <see cref="Downed.DownedManager"/>
/// (downed instead of death, coming back in), the end of the battle on scene clients and the boss music.
/// </summary>
internal sealed class Encounter {
    /// <summary>A gate whose distance to the entry point is within this many units of the closest gate is an entry gate.</summary>
    private const float EntryGateTolerance = 4f;

    /// <summary>Extra height above and below the gates of a <see cref="EncounterKind.BossGates"/> area.</summary>
    private const float GateAreaVerticalMargin = 3f;

    public EncounterKind Kind { get; }

    /// <summary>The name in the logs: object and scene.</summary>
    public string Name { get; }

    /// <summary>The arena, for <see cref="EncounterKind.Battle"/>.</summary>
    public BattleScene? Battle { get; }

    /// <summary>The host objects of the bosses, for <see cref="EncounterKind.BossGates"/>.</summary>
    public List<GameObject> Bosses { get; } = [];

    /// <summary>The gate FSMs of a <see cref="EncounterKind.BossGates"/> encounter.</summary>
    private readonly List<PlayMakerFSM> _bossGates;

    /// <summary>The area of a <see cref="EncounterKind.BossGates"/> encounter.</summary>
    private readonly Rect _gateArea;

    /// <summary>
    /// The entity components that carry the state on the network, the first one sends and applies it (one per
    /// encounter, not one per enemy of the waves). Empty for an arena without synced entities.
    /// </summary>
    public List<EncounterComponent> Carriers { get; } = [];

    /// <summary>The scene host's state, as last sent or received (synced encounters).</summary>
    private EncounterState _hostState;

    /// <summary>Whether the end was announced in this game (<see cref="EncounterTracker.Ending"/>).</summary>
    public bool EndingRaised { get; set; }

    /// <summary>Whether a refused local end of the arena was logged (scene client).</summary>
    public bool LoggedRefusedEnd { get; set; }

    /// <summary>Whether a gate of a <see cref="EncounterKind.BossGates"/> encounter was closed by the game's FSMs.</summary>
    public bool GatesClosedByGame { get; set; }

    /// <summary>Whether, after that, a gate was opened again by the game's FSMs (end of the boss fight).</summary>
    public bool GatesOpenedByGame { get; set; }

    private Encounter(EncounterKind kind, string name, BattleScene? battle, List<PlayMakerFSM> bossGates, Rect area) {
        Kind = kind;
        Name = name;
        Battle = battle;
        _bossGates = bossGates;
        _gateArea = area;
    }

    public static Encounter ForBattle(BattleScene battle) => new(
        EncounterKind.Battle,
        battle.name + " (" + battle.gameObject.scene.name + ")",
        battle,
        [],
        default
    );

    public static Encounter ForBossGates(GameObject boss, List<PlayMakerFSM> gates, Rect area) => new(
        EncounterKind.BossGates,
        boss.name + " (" + boss.scene.name + ")",
        null,
        gates,
        area
    );

    /// <summary>Whether the state follows the scene host (an entity of the encounter is synced).</summary>
    public bool IsSynced => Carriers.Count > 0;

    /// <summary>
    /// The state of the encounter: the scene host's for a synced encounter. An arena without synced entities runs in
    /// each game on its own, its state is the local one.
    /// </summary>
    public EncounterState State {
        get {
            if (IsSynced || Battle == null) {
                return _hostState;
            }

            return Battle.started ? EncounterState.Started :
                Battle.completed ? EncounterState.Ended : EncounterState.Idle;
        }
    }

    public bool IsInProgress => State == EncounterState.Started;

    /// <summary>
    /// Whether nothing waits any more: started or over on the scene host, or in this game (a late arrival's arena
    /// starts once inside).
    /// </summary>
    public bool IsUnderwayOrDone =>
        State != EncounterState.Idle || Battle != null && (Battle.started || Battle.completed);

    /// <summary>Sets the scene host's state (sent by this game as scene host, or received).</summary>
    public void SetHostState(EncounterState state) => _hostState = state;

    /// <summary>Whether the objects of the encounter are gone (scene unloaded).</summary>
    public bool IsDestroyed => Kind == EncounterKind.Battle ? Battle == null : _bossGates.All(g => g == null);

    #region Area

    /// <summary>
    /// Whether the given position is inside the area of the encounter, extended by a margin. The single geometry of
    /// the area: an arena's collider (<see cref="AreaContains"/>), or the span of a boss's gates.
    /// </summary>
    public bool Contains(Vector2 position, float margin = 0f) {
        if (Battle != null) {
            return AreaContains(Battle, position, margin);
        }

        return position.x >= _gateArea.xMin - margin && position.x <= _gateArea.xMax + margin &&
               position.y >= _gateArea.yMin - margin && position.y <= _gateArea.yMax + margin;
    }

    /// <summary>The center of the area.</summary>
    public Vector2 Center => Battle != null ? Battle.transform.position : _gateArea.center;

    /// <summary>
    /// Whether the given position is inside the area of the given object: its box or polygon collider, even when the
    /// game disabled it (arenas disable their trigger once started, some bosses keep it disabled), extended by a
    /// margin. Without a collider, the position is considered inside.
    /// </summary>
    public static bool AreaContains(Component areaOwner, Vector2 position, float margin = 0f) {
        var transform = areaOwner.transform;

        var box = areaOwner.GetComponent<BoxCollider2D>();
        if (box != null) {
            var center = (Vector2) transform.TransformPoint(box.offset);
            var scale = transform.lossyScale;
            var half = new Vector2(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y)) / 2f;
            return Mathf.Abs(position.x - center.x) <= half.x + margin &&
                   Mathf.Abs(position.y - center.y) <= half.y + margin;
        }

        var polygon = areaOwner.GetComponent<PolygonCollider2D>();
        if (polygon != null && polygon.points.Length > 0) {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var point in polygon.points) {
                var world = (Vector2) transform.TransformPoint(point + polygon.offset);
                min = Vector2.Min(min, world);
                max = Vector2.Max(max, world);
            }

            return position.x >= min.x - margin && position.x <= max.x + margin &&
                   position.y >= min.y - margin && position.y <= max.y + margin;
        }

        var collider = areaOwner.GetComponent<Collider2D>();
        if (collider != null && collider.enabled) {
            var bounds = collider.bounds;
            bounds.Expand(margin * 2f);
            return bounds.Contains(new Vector3(position.x, position.y, bounds.center.z));
        }

        return true;
    }

    /// <summary>
    /// The area spanned by the given gates: from the leftmost to the rightmost gate collider, from the lowest to the
    /// highest, with <see cref="GateAreaVerticalMargin"/> above and below.
    /// </summary>
    public static Rect GateSpan(IEnumerable<PlayMakerFSM> gates) {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var gate in gates) {
            var collider = gate.GetComponent<Collider2D>();
            Bounds bounds;
            if (collider is BoxCollider2D box) {
                var center = gate.transform.TransformPoint(box.offset);
                var scale = gate.transform.lossyScale;
                bounds = new Bounds(center, new Vector3(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y)));
            } else if (collider != null) {
                bounds = collider.bounds;
            } else {
                bounds = new Bounds(gate.transform.position, Vector3.zero);
            }

            min = Vector2.Min(min, bounds.min);
            max = Vector2.Max(max, bounds.max);
        }

        min.y -= GateAreaVerticalMargin;
        max.y += GateAreaVerticalMargin;
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    #endregion

    #region Gates

    /// <summary>The gate FSMs of the encounter that are active in the scene.</summary>
    public List<PlayMakerFSM> Gates {
        get {
            if (Battle == null) {
                return _bossGates.Where(g => g != null && g.gameObject.activeInHierarchy).ToList();
            }

            var gates = new List<PlayMakerFSM>();
            if (Battle.gates != null) {
                foreach (Transform child in Battle.gates.transform) {
                    var fsm = child.GetComponent<PlayMakerFSM>();
                    if (fsm != null && child.gameObject.activeInHierarchy) {
                        gates.Add(fsm);
                    }
                }
            }

            return gates;
        }
    }

    /// <summary>Whether the given FSM is one of the gates of this encounter.</summary>
    public bool HasGate(PlayMakerFSM fsm) =>
        Battle != null ? Battle.gates != null && fsm.transform.parent == Battle.gates.transform : _bossGates.Contains(fsm);

    /// <summary>
    /// Sorts the gates into entry gates (closest to the entry point of the local hero in the scene, within
    /// <see cref="EntryGateTolerance"/> of the closest one) and progression gates (the others). With fewer than two
    /// gates, all are entry gates.
    /// </summary>
    public List<(PlayMakerFSM Fsm, bool IsEntry, float Distance, float Closest)> ClassifyGates(Vector2 entry) {
        var gates = Gates.Select(g => (Fsm: g, Distance: Vector2.Distance(entry, g.transform.position))).ToList();
        if (gates.Count == 0) {
            return [];
        }

        var closest = gates.Min(g => g.Distance);
        return gates
            .Select(g => (g.Fsm, gates.Count < 2 || g.Distance <= closest + EntryGateTolerance, g.Distance, closest))
            .ToList();
    }

    /// <summary>Whether the completion of the arena is saved (progression gates only exist for those).</summary>
    public bool IsSaved => Battle != null &&
                           (!string.IsNullOrEmpty(Battle.setPDBoolOnEnd) ||
                            Battle.GetComponent<PersistentBoolItem>() != null);

    /// <summary>
    /// In this game only: closes the gates behind the local hero, for a player entering during the battle (late
    /// arrival, back from the downed state). An arena locks in its own way (<c>LockInBattle</c>: gates and camera
    /// locks); a boss's gates are closed one by one, also from a state that no longer listens to <c>BG CLOSE</c>
    /// (the Bell Beast's gates only reopen through a global <c>BG OPEN</c>, their <c>Open</c> state has no way out).
    /// </summary>
    public void LockLocally() {
        using var _ = EncounterTracker.LocalGateOperation();
        if (Battle != null) {
            Battle.LockInBattle();
            return;
        }

        foreach (var gate in Gates) {
            CloseGate(gate);
        }
    }

    /// <summary>
    /// In this game only: opens the gates that let the hero back in (back from the downed state), but not the
    /// progression gates (those further than the side the hero entered the scene from), so coming back is not a way
    /// past the arena. Without a known entry point, or for an arena whose completion is not saved, all gates open.
    /// </summary>
    public void OpenForReentry() {
        using var _ = EncounterTracker.LocalGateOperation();
        if (Battle != null && Battle.camLocks != null) {
            Battle.camLocks.SetActive(false);
        }

        var entry = EncounterTracker.EntryPoint;
        if (entry == null || Battle != null && !IsSaved || Contains(entry.Value)) {
            foreach (var gate in Gates) {
                EncounterTracker.OpenGateLocally(gate);
            }

            return;
        }

        foreach (var (fsm, isEntry, _, _) in ClassifyGates(entry.Value)) {
            if (isEntry) {
                EncounterTracker.OpenGateLocally(fsm);
            } else {
                Logger.Info($"[ArenaWait] Keeping progression gate '{fsm.name}' of '{Name}' closed on re-entry");
            }
        }
    }

    /// <summary>
    /// On a scene client (or for the save completion of an arena): ends the arena in this game, the scene host's
    /// arena is over. A started arena plays its end (<c>DoEndBattle</c>: gates, camera, end events, save); an arena
    /// that never started here (progression gates closed, late arrival) takes the "already completed" state
    /// (<c>BattleCompleted</c>: gates quick-open).
    /// </summary>
    public void EndLocally(string reason) {
        var battle = Battle;
        if (battle == null) {
            return;
        }

        if (battle.started && battle.isActiveAndEnabled) {
            Logger.Info($"[Encounter] Battle '{battle.name}' {reason}, ending it locally");
            // EndBattle returns at once when completed is set: the completion may already be in the shared save
            battle.completed = false;
            EncounterTracker.EndFromHost(battle);
            return;
        }

        if (battle.completed) {
            return;
        }

        Logger.Info($"[Encounter] Battle '{battle.name}' {reason}, not started here: marking it completed");
        battle.started = false;
        battle.completed = true;
        battle.BattleCompleted();
    }

    /// <summary>
    /// Closes a gate: with <c>BG CLOSE</c> if its current state listens to it, otherwise by entering the state that
    /// <c>BG CLOSE</c> leads to from the open state.
    /// </summary>
    private static void CloseGate(PlayMakerFSM gate) {
        var active = gate.Fsm.ActiveState;
        if (active != null && active.Transitions.Any(t => t.EventName == "BG CLOSE") ||
            gate.FsmGlobalTransitions.Any(t => t.EventName == "BG CLOSE")) {
            gate.SendEvent("BG CLOSE");
            return;
        }

        foreach (var state in gate.FsmStates) {
            var close = state.Transitions.FirstOrDefault(t => t.EventName == "BG CLOSE");
            if (close == null || string.IsNullOrEmpty(close.ToState)) {
                continue;
            }

            if (active != null && (active.Name == close.ToState || IsClosedState(gate, active.Name, close.ToState))) {
                return;
            }

            Logger.Info($"[Encounter] Closing gate '{gate.name}' from state '{active?.Name}' (state '{close.ToState}')");
            gate.SetState(close.ToState);
            EncounterTracker.MarkGateClosedLocally(gate);
            return;
        }
    }

    /// <summary>Whether the state is reached from the closing state through <c>FINISHED</c> (already closed).</summary>
    private static bool IsClosedState(PlayMakerFSM gate, string stateName, string closeState) {
        var current = gate.FsmStates.FirstOrDefault(s => s.Name == closeState);
        for (var i = 0; current != null && i < 4; i++) {
            var next = current.Transitions.FirstOrDefault(t => t.EventName == "FINISHED");
            if (next == null) {
                return false;
            }

            if (next.ToState == stateName) {
                return true;
            }

            current = next.ToFsmState;
        }

        return false;
    }

    #endregion
}
