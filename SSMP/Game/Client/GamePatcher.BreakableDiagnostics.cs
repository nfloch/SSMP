using System;
using System.Collections.Generic;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Temporary diagnostic for breakables that ignore hits (e.g. the "One Way VineWall" in Tut_02, which neither host
/// nor client could break). Logs every <see cref="Breakable.Hit"/> with its result and, when the hit is ignored, the
/// guard of the vanilla method that rejected it.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// Minimum interval, in seconds, between two diagnostic lines for the same breakable, since a single swing can
    /// call <see cref="Breakable.Hit"/> several times.
    /// </summary>
    private const float BreakableLogIntervalSeconds = 0.5f;

    /// <summary>
    /// Last diagnostic log time per breakable instance ID.
    /// </summary>
    private static readonly Dictionary<int, float> BreakableLastLogTimes = new();

    /// <summary>
    /// Hook for <see cref="Breakable.Hit"/> that logs the hit result and why the hit was ignored, if it was.
    /// The reason is computed before calling the original method, since a successful hit changes the state.
    /// </summary>
    private IHitResponder.HitResponse BreakableOnHitDiagnostic(
        Func<Breakable, HitInstance, IHitResponder.HitResponse> orig,
        Breakable self,
        HitInstance damageInstance
    ) {
        string? reason = null;
        try {
            reason = GetBreakableRejectReason(self, damageInstance);
        } catch (Exception e) {
            Logger.Warn($"Breakable diagnostic failed before hit: {e.Message}");
        }

        var response = orig(self, damageInstance);

        try {
            LogBreakableHit(self, damageInstance, response, reason);
        } catch (Exception e) {
            Logger.Warn($"Breakable diagnostic failed after hit: {e.Message}");
        }

        return response;
    }

    /// <summary>
    /// Mirrors the early-return guards of <see cref="Breakable.Hit"/>, in the same order.
    /// </summary>
    /// <returns>The guard that rejects the hit, or <see langword="null"/> if the hit goes through.</returns>
    private static string? GetBreakableRejectReason(Breakable self, HitInstance damageInstance) {
        if (self.firstHitOnly && !damageInstance.IsFirstHit) return "firstHitOnly";
        if (self.ignoreDamagers) return "ignoreDamagers";
        if (!damageInstance.IsNailDamage && damageInstance.IsManualTrigger) return "manualTrigger";
        if (Time.timeAsDouble <= self.hitCooldownEndTime) return "hitCooldown";

        var range = self.breakableRange;
        if (range && range.isActiveAndEnabled && !range.IsInside) {
            return $"breakableRange '{range.name}' empty (InsideCount={range.InsideCount})";
        }

        if (self.isBroken) return "isBroken";

        foreach (var required in self.requireBroken ?? []) {
            if (required && !required.isBroken) {
                return $"requireBroken '{GetPath(required.transform)}' not broken";
            }
        }

        return null;
    }

    /// <summary>
    /// Logs one line per hit, rate-limited per breakable.
    /// </summary>
    private static void LogBreakableHit(
        Breakable self,
        HitInstance damageInstance,
        IHitResponder.Response response,
        string? reason
    ) {
        var id = self.GetInstanceID();
        var now = Time.unscaledTime;
        if (BreakableLastLogTimes.TryGetValue(id, out var last) && now - last < BreakableLogIntervalSeconds) {
            return;
        }

        BreakableLastLogTimes[id] = now;

        var range = self.breakableRange;
        var rangeState = range
            ? $"{range.name} active={range.isActiveAndEnabled} inside={range.IsInside}"
            : "none";

        Logger.Info(
            $"[BreakableDiag] '{GetPath(self.transform)}' in '{self.gameObject.scene.name}': " +
            $"response={response}, reason={reason ?? "-"}, " +
            $"remote={RemoteAttackComponent.IsRemoteAttack(damageInstance.Source)}, " +
            $"attack={damageInstance.AttackType}, nail={damageInstance.IsNailDamage}, " +
            $"isBroken={self.isBroken}, wasAlreadyBroken={self.wasAlreadyBroken}, " +
            $"range=[{rangeState}], requireBroken={self.requireBroken?.Length ?? 0}"
        );
    }

    /// <summary>
    /// Full hierarchy path of a transform, to tell apart objects that share a name.
    /// </summary>
    private static string GetPath(Transform transform) {
        var path = transform.name;
        for (var parent = transform.parent; parent; parent = parent.parent) {
            path = $"{parent.name}/{path}";
        }

        return path;
    }
}
