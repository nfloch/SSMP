using System.Collections.Generic;
using SSMP.Fsm;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component keeps the visual state of an entity the same as on the scene host: physics layer, Z position, tint
/// of its tk2d sprite and which of its own children are active. Battle enemies that emerge from the background (the
/// skull brute of Bone_01's last wave: z 3.2, grey tint, a 'Hidden Group' of skulls, layer 2 then 14 then 11) change
/// all four through their FSM.
/// <para>
/// The FSM actions are replayed only to the players already in the scene, and the Z position they set does not last
/// on a scene client: position updates carry X and Y only and take Z from the inactive host object, which stays at
/// the scene's background Z. This component sends the whole state as entity data that the server stores, so the
/// players in the scene and those entering later (late arrival, reconnection) all get the current state.
/// </para>
/// Replaces <c>LayerComponent</c> (layer only) and <c>ZPositionComponent</c> (Z only, for a few registry entities).
internal class VisualStateComponent : EntityComponent {
    /// <summary>
    /// At most this many children are tracked (bits of the mask).
    /// </summary>
    private const int MaxChildren = 32;

    /// <summary>
    /// The tk2d sprite of the host and client objects, if any.
    /// </summary>
    private readonly tk2dBaseSprite? _hostSprite;
    private readonly tk2dBaseSprite? _clientSprite;

    /// <summary>
    /// The tracked children of the host object, and the client children at the same index with the same name (null
    /// if there is none). Children that are entities of their own (FSM, health) are left out: their state is theirs.
    /// </summary>
    private readonly List<GameObject> _hostChildren = new();
    private readonly List<GameObject?> _clientChildren = new();

    /// <summary>
    /// The last state that was sent or received.
    /// </summary>
    private State _last;

    public VisualStateComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        var host = gameObject.Host;
        var client = gameObject.Client;
        if (host == null) {
            return;
        }

        _hostSprite = host.GetComponent<tk2dBaseSprite>();
        _clientSprite = client != null ? client.GetComponent<tk2dBaseSprite>() : null;

        var clientChildren = client != null ? client.GetChildren() : new List<GameObject>();
        var hostChildren = host.GetChildren();
        for (var i = 0; i < hostChildren.Count && _hostChildren.Count < MaxChildren; i++) {
            var child = hostChildren[i];
            if (child.GetComponent<PlayMakerFSM>() != null || child.GetComponent<HealthManager>() != null) {
                continue;
            }

            _hostChildren.Add(child);
            _clientChildren.Add(
                i < clientChildren.Count && clientChildren[i].name.StartsWith(child.name) ? clientChildren[i] : null
            );
        }

        _last = Read(host);
    }

    /// <inheritdoc />
    /// The scene host sends its current state once, so the server stores it even if the FSM changed it before this
    /// component existed (an enemy put in the background as soon as the scene loads).
    protected override void InitializeHost() {
        if (GameObject.Host == null) {
            return;
        }

        _last = Read(GameObject.Host);
        Send(_last);
    }

    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var state = Read(GameObject.Host);
        if (state.Equals(_last)) {
            return;
        }

        _last = state;
        Send(state);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var packet = data.Packet;
        var state = new State {
            Layer = packet.ReadByte(),
            Z = packet.ReadFloat(),
            HasColor = packet.ReadBool()
        };
        if (state.HasColor) {
            state.Color = new Color32(packet.ReadByte(), packet.ReadByte(), packet.ReadByte(), packet.ReadByte());
        }

        state.ChildCount = packet.ReadByte();
        if (state.ChildCount > 0) {
            state.ChildMask = packet.ReadUInt();
        }

        _last = state;

        if (alreadyInSceneUpdate && GameObject.Client != null) {
            var current = Read(GameObject.Client, _clientSprite, _clientChildren);
            if (!current.Equals(state)) {
                Logger.Info(
                    $"Entity '{GameObject.Client.name}' has visual state {state} on the scene host " +
                    $"(was {current}), applying it"
                );
            }
        }

        Apply(GameObject.Host, _hostSprite, _hostChildren, state);
        Apply(GameObject.Client, _clientSprite, _clientChildren, state);
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// Sends the given state.
    /// </summary>
    private void Send(State state) {
        var data = new EntityNetworkData {
            Type = EntityComponentType.VisualState
        };
        var packet = data.Packet;
        packet.Write((byte) state.Layer);
        packet.Write(state.Z);
        packet.Write(state.HasColor);
        if (state.HasColor) {
            packet.Write(state.Color.r);
            packet.Write(state.Color.g);
            packet.Write(state.Color.b);
            packet.Write(state.Color.a);
        }

        packet.Write((byte) state.ChildCount);
        if (state.ChildCount > 0) {
            packet.Write(state.ChildMask);
        }

        SendData(data);
    }

    /// <summary>
    /// Reads the state of the host object.
    /// </summary>
    private State Read(GameObject host) => Read(host, _hostSprite, _hostChildren);

    /// <summary>
    /// Reads the state of an object with its sprite and tracked children.
    /// </summary>
    private static State Read(GameObject obj, tk2dBaseSprite? sprite, IReadOnlyList<GameObject?> children) {
        var state = new State {
            Layer = obj.layer,
            Z = obj.transform.position.z,
            HasColor = sprite != null,
            ChildCount = children.Count
        };
        if (sprite != null) {
            state.Color = sprite.color;
        }

        for (var i = 0; i < children.Count; i++) {
            var child = children[i];
            if (child != null && child.activeSelf) {
                state.ChildMask |= 1u << i;
            }
        }

        return state;
    }

    /// <summary>
    /// Applies the given state to an object with its sprite and tracked children.
    /// </summary>
    private static void Apply(
        GameObject? obj,
        tk2dBaseSprite? sprite,
        IReadOnlyList<GameObject?> children,
        State state
    ) {
        if (obj == null) {
            return;
        }

        obj.layer = state.Layer;

        // The client object's interpolation writes Z every frame from the last position packet: move it there too
        var position = obj.transform.position;
        if (obj.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
            interpolation.SnapZ(state.Z);
        } else if (!position.z.Equals(state.Z)) {
            obj.transform.position = new Vector3(position.x, position.y, state.Z);
        }

        if (sprite != null && state.HasColor) {
            sprite.color = state.Color;
        }

        // Same children on both sides only: a count that differs means another build or a changed object
        if (state.ChildCount != children.Count) {
            return;
        }

        for (var i = 0; i < children.Count; i++) {
            var child = children[i];
            var active = (state.ChildMask & (1u << i)) != 0;
            if (child != null && child.activeSelf != active) {
                child.SetActive(active);
            }
        }
    }

    /// <summary>
    /// The visual state of an entity.
    /// </summary>
    private struct State {
        public int Layer;
        public float Z;
        public bool HasColor;
        public Color32 Color;
        public int ChildCount;
        public uint ChildMask;

        public bool Equals(State other) =>
            Layer == other.Layer &&
            Z.Equals(other.Z) &&
            HasColor == other.HasColor &&
            (!HasColor || (Color.r == other.Color.r && Color.g == other.Color.g && Color.b == other.Color.b &&
                           Color.a == other.Color.a)) &&
            ChildCount == other.ChildCount &&
            ChildMask == other.ChildMask;

        public override string ToString() =>
            $"layer={Layer} z={Z:0.###}" + (HasColor ? $" color=({Color.r},{Color.g},{Color.b},{Color.a})" : "") +
            (ChildCount > 0 ? $" children=0x{ChildMask:X}" : "");
    }
}
