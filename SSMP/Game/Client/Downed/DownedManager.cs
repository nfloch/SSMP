using System.Collections;
using System.Collections.Generic;
using GlobalEnums;
using SSMP.Api.Client;
using SSMP.Game.Client.Encounters;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Downed;

/// <summary>
/// Death during an encounter, co-op rules of PRINCIPES.md ("Boss et arènes" 2, 3 and 8):
/// <list type="bullet">
/// <item>When the local hero would die inside an encounter in progress (arena or boss fight, on the scene host: the
/// shared notion <see cref="Encounter"/>) while another player is still alive inside it, the hero does not die: it is
/// downed for <see cref="DownedSeconds"/> seconds, hidden and inactive (no input, no hurt box, ignored by enemies),
/// with a grey screen, a countdown, and the camera on an alive teammate.</item>
/// <item>Then it comes back healed next to the arena, in the same scene (the hazard respawn sequence of the game),
/// with the gates open in its own game until it is back inside the arena, where they close again.</item>
/// <item>When nobody is left alive inside the arena (every player downed, or back outside waiting to return), every
/// such player really dies (vanilla death, cocoon, bench). Once the scene is empty, the server drops its stored
/// entity data, so the arena starts from scratch for the next player.</item>
/// <item>Outside a battle in progress, or without an alive teammate in the arena: vanilla death.</item>
/// </list>
/// Each player sends its state (<see cref="PlayerDeathStateType"/>) to the players of the scene, which hide a downed
/// player's object and keep it out of the enemies' targets.
/// </summary>
internal class DownedManager {
    /// <summary>How long a downed player stays down before coming back next to the arena.</summary>
    public const float DownedSeconds = 20f;

    /// <summary>
    /// How long nobody must be alive inside the arena before the real death of everyone, so that a teammate's
    /// position update or state packet arriving a little late does not kill a player.
    /// </summary>
    private const float NobodyAliveGraceSeconds = 0.5f;

    /// <summary>The wait between the fade out and the respawn, as the game's hazard respawn.</summary>
    private const float FadeOutSeconds = 0.65f;

    /// <summary>The interval at which the last grounded position outside any arena is recorded.</summary>
    private const float OutsideSampleSeconds = 0.25f;

    /// <summary>The name of the object holding the local player's name (see PlayerManager).</summary>
    private const string UsernameObjectName = "Username";

    /// <summary>The phase of the local hero.</summary>
    private enum Phase {
        /// <summary>Alive, no co-op death rule in progress.</summary>
        None,

        /// <summary>Downed: hidden and inactive, with the countdown.</summary>
        Downed,

        /// <summary>Fading out and coming back next to the arena.</summary>
        Respawning,

        /// <summary>Back next to the arena while its battle is in progress, gates open until it is back inside.</summary>
        Waiting
    }

    /// <summary>The net client, to send the state of the local player.</summary>
    private readonly NetClient _netClient;

    /// <summary>The data of the remote players, shared with the client manager.</summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>The last death state received from each remote player of the scene.</summary>
    private readonly Dictionary<ushort, PlayerDeathStateType> _remoteStates = new();

    /// <summary>The renderers and colliders of each downed remote player that were disabled to hide it.</summary>
    private readonly Dictionary<ushort, HiddenPlayer> _hiddenPlayers = new();

    /// <summary>The phase of the local hero.</summary>
    private Phase _phase;

    /// <summary>The encounter of the current downed or waiting phase.</summary>
    private Encounter? _battle;


    /// <summary>The unscaled time at which the downed phase ends.</summary>
    private float _downedEnd;

    /// <summary>The unscaled time since which nobody is alive inside the arena, or a negative value.</summary>
    private float _nobodyAliveSince = -1f;

    /// <summary>The remote player object the camera follows while downed.</summary>
    private GameObject? _followed;

    /// <summary>Whether the next call of HeroController.Die must be the vanilla death.</summary>
    private bool _realDeathRequested;

    /// <summary>The last state sent to the server.</summary>
    private PlayerDeathStateType _sentState = PlayerDeathStateType.Alive;

