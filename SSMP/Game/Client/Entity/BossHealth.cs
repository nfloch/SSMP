using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Multiplies the max health of bosses (PRINCIPES.md, "Boss et arènes", rule 6).
/// <para>
/// A boss is an entity whose entry in <c>entity-registry.json</c> has <c>"boss": true</c>. The multiplier is the
/// server setting <c>BossHealthMultiplier</c> when it is positive, otherwise the number of connected players.
/// </para>
/// <para>
/// When: once per spawn, when the scene host registers the boss in a room nobody else was in (the server keeps no
/// data for that room, so the boss is fresh), before the boss FSM starts. Boss FSMs read their HP in their first
/// state to compute the HP thresholds of their phases (e.g. the Bell Beast's <c>Init</c>: GetHP then
/// MultiplyIntByFloat), so the phases keep their proportions. The scaled HP reaches the other players through the
/// usual health sync of <see cref="Component.HealthManagerComponent"/> (sent as a heal, stored by the server for the
/// players entering later). A player who connects or enters the room during the fight does not change the boss HP:
/// it is evaluated again at the next spawn (room reloaded, battle reset after everyone died).
/// </para>
/// <para>
/// Absolute HP set later by a boss FSM (<see cref="SetHP"/>, e.g. a phase that resets the HP) is multiplied too, on
/// the scene host. Each game keeps the multiplier of its bosses so that a client that becomes scene host during the
/// fight keeps scaling them.
/// </para>
/// </summary>
internal static class BossHealth {
    /// <summary>
    /// The multiplier applied to a boss health manager on this game, and whether its current HP was multiplied.
    /// </summary>
    private sealed class State {
        public float Multiplier;
        public bool HpScaled;
    }

    /// <summary>
    /// The state of each boss health manager seen on this game. Weak keys: entities are registered again when a
    /// pre-loaded scene becomes the active one, the same object must not be multiplied twice.
    /// </summary>
    private static readonly ConditionalWeakTable<HealthManager, State> States = new();

