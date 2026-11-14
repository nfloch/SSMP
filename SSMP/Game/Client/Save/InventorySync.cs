using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalEnums;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Live sharing of the inventory between players: tools and silk skills, crests, collectables, relics, mementos,
/// materium, quests, rumours and the hunter's journal.
/// <para>
/// Each game watches these PlayerData structures and sends a change per item (<see cref="InventoryItemUpdate"/>)
/// when its player gains something. The server relays it to the other players, whose games merge it into their own
/// PlayerData with the rules of <see cref="Fields"/>. The host's PlayerData is the common save: it is saved by the
/// host's game and copied to the players who log in (<see cref="LoginSaveCopy"/>).
/// </para>
/// <para>
/// Merge rules: an "obtained" property (unlocked, collected, deposited) is a union and never goes back. A property
/// that the game can undo (a quest accepted or completed, a tool hidden when replaced or consumed) takes the last
/// change. Collectable amounts are a shared pool: gains and spending are added up. Journal kills and quest counters
/// take the maximum, because each game already counts the synced enemies it sees die.
/// </para>
/// <para>
/// What stays per player (loadout, ammo, "seen" markers) is not sent as item changes. The loadout of a remote
/// player is stored by the server instead and applied over the host's copy at their next login
/// (<see cref="InventoryPersonalUpdate"/>).
/// </para>
/// </summary>
internal class InventorySync {
    /// <summary>
    /// The shared inventory structures. The value is sent in <see cref="InventoryItemUpdate.Field"/>.
    /// </summary>
    public enum Field : byte {
        Tools = 0,
        Crests = 1,
        Collectables = 2,
        Relics = 3,
        Mementos = 4,
        Materium = 5,
        Quests = 6,
        Rumours = 7,
        Journal = 8
    }

    /// <summary>
    /// How the count of an item is merged.
    /// </summary>
    private enum CountRule {
        None,
        /// <summary>Changes are sent as signed deltas and added up.</summary>
        Sum,
        /// <summary>The current value is sent, and the receiver keeps the maximum.</summary>
        Max
    }

    /// <summary>
    /// The shared part of an item: its boolean properties as bits, and its count.
    /// </summary>
    private struct ItemState : IEquatable<ItemState> {
        public byte Flags;
        public int Count;

        public bool Equals(ItemState other) => Flags == other.Flags && Count == other.Count;
    }

    /// <summary>
    /// Describes how one structure of PlayerData is shared.
    /// </summary>
    private sealed class FieldDef {
        public Field Id;

        /// <summary>The PlayerData field name, for the logs.</summary>
        public string Name = "";

        /// <summary>The names of the bits of <see cref="ItemState.Flags"/>, for the logs.</summary>
        public string[] BitNames = [];

        /// <summary>The bits that are a union: they only spread when they become true.</summary>
        public byte MonotonicBits;

        public CountRule CountRule;

        /// <summary>Gets the structure object from PlayerData, to detect when the game replaces it.</summary>
        public Func<PlayerData, object?> GetContainer = null!;

        /// <summary>Enumerates the shared state of all items of the structure.</summary>
        public Func<PlayerData, IEnumerable<KeyValuePair<string, ItemState>>> Enumerate = null!;

        /// <summary>Gets the shared state of one item.</summary>
        public Func<PlayerData, string, ItemState> Get = null!;

        /// <summary>Merges a change received from another player into the local game.</summary>
        public Action<PlayerData, InventoryItemUpdate> Apply = null!;
    }

    /// <summary>
    /// The interval in seconds at which the inventory is checked for changes.
    /// </summary>
    private const float CheckInterval = 0.5f;

    /// <summary>
    /// The interval in seconds at which the personal inventory is checked for changes.
    /// </summary>
    private const float PersonalCheckInterval = 3f;

    /// <summary>
    /// The shared structures and their merge rules.
    /// To stop sharing one of them, remove it from this list: it then stays per player after the login copy.
    /// </summary>
    private static readonly FieldDef[] Fields = CreateFields();

    private readonly NetClient _netClient;

    /// <summary>
    /// Whether the local player is the server host, who does not send a personal inventory (the host's own save
    /// holds it).
    /// </summary>
    private readonly Func<bool> _isHostingServer;

    /// <summary>
    /// Whether the save sent by the server at login has been applied, so the inventory is the player's own and its
    /// personal part can be sent.
    /// </summary>
    private readonly Func<bool> _isSaveApplied;

