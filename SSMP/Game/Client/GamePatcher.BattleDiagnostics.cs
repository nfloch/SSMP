using System;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Temporary diagnostic for arenas (<see cref="BattleScene"/>). Each game starts its own arena when its local hero
/// enters the trigger, and the arena then sends its start and end events (e.g. "ALARM RING" / "ALARM END" for the
/// alarm bell of Room_Forge) to local objects only. Logs when and how each game starts and ends an arena, so that a
/// report such as "the bell did not ring for me" can be matched with the other player's log.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// Hook for <see cref="BattleScene.StartBattle"/>, called by the arena trigger (local hero only) or by an FSM.
    /// </summary>
    private void BattleSceneOnStartBattleDiagnostic(Action<BattleScene> orig, BattleScene self) {
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
    /// Hook for <c>BattleScene.OnTriggerEnter2D</c>: on a scene client, the local hero entering the trigger does not
    /// start the arena. The scene host's arena rules, through <see cref="Entity.Component.BattleComponent"/>.
    /// </summary>
    private static void BattleSceneOnTriggerEnter2D(
        Action<BattleScene, UnityEngine.Collider2D> orig,
        BattleScene self,
        UnityEngine.Collider2D collision
    ) {
        if (collision != null && collision.CompareTag("Player") &&
            Entity.Component.BattleComponent.ShouldIgnoreTrigger(self)) {
            return;
        }

        orig(self, collision);
    }

    /// <summary>
    /// Hook for <see cref="BattleScene.DoEndBattle"/>, called when the last wave is cleared or by
    /// <c>SaveManager</c> when the scene host completed the arena.
    /// </summary>
    private void BattleSceneOnDoEndBattleDiagnostic(Action<BattleScene> orig, BattleScene self) {
        try {
            Logger.Info(
                $"[BattleDiag] DoEndBattle '{GetPath(self.transform)}' in '{self.gameObject.scene.name}': " +
                $"started={self.started}, completed={self.completed}, wave={self.currentWave}"
            );
        } catch (Exception e) {
            Logger.Warn($"Battle diagnostic failed on end: {e.Message}");
        }

        orig(self);
    }
}
