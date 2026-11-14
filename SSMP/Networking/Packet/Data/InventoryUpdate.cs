using System;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for a change to one item of the shared inventory (a tool, crest, collectable, relic, quest, journal
/// entry…). Sent by the player whose game changed it, relayed by the server to the other players, who merge it into
/// their own PlayerData. The meaning of the fields depends on <see cref="Field"/>, see
/// <c>SSMP.Game.Client.Save.InventorySync</c>.
/// </summary>
internal class InventoryItemUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The inventory structure the item belongs to (index in <c>InventorySync.Field</c>).
    /// </summary>
    public byte Field { get; set; }

    /// <summary>
    /// The name of the item in the structure (tool name, collectable name, quest name…).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Bit mask of the boolean properties of the item that changed.
    /// </summary>
    public byte Mask { get; set; }

    /// <summary>
    /// The current values of the boolean properties of the item, as bits.
    /// </summary>
    public byte Flags { get; set; }

    /// <summary>
    /// Whether <see cref="Count"/> holds a value to merge.
    /// </summary>
    public bool HasCount { get; set; }

    /// <summary>
    /// The count of the item: a signed change for shared amounts (collectables), the current value for counts merged
    /// with the maximum (journal kills, quest counter).
    /// </summary>
    public int Count { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Field);
        packet.Write(Name);
        packet.Write(Mask);
        packet.Write(Flags);
        packet.Write(HasCount);
        if (HasCount) {
            packet.Write(Count);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Field = packet.ReadByte();
        Name = packet.ReadString();
        Mask = packet.ReadByte();
        Flags = packet.ReadByte();
        HasCount = packet.ReadBool();
        Count = HasCount ? packet.ReadInt() : 0;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"field {Field}, '{Name}', mask {Mask}, flags {Flags}" + (HasCount ? $", count {Count}" : "");
}

/// <summary>
/// Packet data for the personal part of a player's inventory (crest slots and equipped tools, tool ammo, liquids),
/// sent by a remote player to the server, which stores it for their next login. Gzipped JSON.
/// </summary>
internal class InventoryPersonalUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    /// <summary>
    /// The encoded personal inventory.
    /// </summary>
    public byte[] Value { get; set; } = [];

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        if (Value.Length > ushort.MaxValue) {
            throw new Exception($"Personal inventory is too large: {Value.Length} bytes");
        }

        packet.Write((ushort) Value.Length);
        for (var i = 0; i < Value.Length; i++) {
            packet.Write(Value[i]);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        var length = packet.ReadUShort();
        Value = new byte[length];
        for (var i = 0; i < length; i++) {
            Value[i] = packet.ReadByte();
        }
    }
}
