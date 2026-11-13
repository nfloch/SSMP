using System;
using SSMP.Game.Client.Entity;
using SSMP.Game.Command.Server;
using SSMP.Networking.Client;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Bug markers for debugging sessions. Pressing <see cref="Key"/> sends "/bug" to the server, which announces a
/// numbered marker naming the reporting player and their scene. Every game that receives the marker, including the
/// reporter's, writes a snapshot of its own state to its log: scene, scene host, local player and all entities.
/// </summary>
internal class BugReport {
    /// <summary>
    /// The key that reports a bug.
    /// </summary>
    private const KeyCode Key = KeyCode.F8;

    /// <summary>
    /// The net client, to send the marker to the server.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The entity manager, to describe the entities of the scene.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// The number of local snapshots taken without a server, used when not connected.
    /// </summary>
    private int _localNumber;

    public BugReport(NetClient netClient, EntityManager entityManager) {
        _netClient = netClient;
        _entityManager = entityManager;
    }

    /// <summary>
    /// Starts listening to the bug report key.
    /// </summary>
    public void Initialize() {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Handles a received chat message; writes a snapshot if it is a bug marker.
    /// </summary>
    /// <param name="message">The chat message.</param>
    public void OnChatMessage(string message) {
        if (message.StartsWith(BugCommand.MessagePrefix, StringComparison.Ordinal)) {
            WriteSnapshot(message);
        }
    }

    private void OnUpdate() {
        if (!Input.GetKeyDown(Key)) {
            return;
        }

        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetChatMessage("/bug");
        } else {
            // No server to number and broadcast it: only mark our own log
            WriteSnapshot($"{BugCommand.MessagePrefix}local {++_localNumber}] by the local player (not connected)");
        }
    }

    /// <summary>
    /// Writes the state of this game to the log, under the given marker.
    /// </summary>
    private void WriteSnapshot(string marker) {
        try {
            Logger.Info($"{marker} ===== snapshot of this game");

            var scene = SceneManager.GetActiveScene().name;
            Logger.Info($"[BUG]   scene={scene} sceneHost={_entityManager.IsSceneHost}");

            var hero = HeroController.instance;
            var pd = PlayerData.instance;
            if (hero != null && pd != null) {
                var pos = hero.transform.position;
                Logger.Info(
                    $"[BUG]   local player pos=({pos.x:F1}, {pos.y:F1}) health={pd.health}/{pd.maxHealth} " +
                    $"silk={pd.silk}/{pd.silkMax}"
                );
            }

            var count = 0;
            foreach (var entity in _entityManager.ActiveEntities) {
                Logger.Info($"[BUG]   entity {entity.DescribeForBugReport()}");
                count++;
            }

            Logger.Info($"[BUG]   {count} entities ===== end of snapshot");
        } catch (Exception e) {
            Logger.Warn($"[BUG] Could not write the snapshot: {e.Message}");
        }
    }
}
