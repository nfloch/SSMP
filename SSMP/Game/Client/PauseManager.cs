using System.Collections;
using System.Reflection;
using GlobalEnums;
using MonoMod.RuntimeDetour;
using SSMP.Hooks;
using SSMP.Networking.Client;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Handles pause-related behavior while connected to a server.
/// </summary>
/// <remarks>
/// Vanilla freezes the world (time scale 0) when the pause menu or the inventory opens, through
/// <c>GameManager.SetPausedState</c>. While connected, the world must keep running: the scene host simulates the
/// enemies for everyone, and remote players and entities are moved with the scaled frame time. So after vanilla
/// pauses, time is put back to 1 through <see cref="TimeManager"/> (not <c>Time.timeScale</c> directly, which
/// <see cref="TimeManager"/> overwrites with its own value on its next recompute). While the pause menu is open,
/// Hornet gets no input, so her horizontal speed is cancelled every frame: she stands still instead of gliding on
/// the velocity she had when the game was paused.
/// </remarks>
internal sealed class PauseManager {
    /// <summary>
    /// Minimum positive time scale accepted by the vanilla time-scale logic.
    /// Smaller values are treated as paused.
    /// </summary>
    private const float MinPositiveTimeScale = 0.00999999977648258f;

    /// <summary>
    /// Binding flags used to access vanilla instance members regardless of visibility.
    /// </summary>
    private const BindingFlags InstanceMemberFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Whether the vanilla pause menu is currently open while connected to a server.
    /// </summary>
    public static bool IsMultiplayerPauseMenuOpen { get; private set; }

    /// <summary>
    /// The net client used to determine whether multiplayer pause handling should be active.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Hook for <c>GameManager.SetPausedState</c>, the single place where vanilla freezes and unfreezes time for
    /// the pause menu and the inventory.
    /// </summary>
    private Hook? _gameManagerSetPausedStateHook;

    /// <summary>
    /// Hook for <c>TransitionPoint.OnTriggerEnter2D</c>.
    /// </summary>
    private Hook? _transitionPointOnTriggerEnter2DHook;

    /// <summary>
    /// Hook for <c>HeroController.DieFromHazard</c>.
    /// </summary>
    private Hook? _heroControllerDieFromHazardHook;

    /// <summary>
    /// Creates a new pause manager.
    /// </summary>
    /// <param name="netClient">The client used to determine multiplayer connection state.</param>
    public PauseManager(NetClient netClient) {
        _netClient = netClient;
    }

    /// <summary>
    /// Registers pause-related hooks.
    /// </summary>
    public void RegisterHooks() {
        _gameManagerSetPausedStateHook = new Hook(
            typeof(global::GameManager).GetMethod("SetPausedState", InstanceMemberFlags)!,
            GameManagerOnSetPausedState
        );

        _transitionPointOnTriggerEnter2DHook = new Hook(
            typeof(TransitionPoint).GetMethod("OnTriggerEnter2D", InstanceMemberFlags)!,
            TransitionPointOnTriggerEnter2D
        );

        _heroControllerDieFromHazardHook = new Hook(
            typeof(HeroController).GetMethod("DieFromHazard", InstanceMemberFlags)!,
            HeroControllerOnDieFromHazard
        );

        EventHooks.HeroControllerDie += HeroControllerDieHook;
        EventHooks.HeroControllerUpdate += HeroControllerOnUpdate;
    }

    /// <summary>
    /// Deregisters pause-related hooks.
    /// </summary>
    public void DeregisterHooks() {
        _gameManagerSetPausedStateHook?.Dispose();
        _gameManagerSetPausedStateHook = null;

        _transitionPointOnTriggerEnter2DHook?.Dispose();
        _transitionPointOnTriggerEnter2DHook = null;

        _heroControllerDieFromHazardHook?.Dispose();
        _heroControllerDieFromHazardHook = null;

        EventHooks.HeroControllerDie -= HeroControllerDieHook;
        EventHooks.HeroControllerUpdate -= HeroControllerOnUpdate;

        IsMultiplayerPauseMenuOpen = false;
    }

    /// <summary>
    /// Original <c>GameManager.SetPausedState</c> delegate signature.
    /// </summary>
    /// <param name="self">The game manager instance.</param>
    /// <param name="value">Whether the game is being paused.</param>
    private delegate void OrigSetPausedState(global::GameManager self, bool value);

    /// <summary>
    /// Detour for <c>GameManager.SetPausedState</c>.
    /// Lets vanilla pause or unpause (audio, camera shake, pause event), then keeps the world running while
    /// connected. <c>isPaused</c> is set before this call for the pause menu and stays false for the inventory.
    /// </summary>
    /// <param name="orig">The original vanilla method.</param>
    /// <param name="self">The game manager instance.</param>
    /// <param name="value">Whether the game is being paused.</param>
    private void GameManagerOnSetPausedState(OrigSetPausedState orig, global::GameManager self, bool value) {
        orig(self, value);

        if (!_netClient.IsConnected) {
            IsMultiplayerPauseMenuOpen = false;
            return;
        }

        IsMultiplayerPauseMenuOpen = value && self.isPaused;

        if (value) {
            SetTimeScale(1f);
        }

        Logger.Info(
            $"[Pause] {(value ? (self.isPaused ? "pause menu opened" : "inventory opened") : "closed")} while connected, " +
            $"Time.timeScale={Time.timeScale}"
        );
    }

