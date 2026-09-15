using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small runtime panel (UGUI toggles) to switch grass options while playing, e.g. to compare cost on device:
/// lighting, shadows, perspective, wind, ground blend, trample recovery, a scene section (sun rotation) and a camera
/// section (parallax, drag, zoom). Applies to every GrassSpawner
/// in the scene (or only the ones assigned). Creates its own overlay Canvas if none is given.
/// </summary>
[DisallowMultipleComponent]
public class GrassRuntimeUI : MonoBehaviour
{
    [Tooltip("Spawners to control. Empty = every GrassSpawner found at start.")]
    public List<GrassSpawner> spawners = new List<GrassSpawner>();

    [Header("Scene")]
    [Tooltip("TweenRotation that spins the sun (day cycle). Empty = auto: the TweenRotation on RenderSettings.sun or on the first directional light.")]
    public TweenRotation sunRotation;

    [Header("Layout")]
    [Tooltip("Optional parent (e.g. a panel in your own Canvas). Empty = auto-created overlay canvas, top-right.")]
    public RectTransform parent;
    [Min(10)] public int fontSize = 20;
    public Vector2 margin = new Vector2(16f, 16f);

    Font font;

    void Start()
    {
        if (spawners.Count == 0) spawners.AddRange(FindObjectsByType<GrassSpawner>(FindObjectsSortMode.None));
        if (spawners.Count == 0) { Debug.LogWarning("GrassRuntimeUI: no GrassSpawner in the scene.", this); return; }

        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        RectTransform root = parent != null ? parent : CreateOverlay();
        var first = spawners[0];
        savedWindStrength = first.windStrength > 0f ? first.windStrength : 0.35f;

        AddLabel(root, "GRASS");
        AddToggle(root, "Affected by lighting", first.affectedByLighting, v => Apply(s => s.affectedByLighting = v));
        AddToggle(root, "Cast shadows", first.castShadows, v => Apply(s => s.castShadows = v));
        AddToggle(root, "Receive shadows (URP)", first.receiveShadows, v => Apply(s => s.receiveShadows = v));
        AddToggle(root, "Optimize for perspective", first.optimizeForPerspective, v => Apply(s => s.optimizeForPerspective = v));
        AddToggle(root, "Wind", first.windStrength > 0f, v => Apply(s => s.windStrength = v ? savedWindStrength : 0f));
        AddToggle(root, "Blend to ground", first.blendToGround, v => Apply(s => s.blendToGround = v));
        AddToggle(root, "Trample recovery", first.trampleRecovery, v => Apply(s => s.trampleRecovery = v));

        // Scene section: sun rotation (day-cycle simulation) when a TweenRotation drives the directional light.
        var sunTween = sunRotation != null ? sunRotation : FindSunRotation();
        if (sunTween != null)
        {
            AddLabel(root, "SCENE");
            // Start() of the tween may run after ours: fall back to its playOnStart for the initial state.
            // Off = stop and return the sun to its rotation at scene start, so lighting matches the authored scene.
            AddToggle(root, "Sun rotation", sunTween.IsPlaying || sunTween.playOnStart,
                v => { if (v) sunTween.Play(); else sunTween.Stop(); });
        }

        // Camera section: shown when a CameraParallax exists (on this object, the main camera, or anywhere).
        var parallax = GetComponent<CameraParallax>();
        if (parallax == null && Camera.main != null) parallax = Camera.main.GetComponent<CameraParallax>();
        if (parallax == null) parallax = FindFirstObjectByType<CameraParallax>();
        if (parallax != null)
        {
            AddLabel(root, "CAMERA");
            AddToggle(root, "Mouse parallax", parallax.parallaxEnabled, v => parallax.parallaxEnabled = v);
            AddToggle(root, "Drag to orbit", parallax.dragEnabled, v => parallax.dragEnabled = v);
            AddToggle(root, "Scroll to zoom", parallax.zoomEnabled, v => parallax.zoomEnabled = v);
        }
    }

    float savedWindStrength = 0.35f; // restored when the Wind toggle is switched back on

    static TweenRotation FindSunRotation()
    {
        if (RenderSettings.sun != null && RenderSettings.sun.TryGetComponent(out TweenRotation t)) return t;
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional && l.TryGetComponent(out TweenRotation lt)) return lt;
        return null;
    }

    void Apply(System.Action<GrassSpawner> change)
    {
        foreach (var s in spawners)
        {
            if (s == null) continue;
            change(s);
            s.ApplySettings(); // live: shadows, keywords, culling flags. No rebuild needed.
        }
    }

    RectTransform CreateOverlay()
    {
        var canvasGo = new GameObject("Grass UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 31000;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // UI needs an EventSystem to receive clicks
        if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
            es.transform.SetParent(transform, false);
        }

        var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        panelGo.transform.SetParent(canvasGo.transform, false);
        var img = panelGo.GetComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.45f);
        var layout = panelGo.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.spacing = 6f;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        var fitter = panelGo.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var rt = panelGo.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-margin.x, -margin.y);
        return rt;
    }

    /// <summary>Small dim section header between toggle groups.</summary>
    void AddLabel(RectTransform root, string text)
    {
        var go = new GameObject(text, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(root, false);
        go.GetComponent<LayoutElement>().preferredHeight = fontSize * 1.4f;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = Mathf.RoundToInt(fontSize * 0.8f);
        t.fontStyle = FontStyle.Bold;
        t.color = new Color(1f, 1f, 1f, 0.6f);
        t.alignment = TextAnchor.LowerLeft;
        t.text = text;
        t.raycastTarget = false;
    }

    void AddToggle(RectTransform root, string label, bool initial, System.Action<bool> onChanged)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
        go.transform.SetParent(root, false);
        float h = fontSize * 1.5f;
        go.GetComponent<LayoutElement>().preferredHeight = h;
        go.GetComponent<LayoutElement>().minWidth = fontSize * 14f;

        // background box
        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.5f); bgRt.anchorMax = new Vector2(0f, 0.5f); bgRt.pivot = new Vector2(0f, 0.5f);
        bgRt.sizeDelta = new Vector2(h * 0.7f, h * 0.7f);
        bgRt.anchoredPosition = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.9f);

        // checkmark
        var check = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
        check.transform.SetParent(bg.transform, false);
        var ckRt = check.GetComponent<RectTransform>();
        ckRt.anchorMin = Vector2.zero; ckRt.anchorMax = Vector2.one;
        ckRt.offsetMin = new Vector2(4f, 4f); ckRt.offsetMax = new Vector2(-4f, -4f);
        check.GetComponent<Image>().color = new Color(0.25f, 0.65f, 0.2f, 1f);

        // label
        var text = new GameObject("Label", typeof(RectTransform), typeof(Text));
        text.transform.SetParent(go.transform, false);
        var tRt = text.GetComponent<RectTransform>();
        tRt.anchorMin = new Vector2(0f, 0f); tRt.anchorMax = new Vector2(1f, 1f);
        tRt.offsetMin = new Vector2(h * 0.7f + 10f, 0f); tRt.offsetMax = Vector2.zero;
        var t = text.GetComponent<Text>();
        t.font = font; t.fontSize = fontSize; t.color = Color.white;
        t.alignment = TextAnchor.MiddleLeft; t.text = label;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;

        var toggle = go.GetComponent<Toggle>();
        toggle.targetGraphic = bg.GetComponent<Image>();
        toggle.graphic = check.GetComponent<Image>();
        toggle.isOn = initial;
        toggle.onValueChanged.AddListener(v => onChanged(v));
    }
}
