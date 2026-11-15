using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The map markers placed by hand (<c>PlayerData.placedMarkers</c>), seen as items of <see cref="InventorySync"/>.
/// <para>
/// <c>placedMarkers</c> holds one list of map positions per <see cref="MapMarkerMenu.MarkerTypes"/>, and the game
/// allows at most <see cref="MaxPerType"/> markers per type: the remaining count is <c>9 - list.Count</c>, so sharing
/// the lists also shares the remaining count.
/// </para>
/// <para>
/// One item per marker, named by its type and its position rounded to the hundredth of a map unit
/// (<c>A:1234:-567</c>). Every game derives the name from its own float with the same rounding, and a received marker
/// is stored at the rounded position, so a removal finds the right marker on every game. Markers of the same type at
/// the same rounded position get an occurrence suffix (<c>A:1234:-567#1</c>), so stacked markers are counted.
/// </para>
/// </summary>
internal static class MapMarkerItems {
    /// <summary>
    /// The number of markers of each type the game allows, see <c>MapMarkerMenu.PlaceMarker</c>.
    /// </summary>
    public const int MaxPerType = 9;

    /// <summary>
    /// The rounding of the positions in the item names: hundredths of a map unit.
    /// </summary>
    private const float Precision = 100f;

    private static readonly MethodInfo? UpdateAmountsMethod =
        typeof(MapMarkerMenu).GetMethod("UpdateAmounts", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Gets the marker lists of PlayerData, completed like the game does when the map opens, so the structure
    /// object stays the same from one check to the next.
    /// </summary>
    public static WrappedVector2List[]? GetLists(PlayerData pd) {
        ArrayForEnumAttribute.EnsureArraySize(ref pd.placedMarkers, typeof(MapMarkerMenu.MarkerTypes));
        var lists = pd.placedMarkers;
        if (lists == null) {
            return null;
        }

        for (var i = 0; i < lists.Length; i++) {
            lists[i] ??= new WrappedVector2List();
        }

        return lists;
    }

    /// <summary>
    /// Enumerates the item names of all placed markers.
    /// </summary>
    public static IEnumerable<string> Enumerate(PlayerData pd) {
        var lists = GetLists(pd);
        if (lists == null) {
            return [];
        }

        return Enumerable.Range(0, lists.Length).SelectMany(type => Names(type, lists[type].List));
    }

    /// <summary>
    /// Whether the marker with the given item name is placed.
    /// </summary>
    public static bool IsPlaced(PlayerData pd, string name) {
        var lists = GetLists(pd);
        return lists != null && TryParse(name, lists.Length, out var type, out var key, out var occurrence) &&
               lists[type].List.Count(pos => Key(type, pos) == key) > occurrence;
    }

    /// <summary>
    /// Places or removes the marker with the given item name, then refreshes the map if it is open.
    /// When the list of its type is already full, the list keeps the <see cref="MaxPerType"/> markers with the
    /// smallest names: every game holding the same markers drops the same one, so they agree again.
    /// </summary>
    public static void Apply(PlayerData pd, string name, bool placed) {
        var lists = GetLists(pd);
        if (lists == null || !TryParse(name, lists.Length, out var type, out var key, out var occurrence)) {
            Logger.Warn($"[InvSync] Invalid placed marker '{name}', ignored");
            return;
        }

        var list = lists[type].List;
        var count = list.Count(pos => Key(type, pos) == key);

        if (placed && count <= occurrence) {
            if (list.Count >= MaxPerType) {
                var dropped = Names(type, list).Append(name).OrderBy(n => n, StringComparer.Ordinal).Last();
                if (dropped == name) {
                    Logger.Info(
                        $"[InvSync] placedMarkers type {(MapMarkerMenu.MarkerTypes) type} is full " +
                        $"({list.Count}/{MaxPerType}), received marker '{name}' not placed (largest name)"
                    );
                    return;
                }

                RemoveOne(type, list, dropped);
                Logger.Info(
                    $"[InvSync] placedMarkers type {(MapMarkerMenu.MarkerTypes) type} is full " +
                    $"({MaxPerType}/{MaxPerType}), local marker '{dropped}' removed to place '{name}' (largest name)"
                );
            }

            list.Add(Position(key));
        } else if (!placed && count > occurrence) {
            RemoveOne(type, list, name);
        } else {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// The item names of the markers of a list, with an occurrence suffix for markers at the same rounded position.
    /// </summary>
    private static IEnumerable<string> Names(int type, List<Vector2> list) {
        var seen = new Dictionary<string, int>();
        foreach (var pos in list) {
            var key = Key(type, pos);
            seen.TryGetValue(key, out var occurrence);
            seen[key] = occurrence + 1;
            yield return occurrence == 0 ? key : $"{key}#{occurrence}";
        }
    }

    /// <summary>
    /// Removes one marker matching the rounded position of the item name (the last one, like the occurrences).
    /// </summary>
    private static void RemoveOne(int type, List<Vector2> list, string name) {
        var key = name.Split('#')[0];
        var index = list.FindLastIndex(pos => Key(type, pos) == key);
        if (index >= 0) {
            list.RemoveAt(index);
        }
    }

    private static string Key(int type, Vector2 pos) =>
        $"{(MapMarkerMenu.MarkerTypes) type}:{Mathf.RoundToInt(pos.x * Precision)}:{Mathf.RoundToInt(pos.y * Precision)}";

    /// <summary>
    /// The position stored for a received marker: the rounded one, which gives back the same name.
    /// </summary>
    private static Vector2 Position(string key) {
        var parts = key.Split(':');
        return new Vector2(int.Parse(parts[1]) / Precision, int.Parse(parts[2]) / Precision);
    }

    private static bool TryParse(string name, int typeCount, out int type, out string key, out int occurrence) {
        type = 0;
        occurrence = 0;
        key = "";

        var hash = name.Split('#');
        if (hash.Length > 2 || hash.Length == 2 && (!int.TryParse(hash[1], out occurrence) || occurrence < 1)) {
            return false;
        }

        var parts = hash[0].Split(':');
        if (parts.Length != 3 || !Enum.TryParse<MapMarkerMenu.MarkerTypes>(parts[0], out var markerType) ||
            !int.TryParse(parts[1], out _) || !int.TryParse(parts[2], out _)) {
            return false;
        }

        type = (int) markerType;
        key = hash[0];
        return type >= 0 && type < typeCount;
    }

    /// <summary>
    /// Shows a received change on the map and in the marker menu when they are open. Otherwise the game draws the
    /// markers from PlayerData the next time the map opens.
    /// </summary>
    private static void Refresh() {
        try {
            var gameMap = global::GameManager.instance ? global::GameManager.instance.gameMap : null;
            if (gameMap && gameMap!.gameObject.activeInHierarchy) {
                gameMap.SetupMapMarkers();
            }

            var menu = Object.FindAnyObjectByType<MapMarkerMenu>();
            if (!menu) {
                return;
            }

            // A removed marker is hidden by SetupMapMarkers: it must not stay the one the cursor would remove
            foreach (var marker in menu.collidingMarkers.ToList()) {
                if (!marker || !marker.activeInHierarchy) {
                    menu.RemoveFromCollidingList(marker);
                }
            }

            UpdateAmountsMethod?.Invoke(menu, null);
        } catch (Exception e) {
            Logger.Warn($"[InvSync] Could not refresh the map markers: {e.Message}");
        }
    }
}