    /// <summary>The last grounded position of the local hero outside any arena in this scene.</summary>
    private Vector2? _lastOutsidePosition;

    /// <summary>The next unscaled time at which <see cref="_lastOutsidePosition"/> is sampled.</summary>
    private float _nextOutsideSample;

    public DownedManager(NetClient netClient, Dictionary<ushort, ClientPlayerData> playerData) {
        _netClient = netClient;
        _playerData = playerData;
    }

    /// <summary>
    /// Hooks the hero's death, the frame update and the scene changes.
    /// </summary>
    public void Initialize() {
        EventHooks.HeroControllerDieOverride = OnHeroDie;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    #region Local hero

    /// <summary>
    /// Called before the game runs HeroController.Die: returns a routine to run instead of the death when the hero is
    /// downed, or null for the vanilla death.
    /// </summary>
    private IEnumerator? OnHeroDie(HeroController hero, bool nonLethal, bool frostDeath) {
        if (_realDeathRequested) {
            _realDeathRequested = false;
            SendState(PlayerDeathStateType.Dead);
            return null;
        }

        if (_phase is Phase.Downed or Phase.Respawning) {
            // Already down: the hero cannot die again (death catch, damage that slipped through)
            return Nothing();
        }

        if (!_netClient.IsConnected) {
            return null;
        }

        var battle = EncounterTracker.FindInProgressAt(hero.transform.position);
        if (battle == null) {
            Logger.Info("[Downed] Death outside a battle in progress: vanilla death");
            SendState(PlayerDeathStateType.Dead);
            return null;
        }

        if (nonLethal) {
            Logger.Info($"[Downed] Non-lethal death in battle '{battle.Name}': vanilla handling");
            return null;
        }

        var teammate = FindAliveTeammateInArena(battle, out var teammateName);
        if (teammate == null) {
            Logger.Info($"[Downed] Death in battle '{battle.Name}' with no teammate alive in the arena: real death");
            SendState(PlayerDeathStateType.Dead);
            return null;
        }

        StartDowned(hero, battle, teammate, teammateName, frostDeath);
        return Nothing();
    }

    /// <summary>
    /// The routine run in place of the death.
    /// </summary>
    private static IEnumerator Nothing() {
        yield break;
    }

    /// <summary>
    /// Makes the local hero downed: hidden, inactive, ignored by enemies, with the grey screen and the camera on a
    /// teammate. Mirrors what the game does at the start of a hazard death, without its respawn.
    /// </summary>
    private void StartDowned(HeroController hero, Encounter battle, GameObject teammate, string teammateName,
        bool frostDeath) {
        _phase = Phase.Downed;
        _battle = battle;
        _downedEnd = Time.unscaledTime + DownedSeconds;
        _nobodyAliveSince = -1f;

        Logger.Info(
            $"[Downed] Local hero downed in battle '{battle.Name}' ({(frostDeath ? "frost, " : "")}" +
            $"teammate alive: {teammateName}), back in {DownedSeconds} s"
        );

        hero.CancelDamageRecoil();
        hero.SetState(ActorStates.no_input);
        hero.IgnoreInput();
        hero.ResetMotion();
        hero.ResetHardLandingTimer();
        hero.AffectedByGravity(false);
        hero.cState.hazardDeath = true;
        hero.playerData.disablePause = true;
        EventRegister.SendEvent(EventRegisterEvents.FsmCancel);
        EnforceDowned(hero);
        SetUsernameVisible(hero, false);

        PlayerTargetRegistry.SetUntargetable(hero.gameObject, true);
        SendState(PlayerDeathStateType.Downed);

        DownedOverlay.Get().Show(DownedSeconds);
        Follow(teammate, teammateName);
    }

    /// <summary>
    /// Keeps the downed hero hidden and still every frame: some game routines started before the death (end of a
    /// damage recoil) show the hero or give input back.
    /// </summary>
    private static void EnforceDowned(HeroController hero) {
        hero.renderer.enabled = false;
        hero.gameObject.layer = 2;
        hero.heroBox.HeroBoxOff();
        hero.cState.hazardDeath = true;
        hero.rb2d.linearVelocity = Vector2.zero;
        hero.rb2d.isKinematic = true;
        if (hero.acceptingInput) {
            hero.IgnoreInput();
        }
    }

    /// <summary>
    /// Every frame: the downed countdown, the return of a waiting player into the arena, the real death when nobody
    /// is left alive in the arena, and the last position outside the arenas.
    /// </summary>
    private void OnUpdate() {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        switch (_phase) {
            case Phase.Downed:
                UpdateDowned(hero);
                break;
            case Phase.Waiting:
                UpdateWaiting(hero);
                break;
            case Phase.None:
                SampleOutsidePosition(hero);
                break;
        }
    }

    /// <summary>
    /// Downed phase: countdown, camera, end of the battle, real death when nobody is left alive in the arena.
    /// </summary>
    private void UpdateDowned(HeroController hero) {
        EnforceDowned(hero);

        if (!_netClient.IsConnected) {
            Logger.Info("[Downed] Disconnected while downed, coming back now");
            Respawn(hero);
            return;
        }

        if (_battle == null || !_battle.IsInProgress) {
            Logger.Info("[Downed] The battle is no longer in progress, coming back now");
            Respawn(hero);
            return;
        }

        var teammate = FindAliveTeammateInArena(_battle, out var teammateName);
        if (teammate == null) {
            if (CheckNobodyAlive()) {
                RealDeath(hero, "downed");
            }

            return;
        }

        _nobodyAliveSince = -1f;
        Follow(teammate, teammateName);

        var secondsLeft = _downedEnd - Time.unscaledTime;
        DownedOverlay.Get().SetSecondsLeft(secondsLeft);
        if (secondsLeft <= 0f) {
            Logger.Info($"[Downed] {DownedSeconds} s are over, coming back next to battle '{_battle.Name}'");
            Respawn(hero);
        }
    }

    /// <summary>
    /// Waiting phase: back next to the arena, gates open in this game. Back inside the area, the arena locks again;
    /// with nobody alive inside any more, real death.
    /// </summary>
    private void UpdateWaiting(HeroController hero) {
        if (_battle == null || !_battle.IsInProgress) {
            Logger.Info("[Downed] The battle ended while waiting outside the arena");
            _phase = Phase.None;
            _battle = null;
            return;
        }

        if (hero.cState.hazardRespawning || hero.cState.transitioning || hero.cState.dead) {
            return;
        }

        if (_battle.Contains(hero.transform.position)) {
            Logger.Info($"[Downed] Back inside battle '{_battle.Name}', closing its gates behind the hero");
            _battle.LockLocally();
            _phase = Phase.None;
            _battle = null;
            return;
        }

        if (FindAliveTeammateInArena(_battle, out _) == null) {
            if (CheckNobodyAlive()) {
                RealDeath(hero, "waiting outside the arena");
            }
        } else {
            _nobodyAliveSince = -1f;
        }
    }

    /// <summary>
    /// Whether nobody has been alive inside the arena for long enough.
    /// </summary>
    private bool CheckNobodyAlive() {
        var now = Time.unscaledTime;
        if (_nobodyAliveSince < 0f) {
            _nobodyAliveSince = now;
            return false;
        }

        return now - _nobodyAliveSince >= NobodyAliveGraceSeconds;
    }

    /// <summary>
    /// Records the last grounded position of the hero outside any arena, a candidate spot to come back to.
    /// </summary>
    private void SampleOutsidePosition(HeroController hero) {
        var now = Time.unscaledTime;
        if (now < _nextOutsideSample) {
            return;
        }

        _nextOutsideSample = now + OutsideSampleSeconds;
        if (!hero.cState.onGround || hero.cState.hazardDeath || hero.cState.dead || hero.cState.transitioning ||
            hero.cState.hazardRespawning) {
            return;
        }

        Vector2 position = hero.transform.position;
        if (!EncounterTracker.IsInsideAny(position)) {
            _lastOutsidePosition = position;
        }
    }

    /// <summary>
    /// Brings the downed hero back, healed, next to the arena in the same scene, with the game's hazard respawn
    /// (fade, ground point, invulnerability). If the battle is still in progress, its gates open in this game until
    /// the hero is back inside.
    /// </summary>
    private void Respawn(HeroController hero) {
        var battle = _battle;
        _phase = Phase.Respawning;
        _nobodyAliveSince = -1f;

        StopFollowing();
        DownedOverlay.Get().Hide();

        var spot = ChooseRespawnSpot(hero, battle, out var source);
        var facingRight = battle != null ? battle.Center.x > spot.x : hero.cState.facingRight;
        hero.playerData.SetHazardRespawn(spot, facingRight);
        Logger.Info($"[Downed] Coming back at {spot} ({source}), healed to full health");

        hero.MaxHealth();

        if (battle != null && battle.IsInProgress) {
            Logger.Info($"[Downed] Opening the gates of battle '{battle.Name}' in this game until the hero is back in");
            battle.OpenForReentry();
        }

        MonoBehaviourUtil.Instance.StartCoroutine(RespawnRoutine(hero, battle));
    }

    /// <summary>
    /// Fades out, then runs the game's hazard respawn at the chosen spot.
    /// </summary>
    private IEnumerator RespawnRoutine(HeroController hero, Encounter? battle) {
        var gm = global::GameManager.instance;
        gm.screenFader_fsm.SendEventSafe("HAZARD FADE");
        yield return new WaitForSeconds(FadeOutSeconds);

        if (hero == null || _phase != Phase.Respawning) {
            yield break;
        }

        PlayerTargetRegistry.SetUntargetable(hero.gameObject, false);
        hero.rb2d.isKinematic = false;

        // Camera back on the hero, hero placed on the ground at the spot, shown, fade in, control given back
        gm.HazardRespawn();
        SetUsernameVisible(hero, true);
        SendState(PlayerDeathStateType.Alive);

        if (battle != null && battle.IsInProgress) {
            _phase = Phase.Waiting;
            _battle = battle;
        } else {
            _phase = Phase.None;
            _battle = null;
        }
    }

    /// <summary>
    /// Chooses where the hero comes back: a ground point outside the arena, from the hazard respawn point, the last
    /// position outside the arenas, or the respawn point of the gate the hero entered the scene from.
    /// </summary>
    private Vector2 ChooseRespawnSpot(HeroController hero, Encounter? battle, out string source) {
        var candidates = new List<(Vector2, string)> {
            (hero.playerData.hazardRespawnLocation, "hazard respawn point")
        };
        if (_lastOutsidePosition.HasValue) {
            candidates.Add((_lastOutsidePosition.Value, "last position outside the arena"));
        }

        var entryGate = hero.sceneEntryGate;
        if (entryGate != null && entryGate.respawnMarker != null) {
            candidates.Add((entryGate.respawnMarker.transform.position, $"entry gate '{entryGate.name}'"));
        }

        Vector2? firstGround = null;
        var firstGroundSource = "";
        foreach (var (position, name) in candidates) {
            if (!hero.TryFindGroundPoint(out var ground, position, true)) {
                continue;
            }

            if (battle == null || !battle.Contains(ground)) {
                source = name;
                return ground;
            }

            if (!firstGround.HasValue) {
                firstGround = ground;
                firstGroundSource = name + ", inside the arena";
            }
        }

        if (firstGround.HasValue) {
            source = firstGroundSource;
            return firstGround.Value;
        }

        source = "hazard respawn point, no ground found";
        return hero.playerData.hazardRespawnLocation;
    }

    /// <summary>
    /// Nobody is left alive in the arena: the vanilla death (cocoon at the downed spot, respawn at the bench).
    /// </summary>
    private void RealDeath(HeroController hero, string reason) {
        Logger.Info(
            $"[Downed] Nobody is left alive in battle '{(_battle != null ? _battle.Name : "?")}' ({reason}): " +
            "real death, the arena resets once the scene is empty"
        );

        StopFollowing();
        DownedOverlay.Get().Hide();
        PlayerTargetRegistry.SetUntargetable(hero.gameObject, false);
        SetUsernameVisible(hero, true);

        _phase = Phase.None;
        _battle = null;
        _nobodyAliveSince = -1f;

        hero.cState.hazardDeath = false;
        hero.playerData.health = 0;
        _realDeathRequested = true;
        hero.CheckDeathCatch();

        if (_realDeathRequested) {
            _realDeathRequested = false;
            Logger.Warn("[Downed] The game did not start the death of the hero");
        }
    }

    /// <summary>
    /// Points the camera at the given teammate (the camera target follows its transform in place of the hero's).
    /// </summary>
    private void Follow(GameObject teammate, string teammateName) {
        if (_followed == teammate) {
            return;
        }

        var cameraTarget = GameCameras.instance != null ? GameCameras.instance.cameraTarget : null;
        if (cameraTarget == null) {
            return;
        }

        Logger.Info($"[Downed] Camera following {teammateName}");
        _followed = teammate;
        cameraTarget.heroTransform = teammate.transform;
    }

    /// <summary>
    /// Points the camera back at the local hero.
    /// </summary>
    private void StopFollowing() {
        _followed = null;

        var cameraTarget = GameCameras.instance != null ? GameCameras.instance.cameraTarget : null;
        var hero = HeroController.instance;
        if (cameraTarget != null && hero != null) {
            cameraTarget.heroTransform = hero.transform;
        }
    }

    /// <summary>
    /// Shows or hides the name above the local hero.
    /// </summary>
    private static void SetUsernameVisible(HeroController hero, bool visible) {
        var username = hero.gameObject.FindGameObjectInChildren(UsernameObjectName);
        if (username != null) {
            username.SetActive(visible);
        }
    }

    /// <summary>
    /// Sends the death state of the local player to the players of the scene.
    /// </summary>
    private void SendState(PlayerDeathStateType state) {
        if (!_netClient.IsConnected || state == _sentState) {
            return;
        }

        _sentState = state;
        Logger.Info($"[Downed] Sending local death state {state}");
        _netClient.UpdateManager.SetDeathState(state);
    }

    /// <summary>
    /// Finds a remote player of the scene that is alive and inside the arena area.
    /// </summary>
    private GameObject? FindAliveTeammateInArena(Encounter battle, out string name) {
        foreach (var playerData in _playerData.Values) {
            var container = playerData.PlayerContainer;
            if (!playerData.IsInLocalScene || container == null || !container.activeInHierarchy) {
                continue;
            }

            if (_remoteStates.TryGetValue(playerData.Id, out var state) && state != PlayerDeathStateType.Alive) {
                continue;
            }

            var playerObject = playerData.PlayerObject != null ? playerData.PlayerObject : container;
            if (!battle.Contains(playerObject.transform.position)) {
                continue;
            }

            name = playerData.Username;
            return playerObject;
        }

        name = "";
        return null;
    }

    /// <summary>
    /// On a scene change: the co-op death rules are tied to a scene, they start over.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        if (_phase is Phase.Downed or Phase.Respawning) {
            Logger.Info("[Downed] Scene changed while downed");
            StopFollowing();
            DownedOverlay.Get().Hide();
            var hero = HeroController.instance;
            if (hero != null) {
                PlayerTargetRegistry.SetUntargetable(hero.gameObject, false);
                SetUsernameVisible(hero, true);
            }
        }

