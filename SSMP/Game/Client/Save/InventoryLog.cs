using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Writes the local player's inventory, equipment and abilities to the log, to compare both players' state when
/// debugging a session (each player's log holds their own). Logged when the game is entered and at each bench rest.
/// </summary>
internal static class InventoryLog {
    /// <summary>
    /// Integer PlayerData fields worth logging (health, silk, currencies, upgrades).
    /// </summary>
    private static readonly string[] IntFields = [
        "maxHealth", "maxHealthBase", "health", "silkMax", "silk", "silkRegenMax", "geo", "ShellShards",
        "nailUpgrades", "ToolPouchUpgrades", "ToolKitUpgrades"
    ];

    /// <summary>
    /// String PlayerData fields worth logging (crest, bench).
    /// </summary>
    private static readonly string[] StringFields = ["CurrentCrestID", "respawnScene", "respawnMarkerName"];

    /// <summary>
    /// Complex PlayerData fields, logged as the game's own JSON (same as in the save file).
    /// </summary>
    private static readonly string[] JsonFields = ["Tools", "ToolEquips", "ExtraToolEquips", "Collectables", "Relics"];

    /// <summary>
    /// Logs the inventory of the local player.
    /// </summary>
    /// <param name="reason">Why it is logged, written in the first line.</param>
    public static void Dump(string reason) {
        try {
            var pd = PlayerData.instance;
            if (pd == null) {
                return;
            }

            var type = typeof(PlayerData);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var sb = new StringBuilder();
            sb.Append($"[Inventory] {reason}");

            foreach (var name in IntFields.Concat(StringFields)) {
                var field = type.GetField(name, flags);
                if (field != null) {
                    sb.Append($" {name}={field.GetValue(pd)}");
                }
            }

            Logger.Info(sb.ToString());

            // Abilities and unlocks: every bool PlayerData field starting with "has" that is true
            var abilities = new List<string>();
            foreach (var field in type.GetFields(flags)) {
                if (field.FieldType == typeof(bool) && field.Name.StartsWith("has", StringComparison.Ordinal) &&
                    (bool) field.GetValue(pd)) {
                    abilities.Add(field.Name);
                }
            }

            Logger.Info($"[Inventory]   abilities ({abilities.Count}): {string.Join(", ", abilities)}");

            foreach (var name in JsonFields) {
                var value = type.GetField(name, flags)?.GetValue(pd);
                if (value != null) {
                    Logger.Info($"[Inventory]   {name}: {JsonUtility.ToJson(value)}");
                }
            }
        } catch (Exception e) {
            Logger.Warn($"[Inventory] Could not log the inventory: {e.Message}");
        }
    }
}