    /// <summary>
    /// The private "initHp" field of the health manager, the max HP used by HealToMax and RefillHP.
    /// </summary>
    private static readonly FieldInfo? InitHpField =
        typeof(HealthManager).GetField("initHp", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Gets the BossHealthMultiplier server setting.
    /// </summary>
    private static Func<float> _getSetting = () => 0f;

    /// <summary>
    /// Gets the number of connected players, including the local player.
    /// </summary>
    private static Func<int> _getPlayerCount = () => 1;

    private static Hook? _setHpHook;

    /// <summary>
    /// Sets the sources of the multiplier and hooks the SetHP action.
    /// </summary>
    public static void Initialize(Func<float> getSetting, Func<int> getPlayerCount) {
        _getSetting = getSetting;
        _getPlayerCount = getPlayerCount;

        if (InitHpField == null) {
            Logger.Warn("[BossHp] HealthManager.initHp not found, the max HP used by heals will not be multiplied");
        }

        _setHpHook ??= new Hook(
            typeof(SetHP).GetMethod(nameof(SetHP.OnEnter), BindingFlags.Instance | BindingFlags.Public),
            OnSetHpEnter
        );
    }

    /// <summary>
    /// The multiplier to apply now, with a description of where it comes from for the logs.
    /// </summary>
    public static float GetMultiplier(out string source) {
        var setting = _getSetting();
        if (setting > 0f) {
            source = "server setting";
            return setting;
        }

        var players = System.Math.Max(1, _getPlayerCount());
        source = $"auto, {players} connected player{(players > 1 ? "s" : "")}";
        return players;
    }

    /// <summary>
    /// Called when the local player is the scene host of a fresh boss: multiplies its HP and max HP.
    /// </summary>
    public static void OnSpawnAsSceneHost(GameObject hostObject) {
        var healthManager = hostObject != null ? hostObject.GetComponent<HealthManager>() : null;
        if (healthManager == null) {
            return;
        }

        var state = States.GetValue(healthManager, _ => new State());
        if (state.HpScaled) {
            Logger.Info($"[BossHp] '{hostObject!.name}' HP already multiplied (x{state.Multiplier:0.##}), skipping");
            return;
        }

        if (healthManager.isDead || healthManager.hp <= 0) {
            Logger.Info($"[BossHp] '{hostObject!.name}' is dead (hp={healthManager.hp}), not multiplying its HP");
            return;
        }

        var multiplier = GetMultiplier(out var source);
        var maxScaled = state.Multiplier > 0f;
        state.Multiplier = multiplier;
        state.HpScaled = true;

        var baseHp = healthManager.hp;
        var newHp = Scale(baseHp, multiplier);
        healthManager.hp = newHp;

        var initHpText = "";
        if (!maxScaled && InitHpField != null) {
            var initHp = (int) InitHpField.GetValue(healthManager);
            InitHpField.SetValue(healthManager, Scale(initHp, multiplier));
            initHpText = $", max {initHp} -> {Scale(initHp, multiplier)}";
        }

        Logger.Info(
            $"[BossHp] Boss '{hostObject!.name}' spawned as scene host: HP {baseHp} -> {newHp}{initHpText} " +
            $"(x{multiplier:0.##}, {source})"
        );
    }

    /// <summary>
    /// Called when the local player is a scene client of a boss. Its HP comes from the scene host; only its max HP
    /// and the multiplier are kept, for the case where this player becomes scene host during the fight.
    /// </summary>
    public static void OnSpawnAsSceneClient(GameObject hostObject) {
        var healthManager = hostObject != null ? hostObject.GetComponent<HealthManager>() : null;
        if (healthManager == null) {
            return;
        }

        var state = States.GetValue(healthManager, _ => new State());
        if (state.Multiplier > 0f) {
            return;
        }

        var multiplier = GetMultiplier(out var source);
        state.Multiplier = multiplier;

        if (InitHpField != null) {
            var initHp = (int) InitHpField.GetValue(healthManager);
            InitHpField.SetValue(healthManager, Scale(initHp, multiplier));
        }

        Logger.Info(
            $"[BossHp] Boss '{hostObject!.name}' spawned as scene client: HP follows the scene host, " +
            $"multiplier x{multiplier:0.##} kept for a host change ({source})"
        );
    }

    /// <summary>
    /// Multiplies an HP value, keeping at least 1.
    /// </summary>
    private static int Scale(int hp, float multiplier) {
        return System.Math.Max(1, Mathf.RoundToInt(hp * multiplier));
    }

    /// <summary>
    /// Multiplies the absolute HP that a boss FSM sets, on the game that runs the boss FSM (the scene host).
    /// </summary>
    private static void OnSetHpEnter(Action<SetHP> orig, SetHP self) {
        orig(self);

        try {
            if (self.hp == null || self.hp.IsNone || self.hp.Value <= 0) {
                return;
            }

            var target = self.Fsm?.GetOwnerDefaultTarget(self.target);
            var healthManager = target != null ? target.GetComponent<HealthManager>() : null;
            if (healthManager == null || !States.TryGetValue(healthManager, out var state) || state.Multiplier <= 0f) {
                return;
            }

            if (healthManager.hp != self.hp.Value || System.Math.Abs(state.Multiplier - 1f) < 0.001f) {
                return;
            }

            var newHp = Scale(self.hp.Value, state.Multiplier);
            healthManager.hp = newHp;

            Logger.Info(
                $"[BossHp] FSM '{self.Fsm!.Name}' of '{target!.name}' set HP to {self.hp.Value}, " +
                $"multiplied to {newHp} (x{state.Multiplier:0.##})"
            );
        } catch (Exception e) {
            Logger.Warn($"[BossHp] Could not multiply the HP set by an FSM: {e.Message}");
        }
    }
}