    /// <summary>
    /// The last known shared state of each item, per structure.
    /// </summary>
    private readonly Dictionary<Field, Dictionary<string, ItemState>> _snapshots = new();

    /// <summary>
    /// The structure objects the snapshots were taken from. The game replaces them when a save is loaded or the
    /// login copy is applied: the snapshot is then taken again without sending anything.
    /// </summary>
    private readonly Dictionary<Field, object?> _containers = new();

    private float _nextCheckTime;
    private float _nextPersonalCheckTime;

    /// <summary>
    /// The JSON of the personal inventory last sent to the server.
    /// </summary>
    private string? _lastPersonalJson;

    /// <summary>
    /// Whether the connected server uses full synchronisation, without which the inventory is not shared.
    /// </summary>
    public bool Enabled { get; set; }

    public InventorySync(NetClient netClient, Func<bool> isHostingServer, Func<bool> isSaveApplied) {
        _netClient = netClient;
        _isHostingServer = isHostingServer;
        _isSaveApplied = isSaveApplied;
    }

    /// <summary>
    /// Forgets all snapshots, for a new connection.
    /// </summary>
    public void Reset() {
        _snapshots.Clear();
        _containers.Clear();
        _lastPersonalJson = null;
    }

    /// <summary>
    /// Called every frame. Checks the inventory for changes at a fixed interval and sends them.
    /// </summary>
    public void Update() {
        if (!Enabled || !_netClient.IsConnected) {
            return;
        }

        var gm = global::GameManager.instance;
        var pd = PlayerData.instance;
        if (!gm || pd == null || gm.GameState == GameState.MAIN_MENU) {
            return;
        }

        if (Time.unscaledTime >= _nextCheckTime) {
            _nextCheckTime = Time.unscaledTime + CheckInterval;

            foreach (var def in Fields) {
                try {
                    CheckField(def, pd);
                } catch (Exception e) {
                    Logger.Warn($"[InvSync] Could not check '{def.Name}' for changes: {e}");
                }
            }
        }

        if (Time.unscaledTime >= _nextPersonalCheckTime) {
            _nextPersonalCheckTime = Time.unscaledTime + PersonalCheckInterval;

            try {
                CheckPersonal(pd);
            } catch (Exception e) {
                Logger.Warn($"[InvSync] Could not check the personal inventory: {e}");
            }
        }
    }

    /// <summary>
    /// Compares a structure with its snapshot and sends the changes of each item.
    /// </summary>
    private void CheckField(FieldDef def, PlayerData pd) {
        var container = def.GetContainer(pd);
        if (container == null) {
            return;
        }

        if (!_snapshots.TryGetValue(def.Id, out var snapshot) || !_containers.TryGetValue(def.Id, out var known) ||
            !ReferenceEquals(known, container)) {
            TakeSnapshot(def, pd, container);
            return;
        }

        var current = new Dictionary<string, ItemState>();
        foreach (var pair in def.Enumerate(pd)) {
            current[pair.Key] = pair.Value;

            snapshot.TryGetValue(pair.Key, out var last);
            if (!pair.Value.Equals(last)) {
                SendChange(def, pair.Key, last, pair.Value);
            }
        }

        // Items that disappeared from the structure are back to their default state
        foreach (var pair in snapshot) {
            if (!current.ContainsKey(pair.Key) && !pair.Value.Equals(default)) {
                SendChange(def, pair.Key, pair.Value, default);
            }
        }

        _snapshots[def.Id] = current;
    }

    /// <summary>
    /// Takes the snapshot of a structure without sending anything.
    /// </summary>
    private void TakeSnapshot(FieldDef def, PlayerData pd, object container) {
        var snapshot = new Dictionary<string, ItemState>();
        foreach (var pair in def.Enumerate(pd)) {
            snapshot[pair.Key] = pair.Value;
        }

        _snapshots[def.Id] = snapshot;
        _containers[def.Id] = container;

        Logger.Info($"[InvSync] Baseline of '{def.Name}': {snapshot.Count} items");
    }

