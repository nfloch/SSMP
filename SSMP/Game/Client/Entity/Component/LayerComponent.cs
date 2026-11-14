using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the physics layer of an entity. Battle enemies that emerge from the background (the skull
/// brute of Bone_01: layer 2 while hiding, then 14, then 11) only collide with players and attacks once they are back
/// on the enemy layer. The <c>SetLayer</c> FSM actions reach the players already in the scene, but the server does not
/// keep FSM actions for players entering the scene later. This component sends the layer as entity data that the
/// server stores, so a player entering during the battle gets the current layer.
internal class LayerComponent : EntityComponent {
    /// <summary>
    /// The last layer of the host object.
    /// </summary>
    private int _lastLayer;

    public LayerComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _lastLayer = gameObject.Host != null ? gameObject.Host.layer : 0;
    }

    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var newLayer = GameObject.Host.layer;
        if (newLayer == _lastLayer) {
            return;
        }

        _lastLayer = newLayer;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Layer
        };
        data.Packet.Write((byte) newLayer);

        SendData(data);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var newLayer = data.Packet.ReadByte();
        _lastLayer = newLayer;

        if (GameObject.Client != null && GameObject.Client.layer != newLayer) {
            if (alreadyInSceneUpdate) {
                Logger.Info(
                    $"Entity '{GameObject.Client.name}' is on layer {newLayer} on the scene host " +
                    $"(was {GameObject.Client.layer}), applying it"
                );
            }

            GameObject.Client.layer = newLayer;
        }

        if (GameObject.Host != null) {
            GameObject.Host.layer = newLayer;
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
