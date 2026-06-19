using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using SensorFlex.Recorder;

/// <summary>
/// Self-contained AR recording UI. Add to any scene GameObject — no Inspector
/// wiring required. Locates ARSensorFlexRecorder automatically and builds its
/// own Canvas at runtime.
/// </summary>
[AddComponentMenu("XR/SensorFlex/Recorder UI")]
public class RecorderUI : MonoBehaviour
{
    // ── Runtime UI refs ────────────────────────────────────────────────

    Image    _statusDot;
    TMP_Text _statusLabel;
    Button   _recordButton;
    TMP_Text _recordButtonLabel;

    static TMP_FontAsset s_Font;

    // ── Colors ─────────────────────────────────────────────────────────

    static readonly Color s_Idle      = new Color32(140, 140, 140, 255);
    static readonly Color s_Recording = new Color32(230,  51,  51, 255);
    static readonly Color s_Finalizing= new Color32(242, 178,  26, 255);
    static readonly Color s_Done      = new Color32( 51, 204,  89, 255);
    static readonly Color s_BgDark    = new Color32(  0,   0,   0, 180);
    static readonly Color s_BtnIdle   = new Color32( 44,  44,  44, 220);
    static readonly Color s_BtnRecord = new Color32(200,  40,  40, 255);

    // ── State ──────────────────────────────────────────────────────────

    ARSensorFlexRecorder _recorder;
    bool                 _pulsing;
    bool                 _wasFinalizingLastFrame;

    // ── Unity lifecycle ────────────────────────────────────────────────

    void Awake()
    {
        _recorder = FindFirstObjectByType<ARSensorFlexRecorder>();
        if (_recorder == null)
        {
            Debug.LogError("[SF-UI] No ARSensorFlexRecorder found in scene.");
            enabled = false;
            return;
        }

        _recorder.RecordingStartedEvent      += OnStarted;
        _recorder.RecordingFinalizedEvent    += OnFinalized;
        _recorder.RecordingFailedEvent       += OnFailed;
        _recorder.RecordingLimitReachedEvent += OnLimitReached;
    }

    void Start()
    {
        EnsureEventSystem();
        BuildUI();
        SetStatus("Idle", s_Idle, pulse: false);
        RefreshRecordButton();
    }

    void Update()
    {
        if (_recorder.IsFinalizing && !_wasFinalizingLastFrame)
        {
            SetStatus("Finalizing…", s_Finalizing, pulse: false);
            _recordButton.interactable = false;
            _recordButtonLabel.text    = "Saving…";
        }
        _wasFinalizingLastFrame = _recorder.IsFinalizing;
    }

    void OnDestroy()
    {
        if (_recorder == null) return;
        _recorder.RecordingStartedEvent      -= OnStarted;
        _recorder.RecordingFinalizedEvent    -= OnFinalized;
        _recorder.RecordingFailedEvent       -= OnFailed;
        _recorder.RecordingLimitReachedEvent -= OnLimitReached;
    }

    // ── Recorder events ────────────────────────────────────────────────

    void OnStarted(string _)
    {
        SetStatus("Recording", s_Recording, pulse: true);
        RefreshRecordButton();
    }

    void OnFinalized(string[] paths)
    {
        string msg = paths.Length == 1
            ? $"Saved  {Path.GetFileName(paths[0])}"
            : $"Saved {paths.Length} parts";
        SetStatus(msg, s_Done, pulse: false);
        RefreshRecordButton();
    }

    void OnFailed(string err)
    {
        SetStatus($"Error: {err}", s_Recording, pulse: false);
        RefreshRecordButton();
    }

    void OnLimitReached()
    {
        SetStatus("Limit reached — saving…", s_Finalizing, pulse: false);
        RefreshRecordButton();
    }

    void OnRecordPressed()
    {
        if (_recorder.IsRecording)        _recorder.StopRecording();
        else if (!_recorder.IsFinalizing) _recorder.StartRecording();
    }