    /// <summary>
    /// Sends the change of one item from its last to its current state, following the merge rules of its structure.
    /// </summary>
    private void SendChange(FieldDef def, string name, ItemState last, ItemState current) {
        var mask = (byte) (last.Flags ^ current.Flags);
        // A union property only spreads when it becomes true: losing it locally (a reset) is not shared
        mask &= (byte) ~(def.MonotonicBits & ~current.Flags);

        var hasCount = false;
        var count = 0;
        switch (def.CountRule) {
            case CountRule.Sum when current.Count != last.Count:
                hasCount = true;
                count = current.Count - last.Count;
                break;
            case CountRule.Max when current.Count > last.Count:
                hasCount = true;
                count = current.Count;
                break;
        }

        if (mask == 0 && !hasCount) {
            return;
        }

        var update = new InventoryItemUpdate {
            Field = (byte) def.Id,
            Name = name,
            Mask = mask,
            Flags = current.Flags,
            HasCount = hasCount,
            Count = count
        };

        _netClient.UpdateManager.SetInventoryItemUpdate(update);

        Logger.Info($"[InvSync] Sent {def.Name} '{name}': {Describe(def, update)}");
    }

    /// <summary>
    /// Merges a change made by another player into the local PlayerData.
    /// </summary>
    /// <param name="update">The item change relayed by the server.</param>
    public void Receive(InventoryItemUpdate update) {
        var def = Fields.FirstOrDefault(field => (byte) field.Id == update.Field);
        if (def == null) {
            Logger.Warn($"[InvSync] Received a change for a structure that is not shared: {update}");
            return;
        }

        var pd = PlayerData.instance;
        if (pd == null || def.GetContainer(pd) == null) {
            Logger.Warn($"[InvSync] Received {def.Name} '{update.Name}', but there is no PlayerData to merge it into");
            return;
        }

        try {
            // First send our own pending changes of this structure, so the merge does not hide them
            var gm = global::GameManager.instance;
            if (_netClient.IsConnected && Enabled && gm && gm.GameState != GameState.MAIN_MENU) {
                CheckField(def, pd);
            }

            var before = def.Get(pd, update.Name);
            def.Apply(pd, update);
            var after = def.Get(pd, update.Name);

            Logger.Info(
                $"[InvSync] Received {def.Name} '{update.Name}': {Describe(def, update)}, " +
                $"merged {DescribeState(def, before)} -> {DescribeState(def, after)}"
            );

            // The merged state is not a local change to send back
            if (_snapshots.TryGetValue(def.Id, out var snapshot) &&
                _containers.TryGetValue(def.Id, out var known) && ReferenceEquals(known, def.GetContainer(pd))) {
                snapshot[update.Name] = after;
            }
        } catch (Exception e) {
            Logger.Warn($"[InvSync] Could not merge {def.Name} '{update.Name}': {e}");
        }
    }

    #region Personal inventory

    /// <summary>
    /// The personal part of a player's inventory, stored by the server and applied at their next login.
    /// Each structure is the game's own JSON, like in the save file.
    /// </summary>
    [Serializable]
    private class PersonalInventory {
        /// <summary>Crests with their slots and equipped tools (crest unlocks are shared, slots are not).</summary>
        public string ToolEquips = "";

        /// <summary>Tools equipped in the extra slots.</summary>
        public string ExtraToolEquips = "";

        /// <summary>Refills of the liquid tools.</summary>
        public string ToolLiquids = "";

        /// <summary>Ammo and "seen" markers of the tools.</summary>
        public List<ToolPersonal> Tools = [];
    }

    [Serializable]
    private struct ToolPersonal {
        public string Name;
        public int AmountLeft;
        public bool HasBeenSeen;
        public bool HasBeenSelected;
    }

    /// <summary>
    /// Sends the personal inventory to the server when it changed. Only for remote players.
    /// </summary>
    private void CheckPersonal(PlayerData pd) {
        if (_isHostingServer() || !_isSaveApplied() || HeroController.instance == null ||
            pd.ToolEquips == null || pd.Tools == null) {
            return;
        }

        var personal = new PersonalInventory {
            ToolEquips = JsonUtility.ToJson(pd.ToolEquips),
            ExtraToolEquips = pd.ExtraToolEquips == null ? "" : JsonUtility.ToJson(pd.ExtraToolEquips),
            ToolLiquids = pd.ToolLiquids == null ? "" : JsonUtility.ToJson(pd.ToolLiquids),
            Tools = pd.Tools.Enumerate()
                .Select(pair => new ToolPersonal {
                    Name = pair.Key,
                    AmountLeft = pair.Value.AmountLeft,
                    HasBeenSeen = pair.Value.HasBeenSeen,
                    HasBeenSelected = pair.Value.HasBeenSelected
                })
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToList()
        };

        var json = JsonUtility.ToJson(personal);
        if (json == _lastPersonalJson) {
            return;
        }

        var bytes = LoginSaveCopy.Compress(json);
        if (bytes.Length > ushort.MaxValue) {
            Logger.Warn($"[InvSync] Personal inventory too large to send ({bytes.Length} bytes)");
            return;
        }

        _lastPersonalJson = json;
        _netClient.UpdateManager.SetInventoryPersonalUpdate(bytes);

        Logger.Info($"[InvSync] Sent personal inventory (loadout, ammo): {bytes.Length} bytes");
    }

