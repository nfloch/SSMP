using GlobalEnums;
using TeamCherry.Localization;
using UnityEngine;
using UnityEngine.UI;
using UnityStandardAssets.ImageEffects;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Downed;

/// <summary>
/// The screen of a downed player (see <see cref="DownedManager"/>): the game's colours desaturated (saturation of the
/// camera colour correction, the one the scenes and the game's memory sequences animate), a light grey veil over it,
/// and the remaining time before the player is back.
/// </summary>
internal class DownedOverlay : MonoBehaviour {
    /// <summary>The single overlay, created on first use.</summary>
    private static DownedOverlay? _instance;

    /// <summary>The countdown, in large digits.</summary>
    private Text _countdown = null!;

    /// <summary>The line under the countdown.</summary>
    private Text _subtitle = null!;

    /// <summary>The root of the overlay elements, active while shown.</summary>
    private GameObject _root = null!;

    /// <summary>The saturation of the camera colour correction before the overlay was shown, restored after.</summary>
    private float? _savedSaturation;

    /// <summary>The last number of seconds displayed, to update the text only when it changes.</summary>
    private int _lastSeconds = -1;

    /// <summary>Whether the overlay is shown.</summary>
    private bool _shown;

    /// <summary>
    /// Gets the overlay, creating it if needed.
    /// </summary>
    public static DownedOverlay Get() {
        if (_instance != null) {
            return _instance;
        }

        var gameObject = new GameObject("SSMP Downed Overlay");
        DontDestroyOnLoad(gameObject);
        _instance = gameObject.AddComponent<DownedOverlay>();
        _instance.Build();
        return _instance;
    }

    /// <summary>
    /// Creates the canvas, the grey veil and the texts.
    /// </summary>
    private void Build() {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 400;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        _root = new GameObject("Root", typeof(RectTransform));
        _root.transform.SetParent(transform, false);
        Stretch(_root.GetComponent<RectTransform>());

        var veil = new GameObject("Veil", typeof(RectTransform), typeof(Image));
        veil.transform.SetParent(_root.transform, false);
        Stretch(veil.GetComponent<RectTransform>());
        var image = veil.GetComponent<Image>();
        image.color = new Color(0.35f, 0.35f, 0.35f, 0.3f);
        image.raycastTarget = false;

        _countdown = CreateText("Countdown", new Vector2(0f, 140f), 110);
        _subtitle = CreateText("Subtitle", new Vector2(0f, 60f), 34);

        _root.SetActive(false);
    }

    /// <summary>
    /// Creates a centred text of the overlay.
    /// </summary>
    private Text CreateText(string name, Vector2 position, int fontSize) {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Outline));
        textObject.transform.SetParent(_root.transform, false);

        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(1600f, fontSize * 1.5f);
        rect.anchoredPosition = position;

        var text = textObject.GetComponent<Text>();
        text.font = Ui.Resources.FontManager.UIFontRegular;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        textObject.GetComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
        return text;
    }

    /// <summary>
    /// Makes the rect fill its parent.
    /// </summary>
    private static void Stretch(RectTransform rect) {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    /// <summary>
    /// Shows the overlay with the given remaining time.
    /// </summary>
    public void Show(float secondsLeft) {
        _shown = true;
        _lastSeconds = -1;
        _subtitle.text = IsFrench()
            ? "Vous êtes à terre : retour près de l'arène dans"
            : "You are down: back next to the arena in";
        _root.SetActive(true);
        SetSecondsLeft(secondsLeft);
    }

    /// <summary>
    /// Updates the remaining time.
    /// </summary>
    public void SetSecondsLeft(float secondsLeft) {
        var seconds = Mathf.Max(0, Mathf.CeilToInt(secondsLeft));
        if (seconds == _lastSeconds) {
            return;
        }

        _lastSeconds = seconds;
        _countdown.text = $"{seconds}";
    }

    /// <summary>
    /// Hides the overlay and restores the colours.
    /// </summary>
    public void Hide() {
        if (!_shown) {
            return;
        }

        _shown = false;
        _root.SetActive(false);

        var curves = GetCurves();
        if (curves != null && _savedSaturation.HasValue) {
            curves.saturation = _savedSaturation.Value;
        }

        _savedSaturation = null;
    }

    /// <summary>
    /// After the game's own updates of the colour correction (scene colour managers), keeps it desaturated.
    /// </summary>
    private void LateUpdate() {
        if (!_shown) {
            return;
        }

        var curves = GetCurves();
        if (curves == null) {
            return;
        }

        if (!_savedSaturation.HasValue) {
            _savedSaturation = curves.saturation;
            Logger.Info($"[Downed] Greying the screen (saturation {curves.saturation} -> 0)");
        }

        curves.saturation = 0f;
    }

    /// <summary>
    /// The colour correction of the main camera, if any.
    /// </summary>
    private static ColorCorrectionCurves? GetCurves() {
        var cameras = GameCameras.instance;
        return cameras != null && cameras.colorCorrectionCurves != null ? cameras.colorCorrectionCurves : null;
    }

    /// <summary>
    /// Whether the game is in French.
    /// </summary>
    private static bool IsFrench() {
        try {
            return Language.CurrentLanguage() == LanguageCode.FR;
        } catch {
            return false;
        }
    }
}
