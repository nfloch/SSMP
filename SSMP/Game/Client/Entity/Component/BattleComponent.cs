using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component networks the state of the arena (<see cref="BattleScene"/>) an entity fights in: not started,
/// started or ended on the scene host. The scene host's arena rules:
/// <list type="bullet">
/// <item>An arena starts in two ways: its trigger collider when the local hero enters it, or an FSM call
/// (<c>CallMethodProper BattleScene.StartBattle</c>, bosses like Mossbone Mother). Either way, only the scene host's
/// start counts. On a scene client, the trigger is ignored (<see cref="ShouldIgnoreTrigger"/>): the client starts its
/// arena once the scene host's arena is started and its own hero is inside the arena area.</item>
/// <item>The end of the scene host's arena ends the client's arena.</item>
/// <item>The state is sent as entity data that the server stores, so a player entering the scene during the battle
/// (late arrival, return after a death) gets the gates closed, and gets them opened at the end.</item>
/// </list>
/// The arena is found as a parent of the host object (battle wave enemies) or as the target of a
/// <c>CallMethodProper BattleScene.StartBattle</c> action of its FSMs (bosses that start their own arena).
internal class BattleComponent : EntityComponent {
    /// <summary>The battle has not started on the scene host.</summary>
    private const byte StateIdle = 0;

    /// <summary>The battle is in progress on the scene host.</summary>
    private const byte StateStarted = 1;

    /// <summary>The battle has ended on the scene host.</summary>
    private const byte StateEnded = 2;

    /// <summary>
    /// The component in charge of each arena: it sends the state on the scene host and applies it on clients, so the
    /// arena is handled once and not once per enemy of its waves.
    /// </summary>
    private static readonly Dictionary<BattleScene, BattleComponent> Owners = new();

    /// <summary>The arena of the entity.</summary>
    private readonly BattleScene _battleScene;

    /// <summary>The last state sent or received (owner only).</summary>
    private byte _state;

    /// <summary>
    /// Whether the local hero entered the trigger while this game was not (yet) known as the scene host. If this game
    /// becomes the scene host, the arena starts as the game would have done.
    /// </summary>
    private bool _ignoredTrigger;

    /// <summary>The coroutine that waits for the local hero to be inside the arena before starting it.</summary>
    private Coroutine? _startRoutine;