    void RefreshRecordButton()
    {
        bool recording = _recorder.IsRecording;
        _recordButton.interactable = !_recorder.IsFinalizing;
        _recordButtonLabel.text    = recording ? "Stop" : "Record";

        var colors = _recordButton.colors;
        colors.normalColor = recording ? s_BtnRecord : s_BtnIdle;
        _recordButton.colors = colors;
    }

    // ── Status dot ─────────────────────────────────────────────────────

    void SetStatus(string text, Color dotColor, bool pulse)
    {
        _statusLabel.text = text;
        StopCoroutine(nameof(PulseDot));
        _pulsing         = pulse;
        _statusDot.color = dotColor;
        if (pulse) StartCoroutine(nameof(PulseDot));
    }

    IEnumerator PulseDot()
    {
        Color on  = _statusDot.color;
        Color off = new Color(on.r, on.g, on.b, 0.2f);
        while (_pulsing)
        {
            float t = 0f;
            while (t < 1f) { t += Time.deltaTime * 2f; _statusDot.color = Color.Lerp(on, off, t); yield return null; }
            while (t > 0f) { t -= Time.deltaTime * 2f; _statusDot.color = Color.Lerp(on, off, t); yield return null; }
        }
        var c = _statusDot.color; c.a = 1f; _statusDot.color = c;
    }

    // ── UI construction ────────────────────────────────────────────────

    void BuildUI()
    {
        var root = MakeCanvas().transform;
        BuildStatusBar(root);
        BuildRecordButton(root);
    }

    Canvas MakeCanvas()
    {
        var go     = new GameObject("RecorderCanvas");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    void BuildStatusBar(Transform root)
    {
        var bar = UIPanel(root, "StatusBar", s_BgDark);
        var rt  = bar.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.sizeDelta        = new Vector2(400, 58);
        rt.anchoredPosition = new Vector2(0, -52);

        var hlg = bar.AddComponent<HorizontalLayoutGroup>();
        hlg.childAlignment       = TextAnchor.MiddleCenter;
        hlg.padding              = new RectOffset(16, 16, 0, 0);
        hlg.spacing              = 10f;
        hlg.childControlWidth    = true;
        hlg.childControlHeight   = true;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;

        // Dot
        var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        dotGo.transform.SetParent(bar.transform, false);
        var le = dotGo.AddComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = 16f;
        le.flexibleWidth = 0f;
        _statusDot = dotGo.GetComponent<Image>();

        // Label
        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(bar.transform, false);
        _statusLabel           = labelGo.GetComponent<TextMeshProUGUI>();
        _statusLabel.fontSize  = 26f;
        _statusLabel.color     = Color.white;
        _statusLabel.alignment = TextAlignmentOptions.Midline;
    }

    void BuildRecordButton(Transform root)
    {
        UIButton(root, "RecordButton", "Record", 36f, s_BtnIdle,
            out _recordButton, out _recordButtonLabel);
        var rt = _recordButton.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot     = new Vector2(0.5f, 0f);
        rt.sizeDelta        = new Vector2(260, 88);
        rt.anchoredPosition = new Vector2(0, 80);
        _recordButton.onClick.AddListener(OnRecordPressed);
    }

    // ── Low-level UI helpers ───────────────────────────────────────────

    static GameObject UIPanel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    static TMP_Text UIText(Transform parent, string text, float size,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (s_Font == null)
            s_Font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (s_Font != null)
            t.font = s_Font;
        t.text      = text;
        t.fontSize  = size;
        t.color     = Color.white;
        t.alignment = TextAlignmentOptions.Midline;
        return t;
    }

    static void UIButton(Transform parent, string name, string label, float fontSize,
        Color bgColor, out Button button, out TMP_Text buttonLabel)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = bgColor;

        button = go.GetComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.35f, 0.35f, 0.35f, 0.9f);
        colors.pressedColor     = new Color(0.15f, 0.15f, 0.15f, 0.9f);
        button.colors = colors;

        buttonLabel = UIText(go.transform, label, fontSize,
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
    }

    // ── EventSystem bootstrap ──────────────────────────────────────────

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }
}
