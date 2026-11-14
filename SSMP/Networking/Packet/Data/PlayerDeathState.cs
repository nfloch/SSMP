namespace SSMP.Networking.Packet.Data;

/// <summary>
/// The death state of a player during an arena battle (co-op rules of PRINCIPES.md, "Boss et arènes" 2 and 3).
/// </summary>
internal enum PlayerDeathStateType : byte {
    /// <summary>The player is alive (also sent once a downed player is back).</summary>
    Alive = 0,

    /// <summary>
    /// The player would have died in a battle while a teammate was still alive in the arena: hidden and inactive for
    /// a while, then back next to the arena.
    /// </summary>
    Downed = 1,

    /// <summary>The player really died (vanilla death: cocoon, respawn at a bench).</summary>
    Dead = 2
}

/// <summary>
/// Packet data sent by a player to the server for a change of their death state. Replaces the former empty
/// <c>PlayerDeath</c> data, which no game sent.
/// </summary>
internal class ServerPlayerDeathState : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;

    /// <summary>The new death state.</summary>
    public PlayerDeathStateType State { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write((byte) State);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        State = (PlayerDeathStateType) packet.ReadByte();
    }
}

/// <summary>
/// Packet data relayed by the server to the players in the same scene: the death state of another player.
/// </summary>
internal class ClientPlayerDeathState : GenericClientData {
    /// <summary>The new death state.</summary>
    public PlayerDeathStateType State { get; set; }

    public ClientPlayerDeathState() {
        IsReliable = true;
        DropReliableDataIfNewerExists = false;
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        base.WriteData(packet);
        packet.Write((byte) State);
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        base.ReadData(packet);
        State = (PlayerDeathStateType) packet.ReadByte();
    }
}