        _phase = Phase.None;
        _battle = null;
        _followed = null;
        _nobodyAliveSince = -1f;
        _lastOutsidePosition = null;

        ShowAllRemotePlayers();
        _remoteStates.Clear();

        // After a real death, the player respawns at a bench: alive again
        SendState(PlayerDeathStateType.Alive);
    }

    #endregion

    #region Remote players

    /// <summary>
    /// Called when another player's death state arrives: a downed player is hidden and ignored by enemies.
    /// </summary>
    public void OnPlayerDeathState(ClientPlayerDeathState data) {
        if (!_playerData.TryGetValue(data.Id, out var playerData)) {
            Logger.Warn($"[Downed] Death state {data.State} for unknown player {data.Id}");
            return;
        }

        Logger.Info($"[Downed] Player {data.Id} ({playerData.Username}) is now {data.State}");
        _remoteStates[data.Id] = data.State;

        if (data.State == PlayerDeathStateType.Downed) {
            HideRemotePlayer(playerData);
        } else {
            ShowRemotePlayer(data.Id);
        }
    }

    /// <summary>
    /// Called when another player enters the local scene: their state starts as alive, and they learn that the
    /// local player is downed.
    /// </summary>
    public void OnPlayerEnterScene(IClientPlayer player) {
        ShowRemotePlayer(player.Id);
        _remoteStates.Remove(player.Id);

        if (_phase == Phase.Downed && _netClient.IsConnected) {
            Logger.Info($"[Downed] Player {player.Id} entered the scene, sending again that we are downed");
            _netClient.UpdateManager.SetDeathState(PlayerDeathStateType.Downed);
        }
    }

    /// <summary>
    /// Called when another player leaves the local scene or disconnects: their object goes back to the pool, so it
    /// must be shown again.
    /// </summary>
    public void OnPlayerLeaveScene(IClientPlayer player) {
        ShowRemotePlayer(player.Id);
        _remoteStates.Remove(player.Id);
    }

    /// <summary>
    /// Called when the local player disconnects.
    /// </summary>
    public void OnLocalDisconnect() {
        ShowAllRemotePlayers();
        _remoteStates.Clear();
        _sentState = PlayerDeathStateType.Alive;
    }

    /// <summary>
    /// Hides a downed remote player: renderers (sprite, name, effects) and colliders off, ignored by enemies.
    /// </summary>
    private void HideRemotePlayer(ClientPlayerData playerData) {
        var container = playerData.PlayerContainer;
        if (container == null || _hiddenPlayers.ContainsKey(playerData.Id)) {
            return;
        }

        var hidden = new HiddenPlayer { PlayerObject = playerData.PlayerObject };
        foreach (var renderer in container.GetComponentsInChildren<Renderer>(true)) {
            if (renderer.enabled) {
                renderer.enabled = false;
                hidden.Renderers.Add(renderer);
            }
        }

        foreach (var collider in container.GetComponentsInChildren<Collider2D>(true)) {
            if (collider.enabled) {
                collider.enabled = false;
                hidden.Colliders.Add(collider);
            }
        }

        _hiddenPlayers[playerData.Id] = hidden;
        PlayerTargetRegistry.SetUntargetable(playerData.PlayerObject, true);
        Logger.Info($"[Downed] Hiding downed player {playerData.Id} ({playerData.Username})");
    }

    /// <summary>
    /// Shows a remote player hidden by <see cref="HideRemotePlayer"/> again.
    /// </summary>
    private void ShowRemotePlayer(ushort id) {
        if (!_hiddenPlayers.TryGetValue(id, out var hidden)) {
            return;
        }

        _hiddenPlayers.Remove(id);
        foreach (var renderer in hidden.Renderers) {
            if (renderer != null) {
                renderer.enabled = true;
            }
        }

        foreach (var collider in hidden.Colliders) {
            if (collider != null) {
                collider.enabled = true;
            }
        }

        PlayerTargetRegistry.SetUntargetable(hidden.PlayerObject, false);
        Logger.Info($"[Downed] Showing player {id} again");
    }

    /// <summary>
    /// Shows every hidden remote player again.
    /// </summary>
    private void ShowAllRemotePlayers() {
        foreach (var id in new List<ushort>(_hiddenPlayers.Keys)) {
            ShowRemotePlayer(id);
        }
    }

    /// <summary>
    /// The parts of a remote player disabled to hide it.
    /// </summary>
    private class HiddenPlayer {
        public GameObject? PlayerObject;
        public readonly List<Renderer> Renderers = [];
        public readonly List<Collider2D> Colliders = [];
    }

    #endregion
}
