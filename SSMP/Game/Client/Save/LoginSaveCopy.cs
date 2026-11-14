using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Copies the host's inventory and progression structures to a player when they log in. These PlayerData fields
/// (tools, crests, collectables, quests, journal…) are complex types that are not handled by the generic save sync
/// (<c>Sync: false</c> in save-data.json), so without this a remote player would start every session with them
/// empty. After the login, they are shared live item by item by <see cref="InventorySync"/>, which merges every
/// player's gains into the host's PlayerData: the copy therefore holds what every player obtained. The personal
/// part (loadout, ammo) of the player is then applied over it.
/// </summary>
internal static class LoginSaveCopy {
    /// <summary>
    /// Names of the PlayerData fields to copy from the host at login.
    /// </summary>
    private static readonly HashSet<string> FieldNames = [
        "Tools",
        "ToolEquips",
        "ExtraToolEquips",
        "ToolLiquids",
        "Collectables",
        "Relics",
        "MementosDeposited",
        "MateriumCollected",
        "QuestCompletionData",
        "QuestRumourData",
        "EnemyJournalKillData"
    ];

    /// <summary>
    /// Whether the PlayerData variable with the given name is copied from the host at login.
    /// </summary>
    public static bool IsCopied(string name) => FieldNames.Contains(name);

    /// <summary>
    /// Encodes the copied fields of the local (host) PlayerData, keyed by their save data index.
    /// Each value is the field serialized with <see cref="JsonUtility"/>, like the game's own save, then gzipped.
    /// </summary>
    public static Dictionary<ushort, byte[]> EncodeFromLocalPlayerData() {
        var result = new Dictionary<ushort, byte[]>();

        var pd = PlayerData.instance;
        if (pd == null) {
            return result;
        }

        foreach (var name in FieldNames) {
            try {
                if (!SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(name, out var index)) {
                    Logger.Warn($"No save data index for '{name}', not copied at login");
                    continue;
                }

                var field = GetField(name);
                var value = field?.GetValue(pd);
                if (value == null) {
                    continue;
                }

                var bytes = Compress(JsonUtility.ToJson(value));
                if (bytes.Length > ushort.MaxValue) {
                    Logger.Warn($"'{name}' is too large to copy at login ({bytes.Length} bytes)");
                    continue;
                }

                result[index] = bytes;
            } catch (Exception e) {
                Logger.Warn($"Could not encode '{name}' for the login copy: {e.Message}");
            }
        }

        Logger.Info($"Copying {result.Count} inventory/progression structures from the host to the logging-in player");
        return result;
    }

    /// <summary>
    /// Applies a copied field received at login to the local PlayerData.
    /// </summary>
    public static void Apply(string name, byte[] encodedValue) {
        var field = GetField(name);
        if (field == null) {
            Logger.Warn($"PlayerData has no field '{name}', login copy skipped");
            return;
        }

        var value = JsonUtility.FromJson(Decompress(encodedValue), field.FieldType);
        field.SetValue(PlayerData.instance, value);
        Logger.Info($"Applied host copy of '{name}' at login");
    }

    private static FieldInfo? GetField(string name) =>
        typeof(PlayerData).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static byte[] Compress(string text) {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal)) {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    internal static string Decompress(byte[] bytes) {
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