    /// <summary>
    /// Applies the personal inventory stored by the server over the host's copy received at login.
    /// </summary>
    /// <param name="encoded">The gzipped JSON, empty if the server has none for this player yet.</param>
    public static void ApplyPersonal(byte[] encoded) {
        if (encoded.Length == 0) {
            Logger.Info("[InvSync] No personal inventory stored for this player, keeping the host's loadout");
            return;
        }

        var pd = PlayerData.instance;
        if (pd == null) {
            return;
        }

        var personal = JsonUtility.FromJson<PersonalInventory>(LoginSaveCopy.Decompress(encoded));

        var crests = 0;
        if (!string.IsNullOrEmpty(personal.ToolEquips) && pd.ToolEquips != null) {
            var personalCrests = JsonUtility.FromJson<ToolCrestsData>(personal.ToolEquips);
            foreach (var pair in personalCrests.Enumerate()) {
                var shared = pd.ToolEquips.GetData(pair.Key);
                var mine = pair.Value;

                // The crest is unlocked if anyone unlocked it, the slots and equipped tools are the player's
                mine.IsUnlocked |= shared.IsUnlocked;
                if (!mine.IsUnlocked) {
                    continue;
                }

                mine.Slots ??= shared.Slots;
                pd.ToolEquips.SetData(pair.Key, mine);
                crests++;
            }
        }

        if (!string.IsNullOrEmpty(personal.ExtraToolEquips)) {
            pd.ExtraToolEquips = JsonUtility.FromJson<FloatingCrestSlotsData>(personal.ExtraToolEquips);
        }

        if (!string.IsNullOrEmpty(personal.ToolLiquids)) {
            pd.ToolLiquids = JsonUtility.FromJson<ToolItemLiquidsData>(personal.ToolLiquids);
        }

        var tools = 0;
        if (pd.Tools != null) {
            foreach (var tool in personal.Tools) {
                var data = pd.Tools.GetData(tool.Name);
                if (!data.IsUnlocked) {
                    continue;
                }

                data.AmountLeft = tool.AmountLeft;
                data.HasBeenSeen = tool.HasBeenSeen;
                data.HasBeenSelected = tool.HasBeenSelected;
                pd.Tools.SetData(tool.Name, data);
                tools++;
            }
        }

        Logger.Info(
            $"[InvSync] Applied personal inventory at login: {crests} crest layouts, extra slots, liquids, " +
            $"{tools} tool amounts"
        );
    }

    #endregion

    #region Structures and merge rules

    private static bool Bit(byte flags, int bit) => (flags & (1 << bit)) != 0;

    private static bool Changed(InventoryItemUpdate update, int bit) => Bit(update.Mask, bit);

    private static bool Value(InventoryItemUpdate update, int bit) => Bit(update.Flags, bit);

    /// <summary>
    /// Merges a bit of a received change into a local value: a union bit only becomes true, another takes the
    /// received value.
    /// </summary>
    private static bool Merge(InventoryItemUpdate update, int bit, bool local, bool monotonic) {
        if (!Changed(update, bit)) {
            return local;
        }

        return monotonic ? local || Value(update, bit) : Value(update, bit);
    }

    private static byte Flags(params bool[] bits) {
        var flags = 0;
        for (var i = 0; i < bits.Length; i++) {
            if (bits[i]) {
                flags |= 1 << i;
            }
        }

        return (byte) flags;
    }

    /// <summary>
    /// Creates the definition of a structure based on the game's <see cref="SerializableNamedList{TData,TContainer}"/>.
    /// </summary>
    private static FieldDef NamedList<TData, TContainer>(
        Field id,
        string name,
        Func<PlayerData, SerializableNamedList<TData, TContainer>?> getList,
        Func<TData, ItemState> project,
        string[] bitNames,
        byte monotonicBits,
        CountRule countRule,
        Action<PlayerData, SerializableNamedList<TData, TContainer>, InventoryItemUpdate> apply
    ) where TContainer : SerializableNamedData<TData>, new() {
        return new FieldDef {
            Id = id,
            Name = name,
            BitNames = bitNames,
            MonotonicBits = monotonicBits,
            CountRule = countRule,
            GetContainer = getList,
            Enumerate = pd => getList(pd)?.Enumerate()
                                  .Select(pair => new KeyValuePair<string, ItemState>(pair.Key, project(pair.Value)))
                              ?? [],
            Get = (pd, itemName) => getList(pd) is { } list ? project(list.GetData(itemName)) : default,
            Apply = (pd, update) => {
                if (getList(pd) is { } list) {
                    apply(pd, list, update);
                }
            }
        };
    }

