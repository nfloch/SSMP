using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Encounters;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries the state of the encounter (<see cref="Encounter"/>) an entity fights in, as decided by the
/// scene host: not started, in progress or over. It is the network side of the shared notion of "encounter in
/// progress" (docs/arenes.md):
/// <list type="bullet">
/// <item>Arena (<see cref="BattleScene"/>, wave enemies or a boss that starts it from its FSM): the scene host's
/// arena started (by its trigger once everyone is in, or by an FSM call) or ended. On a scene client the trigger is
/// ignored (<see cref="ShouldIgnoreTrigger"/>); the client starts its arena once its hero is inside the area, and ends
/// it with the scene host.</item>
/// <item>Boss that manages its own gates (<c>"boss": true</c>, no arena, Bell Beast): started when the game's FSMs
/// close its gates on the scene host, over when every boss of it is dead or the game opens its gates again. A late
/// arrival inside the area gets the gates closed.</item>
/// <item>The state is entity data that the server stores: a player entering the scene during the encounter gets it.
/// </item>
/// </list>
/// Each encounter has one owner, the first carrier: it sends and applies the state, not once per enemy of the waves.
internal class EncounterComponent : EntityComponent {
    /// <summary>Seconds the local hero stays inside a boss encounter in progress before a late arrival is locked in.</summary>
    private const float LateLockSeconds = 1f;

    /// <summary>The encounter of the entity.</summary>
    private readonly Encounter _encounter;

    /// <summary>Whether the entity is a boss of a <see cref="EncounterKind.BossGates"/> encounter.</summary>
    private readonly bool _isGateBoss;

    /// <summary>Whether the ignored trigger was logged, to log it once.</summary>
    private bool _loggedIgnoredTrigger;

    /// <summary>The coroutine that waits for the local hero to be inside the arena before starting it.</summary>
    private Coroutine? _startRoutine;

    /// <summary>Since when the local hero is inside a boss encounter in progress with open gates, or a negative value.</summary>
    private float _insideSince = -1f;

    /// <summary>Whether this game already locked the late arrival in.</summary>
    private bool _lateLocked;

    /// <summary>Whether the arena was seen started in this game while scene host.</summary>
    private bool _sawLocalStart;