    private BattleComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        BattleScene battleScene
    ) : base(netClient, entityId, gameObject) {
        _battleScene = battleScene;

        if (!Owners.TryGetValue(battleScene, out var owner) || owner == null) {
            Owners[battleScene] = this;
            MonoBehaviourUtil.Instance.OnUpdateEvent += OnOwnerUpdate;
        }
    }

    /// <summary>
    /// Creates the component if the entity belongs to an arena.
    /// </summary>
    public static BattleComponent? TryCreate(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        IEnumerable<PlayMakerFSM> hostFsms
    ) {
        var battleScene = FindBattleScene(gameObject.Host, hostFsms);
        return battleScene == null ? null : new BattleComponent(netClient, entityId, gameObject, battleScene);
    }

    /// <summary>
    /// Called from the hook on <c>BattleScene.OnTriggerEnter2D</c>: whether the local hero entering the trigger must
    /// not start the arena, because this game is not the scene host of an arena with synced entities.
    /// </summary>
    public static bool ShouldIgnoreTrigger(BattleScene battleScene) {
        if (!Owners.TryGetValue(battleScene, out var owner) || owner == null || !owner.IsControlled) {
            return false;
        }

        if (!owner._ignoredTrigger) {
            Logger.Info(
                $"Ignoring the trigger of battle '{battleScene.name}' as scene client, the scene host's arena rules"
            );
        }

        owner._ignoredTrigger = true;
        return true;
    }

    /// <summary>
    /// Finds the arena of the given host object: a parent, or the target of a StartBattle call in its FSMs.
    /// </summary>
    private static BattleScene? FindBattleScene(GameObject? host, IEnumerable<PlayMakerFSM> hostFsms) {
        if (host == null) {
            return null;
        }

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
    /// Every frame on the owner, as scene host: sends the arena state when it changes. Not tied to the entity update,
    /// which stops once the host object of the boss is destroyed at its death.
    /// </summary>
    private void OnOwnerUpdate() {
        if (IsControlled || _battleScene == null) {
            return;
        }

        if (_ignoredTrigger) {
            // Became the scene host after the trigger was ignored: start the arena like the trigger would have
            _ignoredTrigger = false;
            if (_state != StateStarted && !_battleScene.started && !_battleScene.completed &&
                _battleScene.currentWave == 0 && IsHeroInside()) {
                Logger.Info($"Starting battle '{_battleScene.name}' whose trigger was ignored, now scene host");
                _battleScene.StartBattle();
            }
        }

        var newState = _state;
        if (_state != StateStarted && _battleScene.started) {
            newState = StateStarted;

            // A battle completed in the save (another player's victory, synced by the server) would never end:
            // EndBattle returns at once when completed is set
            if (_battleScene.completed) {
                Logger.Info($"Battle '{_battleScene.name}' started while completed in the save, letting it end");
                _battleScene.completed = false;
            }
        } else if (_state == StateStarted && (!_battleScene.started || _battleScene.completed)) {
            newState = StateEnded;
        }

        if (newState == _state) {
            return;
        }

        _state = newState;
        Logger.Info(
            $"Battle '{_battleScene.name}' {(newState == StateStarted ? "started" : "ended")} as scene host, " +
            "sending it"
        );

        var data = new EntityNetworkData { Type = EntityComponentType.Battle };
        data.Packet.Write(newState);
        SendData(data);

        if (newState == StateEnded) {
            // The stored boss music must not play for a player entering the scene after the battle
            MusicComponent.ResetHostMusic();
        }
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var newState = data.Packet.ReadByte();
        if (!IsControlled || _battleScene == null) {
            return;
        }

        // The scene host's owner may be another entity of the arena than ours
        var owner = Owners.TryGetValue(_battleScene, out var o) && o != null ? o : this;
        owner.ApplyState(newState, alreadyInSceneUpdate);
    }

    /// <summary>
    /// Applies the scene host's arena state on a scene client.
    /// </summary>
    private void ApplyState(byte newState, bool alreadyInSceneUpdate) {
        _state = newState;

        switch (newState) {
            case StateStarted:
                if (_battleScene.started || _startRoutine != null) {
                    return;
                }

                Logger.Info(
                    $"Battle '{_battleScene.name}' is in progress on the scene host" +
                    (alreadyInSceneUpdate ? " (entering late)" : "") + ", starting it once inside the arena"
                );
                _startRoutine = _battleScene.StartCoroutine(StartWhenInside());
                break;
            case StateEnded:
                if (_startRoutine != null) {
                    _battleScene.StopCoroutine(_startRoutine);
                    _startRoutine = null;
                }

                // Not ended locally yet (by its own waves or by the persistent bool in SaveManager)
                if (_battleScene.started) {
                    Logger.Info($"Battle '{_battleScene.name}' ended on the scene host, ending it locally");
                    _battleScene.completed = false;
                    _battleScene.DoEndBattle();
                }

                break;
        }
    }

    /// <summary>
    /// Waits for the local hero to be inside the arena area, then starts the arena: gates, camera locks, start events
    /// and music. Its waves only drive the inactive host objects; the enemies follow the scene host.
    /// </summary>
    private IEnumerator StartWhenInside() {
        while (!IsHeroInside()) {
            yield return null;
        }

        _startRoutine = null;
        if (_battleScene.started || _state != StateStarted) {
            yield break;
        }

        // A battle completed in the save (another player's victory, synced by the server) would never end
        _battleScene.completed = false;

        Logger.Info($"Starting battle '{_battleScene.name}' started by the scene host");
        _battleScene.StartBattle();
    }

    /// <summary>
    /// Whether the local hero is inside the area of the arena: its trigger collider, even when the game disabled it
    /// (bosses that start the arena themselves). Without a collider, the hero is considered inside.
    /// </summary>
    private bool IsHeroInside() {
        var hero = HeroController.instance;
        if (hero == null) {
            return false;
        }

        Vector2 heroPos = hero.transform.position;
        var transform = _battleScene.transform;

        var box = _battleScene.GetComponent<BoxCollider2D>();
        if (box != null) {
            var center = (Vector2) transform.TransformPoint(box.offset);
            var scale = transform.lossyScale;
            var half = new Vector2(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y)) / 2f;
            return Mathf.Abs(heroPos.x - center.x) <= half.x && Mathf.Abs(heroPos.y - center.y) <= half.y;
        }

        var polygon = _battleScene.GetComponent<PolygonCollider2D>();
        if (polygon != null && polygon.points.Length > 0) {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var point in polygon.points) {
                var world = (Vector2) transform.TransformPoint(point + polygon.offset);
                min = Vector2.Min(min, world);
                max = Vector2.Max(max, world);
            }

            return heroPos.x >= min.x && heroPos.x <= max.x && heroPos.y >= min.y && heroPos.y <= max.y;
        }

        return true;
    }

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnOwnerUpdate;

        if (_battleScene != null && Owners.TryGetValue(_battleScene, out var owner) && owner == this) {
            Owners.Remove(_battleScene);
        }

        if (_startRoutine != null && _battleScene != null) {
            _battleScene.StopCoroutine(_startRoutine);
        }

        _startRoutine = null;
    }
}