    private static FieldDef[] CreateFields() => [
        // Tools and silk skills: unlocked is a union, hidden (replaced by an upgrade or consumed) the last change.
        // The ammo (AmountLeft) and the "seen" markers stay per player.
        NamedList<ToolItemsData.Data, ToolItemsData.NamedData>(
            Field.Tools, "Tools", pd => pd.Tools,
            d => new ItemState { Flags = Flags(d.IsUnlocked, d.IsHidden) },
            ["unlocked", "hidden"], 0b01, CountRule.None,
            (_, list, update) => {
                var tool = ToolItemManager.GetToolByName(update.Name);
                var data = list.GetData(update.Name);

                if (Changed(update, 0) && Value(update, 0) && !data.IsUnlocked) {
                    if (tool) {
                        // Without popup, like a silent "Get": the game still marks it as new in the inventory
                        tool.Unlock(null, ToolItem.PopupFlags.None);
                    } else {
                        data.IsUnlocked = true;
                        list.SetData(update.Name, data);
                    }
                }

                if (Changed(update, 1)) {
                    data = list.GetData(update.Name);
                    var hidden = Value(update, 1);
                    if (hidden && !data.IsHidden && tool) {
                        // Also removes it from the crests, like the game does
                        tool.Lock();
                    } else if (hidden != data.IsHidden) {
                        data.IsHidden = hidden;
                        list.SetData(update.Name, data);
                    }
                }

                ToolItemManager.IncrementVersion();
            }
        ),
        // Crests: unlocked is a union. Slots and equipped tools stay per player.
        NamedList<ToolCrestsData.Data, ToolCrestsData.NamedData>(
            Field.Crests, "ToolEquips", pd => pd.ToolEquips,
            d => new ItemState { Flags = Flags(d.IsUnlocked) },
            ["unlocked"], 0b1, CountRule.None,
            (_, list, update) => {
                if (!Changed(update, 0) || !Value(update, 0) || list.GetData(update.Name).IsUnlocked) {
                    return;
                }

                var crest = ToolItemManager.GetCrestByName(update.Name);
                if (crest) {
                    // Creates the slots of the crest, or takes those of its previous version for an upgrade
                    crest.Unlock();
                    ToolItemManager.IncrementVersion();
                } else {
                    Logger.Warn($"[InvSync] Crest '{update.Name}' not found, not unlocked");
                }
            }
        ),
        // Collectables: a shared pool, gains and spending are added up. Never below zero.
        NamedList<CollectableItemsData.Data, CollectableItemsData.NamedData>(
            Field.Collectables, "Collectables", pd => pd.Collectables,
            d => new ItemState { Count = d.Amount },
            [], 0, CountRule.Sum,
            (_, list, update) => {
                if (!update.HasCount) {
                    return;
                }

                var data = list.GetData(update.Name);
                data.Amount = System.Math.Max(0, data.Amount + update.Count);
                list.SetData(update.Name, data);
                CollectableItemManager.IncrementVersion();
            }
        ),
        // Relics: collected and deposited are unions
        NamedList<CollectableRelicsData.Data, CollectableRelicsData.NamedData>(
            Field.Relics, "Relics", pd => pd.Relics,
            d => new ItemState { Flags = Flags(d.IsCollected, d.IsDeposited) },
            ["collected", "deposited"], 0b11, CountRule.None,
            (_, list, update) => {
                var data = list.GetData(update.Name);
                data.IsCollected = Merge(update, 0, data.IsCollected, true);
                data.IsDeposited = Merge(update, 1, data.IsDeposited, true);
                list.SetData(update.Name, data);
            }
        ),
        NamedList<CollectableMementosData.Data, CollectableMementosData.NamedData>(
            Field.Mementos, "MementosDeposited", pd => pd.MementosDeposited,
            d => new ItemState { Flags = Flags(d.IsDeposited) },
            ["deposited"], 0b1, CountRule.None,
            (_, list, update) => {
                var data = list.GetData(update.Name);
                data.IsDeposited = Merge(update, 0, data.IsDeposited, true);
                list.SetData(update.Name, data);
            }
        ),
        NamedList<MateriumItemsData.Data, MateriumItemsData.NamedData>(
            Field.Materium, "MateriumCollected", pd => pd.MateriumCollected,
            d => new ItemState { Flags = Flags(d.IsCollected) },
            ["collected"], 0b1, CountRule.None,
            (_, list, update) => {
                var data = list.GetData(update.Name);
                data.IsCollected = Merge(update, 0, data.IsCollected, true);
                list.SetData(update.Name, data);
            }
        ),
        // Quests: accepted and completed take the last change (the game resets them for repeatable quests and
        // cancels some), "ever completed" is a union, the counter takes the maximum. "Seen" stays per player.
        NamedList<QuestCompletionData.Completion, QuestCompletionData.NamedCompletion>(
            Field.Quests, "QuestCompletionData", pd => pd.QuestCompletionData,
            d => new ItemState {
                Flags = Flags(d.IsAccepted, d.IsCompleted, d.WasEverCompleted), Count = d.CompletedCount
            },
            ["accepted", "completed", "everCompleted"], 0b100, CountRule.Max,
            (_, list, update) => {
                var data = list.GetData(update.Name);
                data.IsAccepted = Merge(update, 0, data.IsAccepted, false);
                data.IsCompleted = Merge(update, 1, data.IsCompleted, false);
                data.WasEverCompleted = Merge(update, 2, data.WasEverCompleted, true);
                if (update.HasCount) {
                    data.CompletedCount = System.Math.Max(data.CompletedCount, update.Count);
                }

                list.SetData(update.Name, data);
                QuestManager.IncrementVersion();
            }
        ),
        NamedList<QuestRumourData.Data, QuestRumourData.NamedData>(
            Field.Rumours, "QuestRumourData", pd => pd.QuestRumourData,
            d => new ItemState { Flags = Flags(d.IsAccepted) },
            ["accepted"], 0, CountRule.None,
            (_, list, update) => {
                var data = list.GetData(update.Name);
                data.IsAccepted = Merge(update, 0, data.IsAccepted, false);
                list.SetData(update.Name, data);
                QuestManager.IncrementVersion();
            }
        ),
        // Journal: kills take the maximum, since every game records the kill of a synced enemy it sees die.
        // "Seen" stays per player.
        new FieldDef {
            Id = Field.Journal,
            Name = "EnemyJournalKillData",
            CountRule = CountRule.Max,
            GetContainer = pd => pd.EnemyJournalKillData,
            Enumerate = pd => pd.EnemyJournalKillData?.Dictionary?
                                  .Select(pair => new KeyValuePair<string, ItemState>(
                                      pair.Key, new ItemState { Count = pair.Value.Kills }
                                  ))
                              ?? [],
            Get = (pd, name) => new ItemState { Count = pd.EnemyJournalKillData?.GetKillData(name).Kills ?? 0 },
            Apply = (pd, update) => {
                var journal = pd.EnemyJournalKillData;
                if (journal?.Dictionary == null || !update.HasCount) {
                    return;
                }

                var data = journal.GetKillData(update.Name);
                if (update.Count > data.Kills) {
                    data.Kills = update.Count;
                    journal.RecordKillData(update.Name, data);
                }
            }
        }
    ];

    #endregion

    #region Logs

    private static string Describe(FieldDef def, InventoryItemUpdate update) {
        var sb = new StringBuilder();
        for (var bit = 0; bit < def.BitNames.Length; bit++) {
            if (Changed(update, bit)) {
                sb.Append($"{def.BitNames[bit]}={Value(update, bit)} ");
            }
        }

        if (update.HasCount) {
            sb.Append(def.CountRule == CountRule.Sum ? $"amount{update.Count:+0;-0} " : $"count={update.Count} ");
        }

        return sb.ToString().TrimEnd();
    }

    private static string DescribeState(FieldDef def, ItemState state) {
        var parts = new List<string>();
        for (var bit = 0; bit < def.BitNames.Length; bit++) {
            if (Bit(state.Flags, bit)) {
                parts.Add(def.BitNames[bit]);
            }
        }

        if (def.CountRule != CountRule.None) {
            parts.Add($"count={state.Count}");
        }

        return parts.Count == 0 ? "(none)" : string.Join(",", parts);
    }

    #endregion
}