    private EncounterComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        Encounter encounter,
        bool isGateBoss
    ) : base(netClient, entityId, gameObject) {
        _encounter = encounter;
        _isGateBoss = isGateBoss;

        encounter.Carriers.Add(this);
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnEveryFrame;
    }

    /// <summary>The owner of the encounter: the first live carrier.</summary>
    private bool IsOwner => _encounter.Carriers.Count > 0 && _encounter.Carriers[0] == this;

    /// <summary>
    /// Creates the component if the entity belongs to an encounter: an arena (parent of the host object, or target
    /// of a <c>CallMethodProper BattleScene.StartBattle</c> of its FSMs), or a boss with gates of its own.
    /// </summary>
    public static EncounterComponent? TryCreate(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        IEnumerable<PlayMakerFSM> hostFsms,
        bool isBoss
    ) {
        if (gameObject.Host == null) {
            return null;
        }

        var battleScene = FindBattleScene(gameObject.Host, hostFsms);
        if (battleScene != null) {
            return new EncounterComponent(
                netClient, entityId, gameObject, EncounterTracker.ForBattle(battleScene), false
            );
        }

        if (!isBoss) {
            return null;
        }

        var encounter = EncounterTracker.ForBoss(gameObject.Host);
        return encounter == null ? null : new EncounterComponent(netClient, entityId, gameObject, encounter, true);
    }

    /// <summary>
    /// Called from the hook on <c>BattleScene.OnTriggerEnter2D</c>: whether the local hero entering the trigger must
    /// not start the arena, because this game is a scene client of an arena with synced entities.
    /// </summary>
    public static bool ShouldIgnoreTrigger(BattleScene battleScene) {
        var encounter = EncounterTracker.ForBattle(battleScene);
        if (!encounter.IsSynced || encounter.Carriers[0] is not { IsControlled: true } owner) {
            return false;
        }

        if (!owner._loggedIgnoredTrigger) {
            Logger.Info(
                $"Ignoring the trigger of battle '{battleScene.name}' as scene client, the scene host's arena rules"
            );
        }

        // Starting it if this game becomes the scene host: ArenaWait, once all players are inside
        owner._loggedIgnoredTrigger = true;
        return true;
    }

    /// <summary>
    /// Finds the arena of the given host object: a parent, or the target of a StartBattle call in its FSMs.
    /// </summary>
    private static BattleScene? FindBattleScene(GameObject host, IEnumerable<PlayMakerFSM> hostFsms) {
        var parent = host.GetComponentInParent<BattleScene>(true);
        if (parent != null) {
            return parent;
        }

        foreach (var fsm in hostFsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is not CallMethodProper call ||
                        call.behaviour.Value != nameof(BattleScene) ||
                        call.methodName.Value != nameof(BattleScene.StartBattle)) {
                        continue;
                    }

                    var target = call.Fsm.GetOwnerDefaultTarget(call.gameObject);
                    var battleScene = target != null ? target.GetComponent<BattleScene>() : null;
                    if (battleScene != null) {
                        return battleScene;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every frame, not tied to the entity update, which stops once the host object of the boss is destroyed at its
    /// death. The owner decides as scene host; every carrier of a boss watches its own copy on a scene client.
    /// </summary>
    private void OnEveryFrame() {
        if (_encounter.IsDestroyed) {
            return;
        }

        if (IsControlled) {
            UpdateClient();
        } else if (IsOwner) {
            UpdateHost();
        }
    }

    #region Scene host

    /// <summary>
    /// As scene host: decides the state of the encounter from this game and sends it when it changes.
    /// </summary>
    private void UpdateHost() {
        var state = _encounter.State;
        var newState = _encounter.Kind == EncounterKind.Battle ? DecideBattle(state) : DecideBoss(state);
        if (newState == state) {
            return;
        }

        _encounter.SetHostState(newState);
        Logger.Info(
            $"[Encounter] {(_encounter.Battle != null ? "Battle" : "Boss encounter")} '{_encounter.Name}' " +
            $"{(newState == EncounterState.Started ? "started" : "ended")} as scene host, sending it"
        );

        var data = new EntityNetworkData { Type = EntityComponentType.Encounter };
        data.Packet.Write((byte) newState);
        SendData(data);

        if (newState == EncounterState.Ended) {
            EncounterTracker.RaiseEnding(_encounter, "over on the scene host");

            // The stored boss music must not play for a player entering the scene after the battle
            MusicComponent.ResetHostMusic();
        }
    }

    /// <summary>
    /// The scene host's arena: started or ended in this game. A scene host that took over during the battle while its
    /// own arena had not started (hero outside) keeps the battle in progress until its arena starts and ends here.
    /// </summary>
    private EncounterState DecideBattle(EncounterState state) {
        var battle = _encounter.Battle!;
        if (battle.started) {
            _sawLocalStart = true;
        }

        if (state == EncounterState.Started && (battle.completed || _sawLocalStart && !battle.started)) {
            return EncounterState.Ended;
        }

        if (state != EncounterState.Started && battle.started) {
            // Completion is shared, and ArenaWait never starts a completed arena: a start while completed means an
            // FSM started it anyway (save and arena out of step). EndBattle returns at once when completed is set,
            // so the arena would never end and the gates would stay closed: let it end
            if (battle.completed) {
                Logger.Info($"[Encounter] Battle '{battle.name}' started while completed in the save, letting it end");
                battle.completed = false;
            }

            return EncounterState.Started;
        }

        return state;
    }

    /// <summary>
    /// The scene host's boss fight: started when the game closes its gates, over when every boss of it is dead or
    /// the game opens its gates again.
    /// </summary>
    private EncounterState DecideBoss(EncounterState state) {
        switch (state) {
            case EncounterState.Idle when _encounter.GatesClosedByGame && !_encounter.GatesOpenedByGame:
                return EncounterState.Started;
            case EncounterState.Started when _encounter.GatesOpenedByGame || AllBossesDead(host: true):
                return EncounterState.Ended;
            default:
                return state;
        }
    }

    /// <summary>
    /// Whether every boss of the encounter is dead in this game: its host object (scene host) or client copy, gone
    /// or with a dead <see cref="HealthManager"/>.
    /// </summary>
    private bool AllBossesDead(bool host) {
        foreach (var carrier in _encounter.Carriers) {
            if (!carrier._isGateBoss) {
                continue;
            }

            var boss = host ? carrier.GameObject.Host : carrier.GameObject.Client;
            if (boss == null) {
                continue;
            }

            var healthManager = boss.GetComponent<HealthManager>();
            if (healthManager != null && !healthManager.isDead && healthManager.hp > 0) {
                return false;
            }
        }

        return true;
    }

    #endregion

    /// <inheritdoc />
    /// Becoming scene host during a battle: the local end of its waves was held on this game (the end followed the
    /// previous scene host), so count the remaining enemies again to carry on.
    public override void InitializeHost(uint sceneHostEpoch) {
        var battle = _encounter.Battle;
        if (!IsOwner || battle == null || !battle.started || _encounter.State != EncounterState.Started) {
            return;
        }

        Logger.Info($"[Encounter] Became scene host during battle '{battle.name}', counting its enemies again");
        battle.CheckEnemies();
    }

    #region Scene client

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var newState = (EncounterState) data.Packet.ReadByte();
        if (!IsControlled || _encounter.IsDestroyed) {
            return;
        }

        // The scene host's owner may be another entity of the encounter than ours
        var owner = _encounter.Carriers.Count > 0 ? _encounter.Carriers[0] : this;
        owner.ApplyState(newState, alreadyInSceneUpdate);
    }

    /// <summary>
    /// Applies the scene host's state on a scene client.
    /// </summary>
    private void ApplyState(EncounterState newState, bool alreadyInSceneUpdate) {
        var previous = _encounter.State;
        _encounter.SetHostState(newState);
        if (newState != previous) {
            Logger.Info(
                $"[Encounter] '{_encounter.Name}' is {newState} on the scene host" +
                (alreadyInSceneUpdate ? " (entering late)" : "")
            );
        }

        var battle = _encounter.Battle;
        switch (newState) {
            case EncounterState.Started when battle != null:
                if (battle.started || _startRoutine != null) {
                    return;
                }

                Logger.Info(
                    $"Battle '{battle.name}' is in progress on the scene host" +
                    (alreadyInSceneUpdate ? " (entering late)" : "") + ", starting it once inside the arena"
                );
                _startRoutine = battle.StartCoroutine(StartWhenInside(battle));
                break;
            case EncounterState.Ended:
                if (_startRoutine != null && battle != null) {
                    battle.StopCoroutine(_startRoutine);
                    _startRoutine = null;
                }

                EncounterTracker.RaiseEnding(_encounter, "over on the scene host");
                _encounter.EndLocally("ended on the scene host");
                break;
        }
    }

    /// <summary>
    /// Waits for the local hero to be inside the arena area, then starts the arena: gates, camera locks, start events
    /// and music. Its waves only drive the inactive host objects; the enemies follow the scene host.
    /// </summary>
    private IEnumerator StartWhenInside(BattleScene battle) {
        while (!IsHeroInside()) {
            yield return null;
        }

        _startRoutine = null;
        if (battle.started || _encounter.State != EncounterState.Started) {
            yield break;
        }

        // Completion is shared (nfloch/ssmp-bugs#8): completed here means the scene host's arena already ended and
        // its end is on its way. Starting it again would close the gates on a finished battle
        if (battle.completed) {
            Logger.Info($"Battle '{battle.name}' is already completed (shared), not starting it");
            yield break;
        }

        Logger.Info($"Starting battle '{battle.name}' started by the scene host");
        battle.StartBattle();
    }

    /// <summary>
    /// As scene client, every frame: a boss encounter in progress locks a late arrival in once inside its area (the
    /// game's own zone may not do it), and the death of the boss's copy announces the end in this game.
    /// </summary>
    private void UpdateClient() {
        if (!_isGateBoss) {
            return;
        }

        if (_encounter.IsInProgress && AllBossesDead(host: false)) {
            EncounterTracker.RaiseEnding(_encounter, "boss dead in this game");
        }

        if (!IsOwner || _lateLocked || !_encounter.IsInProgress) {
            _insideSince = -1f;
            return;
        }

        if (!IsHeroInside() || _encounter.Gates.TrueForAll(EncounterTracker.IsGateClosed)) {
            _insideSince = -1f;
            return;
        }

        if (_insideSince < 0f) {
            _insideSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _insideSince < LateLockSeconds) {
            return;
        }

        _lateLocked = true;
        Logger.Info($"[Encounter] Boss encounter '{_encounter.Name}' in progress, closing its gates behind the hero");
        _encounter.LockLocally();
    }

    private bool IsHeroInside() {
        var hero = HeroController.instance;
        return hero != null && !hero.cState.hazardDeath && _encounter.Contains(hero.transform.position);
    }

    #endregion

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnEveryFrame;
        _encounter.Carriers.Remove(this);
        if (_isGateBoss) {
            EncounterTracker.Forget(GameObject.Host);
        }

        if (_startRoutine != null && _encounter.Battle != null) {
            _encounter.Battle.StopCoroutine(_startRoutine);
        }

        _startRoutine = null;
    }
}
