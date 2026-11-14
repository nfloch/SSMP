using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Audio;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// <summary>
/// Manages boss fight music synchronisation across the network.
/// Note: This component relies heavily on PlayMakerFSM internals (via reflection on OnEnable)
/// and is fragile; it may break if game updates change PlayMaker structures or method signatures.
/// </summary>
/// <remarks>Manages boss fight music synchronisation across the network.</remarks>
internal class MusicComponent : EntityComponent {
    #region Constants

    /// <summary>
    /// The file path of the embedded resource file for music data.
    /// </summary>
    private const string MusicDataFilePath = "SSMP.Resource.music-data.json";

    #endregion

    #region Static data

    /// <summary>
    /// Maps music cue names to their corresponding data structures for fast lookup.
    /// </summary>
    private static readonly Dictionary<string, MusicCueData> MusicCueDataByName;

    /// <summary>
    /// Maps audio mixer snapshot names to their corresponding data structures for fast lookup.
    /// </summary>
    private static readonly Dictionary<string, AudioMixerSnapshotData> SnapshotDataByName;

    /// <summary>
    /// Maps network indices to their corresponding music cue data structures.
    /// </summary>
    private static readonly Dictionary<byte, MusicCueData> MusicCueDataByIndex;

    /// <summary>
    /// Maps network indices to their corresponding audio mixer snapshot data structures.
    /// </summary>
    private static readonly Dictionary<byte, AudioMixerSnapshotData> SnapshotDataByIndex;

    #endregion

    #region Static lifecycle state

    /// <summary>
    /// The live instances, one per entity whose FSMs play music (bosses). Each one only sends the music of its own
    /// entity's FSMs.
    /// </summary>
    private static readonly List<MusicComponent> Instances = [];

    /// <summary>
    /// Hook for intercepting PlayMakerFSM.OnEnable to resolve music cue and snapshot references.
    /// </summary>
    private static Hook? _fsmEnableHook;

    #endregion

    #region Instance state

    /// <summary>
    /// The index of the last played music cue, so we don't restart them unnecessarily.
    /// </summary>
    private byte _lastMusicCueIndex;

    /// <summary>
    /// The index of the last played audio snapshot, so we don't restart them unnecessarily.
    /// </summary>
    private byte _lastSnapshotIndex;

    #endregion

    #region Static construction

    /// <summary>
    /// Static constructor responsible for loading data from the JSON and registering static hooks.
    /// </summary>
    static MusicComponent() {
        var (cues, snapshots) = FileUtil.LoadObjectFromEmbeddedJson<
            (List<MusicCueData>, List<AudioMixerSnapshotData>)
        >(MusicDataFilePath);

        MusicCueDataByName = new Dictionary<string, MusicCueData>();
        SnapshotDataByName = new Dictionary<string, AudioMixerSnapshotData>();
        MusicCueDataByIndex = new Dictionary<byte, MusicCueData>();
        SnapshotDataByIndex = new Dictionary<byte, AudioMixerSnapshotData>();

        // Indices start at 1; 0 is reserved as "none".
        byte index = 1;

        foreach (var data in cues) {
            data.Index = index++;
            MusicCueDataByName[data.Name] = data;
            MusicCueDataByIndex[data.Index] = data;
        }

        foreach (var data in snapshots) {
            data.Index = index++;
            SnapshotDataByName[data.Name] = data;
            SnapshotDataByIndex[data.Index] = data;
        }
    }

    #endregion