    /// <summary>
    /// Callback on <c>HeroController.Update</c>. While the pause menu is open, vanilla stops reading input
    /// (<c>acceptingInput = false</c>), and <c>HeroController.Move</c> then leaves the horizontal speed untouched,
    /// so Hornet would keep gliding at her running speed with the idle animation. Cancel that speed instead.
    /// Dashes and recoils set their own speed and end by themselves.
    /// </summary>
    /// <param name="hero">The hero controller instance.</param>
    private static void HeroControllerOnUpdate(HeroController hero) {
        if (!IsMultiplayerPauseMenuOpen) {
            return;
        }

        var cState = hero.cState;
        if (cState.dashing || cState.backDashing || cState.recoiling || cState.transitioning || cState.dead) {
            return;
        }

        var body = hero.Body;
        if (body == null || body.linearVelocity.x == 0f) {
            return;
        }

        body.linearVelocity = new Vector2(0f, body.linearVelocity.y);
    }

    /// <summary>
    /// Event callback fired when the hero dies.
    /// </summary>
    /// <param name="nonLethal">Whether the death was non-lethal.</param>
    /// <param name="frostDeath">Whether the death was caused by frost.</param>
    private void HeroControllerDieHook(bool nonLethal, bool frostDeath) {
        ImmediateUnpauseIfPaused();
    }

    /// <summary>
    /// Original <c>HeroController.DieFromHazard</c> delegate signature.
    /// </summary>
    /// <param name="self">The hero controller instance.</param>
    /// <param name="hazardType">The hazard type that caused the death.</param>
    /// <param name="angle">The hazard impact angle.</param>
    /// <returns>The original hazard death coroutine.</returns>
    private delegate IEnumerator OrigDieFromHazard(HeroController self, HazardType hazardType, float angle);

    /// <summary>
    /// Detour for <c>HeroController.DieFromHazard</c>.
    /// Forces an immediate unpause before starting the hazard death coroutine.
    /// </summary>
    /// <param name="orig">The original vanilla method.</param>
    /// <param name="self">The hero controller instance.</param>
    /// <param name="hazardType">The hazard type that caused the death.</param>
    /// <param name="angle">The hazard impact angle.</param>
    /// <returns>The original hazard death coroutine.</returns>
    private IEnumerator HeroControllerOnDieFromHazard(
        OrigDieFromHazard orig,
        HeroController self,
        HazardType hazardType,
        float angle
    ) {
        ImmediateUnpauseIfPaused();
        return orig(self, hazardType, angle);
    }

    /// <summary>
    /// Original <c>TransitionPoint.OnTriggerEnter2D</c> delegate signature.
    /// </summary>
    /// <param name="self">The transition point instance.</param>
    /// <param name="obj">The collider that entered the transition trigger.</param>
    private delegate void OrigOnTriggerEnter2D(TransitionPoint self, Collider2D obj);

    /// <summary>
    /// Detour for <c>TransitionPoint.OnTriggerEnter2D</c>.
    /// Forces an immediate unpause before non-door scene transitions.
    /// </summary>
    /// <param name="orig">The original vanilla method.</param>
    /// <param name="self">The transition point instance.</param>
    /// <param name="obj">The collider that entered the transition trigger.</param>
    private void TransitionPointOnTriggerEnter2D(
        OrigOnTriggerEnter2D orig,
        TransitionPoint self,
        Collider2D obj
    ) {
        if (!self.isADoor) {
            ImmediateUnpauseIfPaused();
        }

        orig(self, obj);
    }


    /// <summary>
    /// Unpauses the game immediately if the UI is currently in the paused state.
    /// Used for death, hazard death, and scene transitions where the pause menu must not remain open.
    /// </summary>
    private static void ImmediateUnpauseIfPaused() {
        IsMultiplayerPauseMenuOpen = false;

        if (UIManager.instance == null || UIManager.instance.uiState != UIState.PAUSED) {
            SetTimeScale(1f);
            return;
        }

        var gameManager = global::GameManager.instance;

        gameManager.gameCams.ResumeCameraShake();
        gameManager.inputHandler.PreventPause();
        gameManager.actorSnapshotUnpaused.TransitionTo(0f);
        gameManager.isPaused = false;
        gameManager.ui.AudioGoToGameplay(0.2f);
        gameManager.ui.SetState(UIState.PLAYING);
        gameManager.SetState(GameState.PLAYING);

        if (HeroController.instance != null) {
            HeroController.instance.UnPause();
        }

        MenuButtonList.ClearAllLastSelected();
        gameManager.inputHandler.AllowPause();

        SetTimeScale(1f);
    }

    /// <summary>
    /// Puts back the vanilla frozen time when the client disconnects while the pause menu or the inventory is open.
    /// </summary>
    public static void RestoreVanillaTimeOnDisconnect() {
        IsMultiplayerPauseMenuOpen = false;

        var gameManager = global::GameManager.UnsafeInstance;
        if (gameManager != null && (gameManager.isPaused || PlayerData.instance is { isInventoryOpen: true })) {
            SetTimeScale(0f);
        }
    }

    /// <summary>
    /// Sets the game time scale through <see cref="TimeManager"/>, with the same lower bound as
    /// <c>GameManager.SetTimeScale</c>. Writing <c>Time.timeScale</c> directly would only last until the next
    /// <see cref="TimeManager"/> recompute (camera shake freeze frames, time controls), which puts its own stored
    /// value back, i.e. 0 after a vanilla pause.
    /// </summary>
    /// <param name="timeScale">The requested time scale.</param>
    private static void SetTimeScale(float timeScale) {
        TimeManager.TimeScale = timeScale > MinPositiveTimeScale ? timeScale : 0f;
    }
}