    #region Instance construction

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicComponent"/> class.
    /// Registers event handlers for FSM audio actions.
    /// </summary>
    /// <param name="netClient">The net client used for synchronization.</param>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="gameObject">The GameObject associated with this component.</param>
    private MusicComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        CustomHooks.ApplyMusicCueFromFsmAction += OnApplyMusicCue;
        CustomHooks.TransitionToAudioSnapshotFromFsmAction += OnTransitionToAudioSnapshot;
        Instances.Add(this);
    }

    #endregion

    #region Instance management

    /// <summary>
    /// Creates an instance for an entity (the <c>Music</c> component of the entity registry).
    /// </summary>
    /// <param name="netClient">The NetClient instance for networking data.</param>
    /// <param name="entityId">The entity ID that this component is attached to.</param>
    /// <param name="gameObject">The host-client pair of game objects of the entity.</param>
    /// <param name="component">The created instance of MusicComponent.</param>
    /// <returns>Always true.</returns>
    public static bool TryCreateInstance(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        out MusicComponent? component
    ) {
        component = new MusicComponent(netClient, entityId, gameObject);
        return true;
    }

    /// <summary>
    /// Whether one of the given FSMs plays music (an <c>ApplyMusicCue</c> action), like the FSM of most bosses. Such
    /// entities get a MusicComponent without having to list it in the entity registry.
    /// </summary>
    public static bool HasMusicAction(IEnumerable<PlayMakerFSM> fsms) {
        foreach (var fsm in fsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is ApplyMusicCue) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets all instances, after their entities were destroyed on a scene change.
    /// </summary>
    public static void ClearInstance() => Instances.Clear();

    /// <summary>
    /// On the scene host, at the end of a battle: replaces the music stored by the server with "none", so a player
    /// entering the scene after the battle does not get the boss music.
    /// </summary>
    public static void ResetHostMusic() {
        foreach (var instance in Instances) {
            if (instance.IsControlled || instance._lastMusicCueIndex == 0 && instance._lastSnapshotIndex == 0) {
                continue;
            }

            Logger.Info("Battle ended, clearing the stored boss music");
            instance._lastMusicCueIndex = 0;
            instance._lastSnapshotIndex = 0;
            instance.SendMusicPacket(0, 0);
        }
    }

    /// <summary>
    /// Whether the FSM action belongs to this entity: its host object or one of its children.
    /// </summary>
    private bool IsOwnFsm(GameObject fsmObject) {
        var host = GameObject.Host;
        return host != null && fsmObject != null && fsmObject.transform.IsChildOf(host.transform);
    }

    #endregion

    #region Hook registration

    /// <summary>
    /// Register hooks for music-related operations.
    /// </summary>
    public static void RegisterHooks() {
        var method = typeof(PlayMakerFSM).GetMethod(
            "OnEnable",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance
        );

        if (method != null) {
            _fsmEnableHook = new Hook(method, OnFsmEnable);
        } else {
            Logger.Error("Could not find PlayMakerFSM.OnEnable method info.");
        }
    }

    /// <summary>
    /// Deregister hooks for music-related operations.
    /// </summary>
    public static void DeregisterHooks() {
        _fsmEnableHook?.Dispose();
        _fsmEnableHook = null;
    }

    #endregion

    #region Component lifecycle

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Destroy() {
        CustomHooks.ApplyMusicCueFromFsmAction -= OnApplyMusicCue;
        CustomHooks.TransitionToAudioSnapshotFromFsmAction -= OnTransitionToAudioSnapshot;
        Instances.Remove(this);
    }

    #endregion

    #region Host-side music capture

    /// <summary>
    /// Hook that is called when the AudioManager.ApplyMusicCue is called from an ApplyMusicCue FSM action.
    /// Used to network the starting of a music cue for the scene host.
    /// </summary>
    /// <param name="action">The ApplyMusicCue FSM action responsible for the call.</param>
    private void OnApplyMusicCue(ApplyMusicCue action) {
        Logger.Debug($"OnApplyMusicCue: {action.Fsm.GameObject.gameObject.name}, {action.Fsm.Name}");

        if (IsControlled) return;

        // The hook sees every FSM; only send the music of this entity (scene music regions are local to each player)
        if (!IsOwnFsm(action.Fsm.GameObject)) return;

        if (action.musicCue.Value is not MusicCue musicCue) return;

        if (!MusicCueDataByName.TryGetValue(musicCue.name, out var cueData)) {
            Logger.Debug($"  Music cue '{musicCue.name}' not in music-data.json");
            return;
        }

        Logger.Info($"Sending music cue '{musicCue.name}' from '{action.Fsm.GameObject.name}' as scene host");

        SendMusicPacket(cueData.Index, _lastSnapshotIndex);
        _lastMusicCueIndex = cueData.Index;
    }

    /// <summary>
    /// Hook that is called when the AudioMixerSnapshot.TransitionTo is called from a TransitionToAudioSnapshot FSM
    /// action. Used to network the starting of an audio snapshot for the scene host.
    /// </summary>
    /// <param name="action">The TransitionToAudioSnapshot FSM action responsible for the call.</param>
    private void OnTransitionToAudioSnapshot(TransitionToAudioSnapshot action) {
        Logger.Debug($"OnTransitionToAudioSnapshot: {action.Fsm.GameObject.gameObject.name}, {action.Fsm.Name}");

        // Door Control FSMs trigger snapshots independently of boss music; let them through.
        if (action.Fsm.Name.Equals("Door Control")) {
            Logger.Debug("  Door Control FSM, skipping");
            return;
        }

        if (IsControlled) return;

        // The hook sees every FSM, including the hero's own ones (e.g. 'Hero Damaged Effects' lowpass when the host
        // is hit). Only the snapshots of this entity's FSMs must reach the clients.
        if (!IsOwnFsm(action.Fsm.GameObject)) return;

        if (action.snapshot.Value is not AudioMixerSnapshot snapshot) return;

        if (!SnapshotDataByName.TryGetValue(snapshot.name, out var snapshotData)) {
            Logger.Debug($"  Audio mixer snapshot '{snapshot.name}' not in music-data.json");
            return;
        }

        Logger.Debug($"  Sending index: {snapshotData.Index}");

        SendMusicPacket(_lastMusicCueIndex, snapshotData.Index);
        _lastSnapshotIndex = snapshotData.Index;
    }

    /// <summary>
    /// Sends a network packet containing the updated music cue and audio snapshot indices.
    /// </summary>
    /// <param name="cueIndex">The network index of the music cue.</param>
    /// <param name="snapshotIndex">The network index of the audio snapshot.</param>
    private void SendMusicPacket(byte cueIndex, byte snapshotIndex) {
        var networkData = new EntityNetworkData { Type = EntityComponentType.Music };
        networkData.Packet.Write(cueIndex);
        networkData.Packet.Write(snapshotIndex);

        SendData(networkData);
    }

    #endregion

    #region Controlled-client music application

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        Logger.Debug("Update MusicComponent");

        if (!IsControlled) {
            Logger.Debug("  Not controlled, skipping");
            return;
        }

        var cueIndex = data.Packet.ReadByte();
        var snapshotIndex = data.Packet.ReadByte();

        Logger.Debug($"  Received indices - cue: {cueIndex}, snapshot: {snapshotIndex}");

        if (alreadyInSceneUpdate && cueIndex == 0 && snapshotIndex == 0) {
            // Music cleared by the scene host at the end of the battle: keep the scene's own music
            return;
        }

        if (alreadyInSceneUpdate) {
            Logger.Info("Boss music is playing on the scene host, applying it on entering the scene");
        }

        if (cueIndex != _lastMusicCueIndex) {
            ApplyIndex(cueIndex);
            _lastMusicCueIndex = cueIndex;
        }

        if (snapshotIndex != _lastSnapshotIndex) {
            ApplyIndex(snapshotIndex);
            _lastSnapshotIndex = snapshotIndex;
        }
    }

    /// <summary>
    /// Resolves and applies either a music cue or an audio snapshot matching the given network index.
    /// </summary>
    /// <param name="index">The network index to apply.</param>
    private static void ApplyIndex(byte index) {
        if (MusicCueDataByIndex.TryGetValue(index, out var cueData)) {
            if (cueData.MusicCue == null) {
                Logger.Debug($"  Music cue '{cueData.Name}' indexed but not resolved in-scene");
                return;
            }

            Logger.Info($"Applying music cue '{cueData.Name}' from scene host");
            global::GameManager.instance.AudioManager.ApplyMusicCue(cueData.MusicCue, 0f, 0f, false);
            return;
        }

        if (SnapshotDataByIndex.TryGetValue(index, out var snapshotData)) {
            if (snapshotData.Snapshot == null) {
                Logger.Debug($"  Audio snapshot '{snapshotData.Name}' indexed but not resolved in-scene");
                return;
            }

            Logger.Debug($"  Transitioning to audio snapshot ({snapshotData.Name})");
            snapshotData.Snapshot.TransitionTo(0f);
            return;
        }

        Logger.Debug($"  No cue or snapshot found for index {index}");
    }

    #endregion

    #region FSM reference resolution

    /// <summary>
    /// Hook for when an FSM becomes enabled. Used to check for ApplyMusicCue or TransitionToAudioSnapshot actions
    /// such that their audio data can be resolved.
    /// </summary>
    /// <param name="orig">The original method delegate.</param>
    /// <param name="self">The PlayMakerFSM instance being enabled.</param>
    private static void OnFsmEnable(Action<PlayMakerFSM> orig, PlayMakerFSM self) {
        orig(self);

        foreach (var state in self.FsmStates) {
            foreach (var action in state.Actions) {
                switch (action) {
                    case ApplyMusicCue applyMusicCue: {
                        if (applyMusicCue.musicCue.Value is not MusicCue musicCue) continue;

                        Logger.Debug($"Found music cue '{musicCue.name}' in FSM '{self.Fsm.Name}' / '{state.Name}'");

                        if (MusicCueDataByName.TryGetValue(musicCue.name, out var cueData)) {
                            Logger.Debug($"  Resolved - type: {cueData.Type}");
                            cueData.MusicCue = musicCue;
                        }

                        break;
                    }

                    case TransitionToAudioSnapshot snapshotAction: {
                        if (snapshotAction.snapshot.Value is not AudioMixerSnapshot snapshot) continue;

                        Logger.Debug(
                            $"Found audio snapshot '{snapshot.name}' in FSM '{self.Fsm.Name}' / '{state.Name}'"
                        );

                        if (SnapshotDataByName.TryGetValue(snapshot.name, out var snapshotData)) {
                            Logger.Debug($"  Resolved - type: {snapshotData.Type}");
                            snapshotData.Snapshot = snapshot;
                        }

                        break;
                    }
                }
            }
        }
    }

    #endregion
}

#region Data containers

// ReSharper disable ClassNeverInstantiated.Global
/// <summary>
/// Data for music cues, used for looking up the index or the music cue from index for networking purposes.
/// </summary>
internal class MusicCueData {
    public string Type { get; }
    public string Name { get; }

    [JsonConstructor]
    public MusicCueData(string type, string name) {
        Type = type;
        Name = name;
    }

    [JsonIgnore] public byte Index { get; set; }

    [JsonIgnore] public MusicCue? MusicCue { get; set; }
}

// ReSharper disable ClassNeverInstantiated.Global
/// <summary>
/// Data for audio snapshots, used for looking up the index or the audio snapshot from index for networking purposes.
/// </summary>
internal class AudioMixerSnapshotData {
    public string Type { get; }
    public string Name { get; }

    [JsonConstructor]
    public AudioMixerSnapshotData(string type, string name) {
        Type = type;
        Name = name;
    }

    [JsonIgnore] public byte Index { get; set; }

    [JsonIgnore] public AudioMixerSnapshot? Snapshot { get; set; }
}

#endregion
