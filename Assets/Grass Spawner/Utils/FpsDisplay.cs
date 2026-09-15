using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal FPS counter on a UGUI Text. Drop it on any GameObject: if no Text is assigned it creates
/// its own overlay Canvas + Text in the chosen corner. Cheap enough to ship in WebGL/mobile builds.
/// </summary>
[DisallowMultipleComponent]
public class FpsDisplay : MonoBehaviour
{
    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    [Tooltip("Optional. Leave empty to auto-create a Canvas + Text overlay.")]
    public Text target;

    [Header("Auto-created overlay")]
    public Corner corner = Corner.TopLeft;
    [Min(8)] public int fontSize = 24;
    public Vector2 margin = new Vector2(12f, 12f);

    [Header("Behaviour")]
    [Tooltip("Seconds between text updates. Frames are averaged over this window.")]
    [Range(0.05f, 2f)] public float updateInterval = 0.5f;
    public bool showFrameTime = true;
    [Tooltip("Also show blade / visible chunk totals from every GrassSpawner in the scene.")]
    public bool showGrassStats = false;
    [Tooltip("Color thresholds: green at or above Good FPS, yellow at or above Warn FPS, red below.")]
    public int goodFps = 55;
    public int warnFps = 28;

    float accumulated;
    int frames;
    float timer;
    GrassSpawner[] spawners;

    void Awake()
    {
        if (target == null) target = CreateOverlay();
        if (showGrassStats) spawners = FindObjectsByType<GrassSpawner>(FindObjectsSortMode.None);
    }

    void Update()
    {
        accumulated += Time.unscaledDeltaTime;
        frames++;
        timer += Time.unscaledDeltaTime;
        if (timer < updateInterval) return;

        float avgDelta = accumulated / Mathf.Max(frames, 1);
        float fps = avgDelta > 0f ? 1f / avgDelta : 0f;
        accumulated = 0f; frames = 0; timer = 0f;

        if (target == null) return;

        string text = showFrameTime
            ? $"{fps:0} FPS  ({avgDelta * 1000f:0.0} ms)"
            : $"{fps:0} FPS";

        if (showGrassStats)
        {
            if (spawners == null || spawners.Length == 0) spawners = FindObjectsByType<GrassSpawner>(FindObjectsSortMode.None);
            int blades = 0, chunks = 0, visible = 0;
            foreach (var sp in spawners)
            {
                if (sp == null) continue;
                blades += sp.BladeCount; chunks += sp.ChunkCount; visible += sp.VisibleChunkCount;
            }
            text += $"\nGrass: {blades:N0} blades, {visible}/{chunks} chunks";
        }

        target.text = text;
        target.color = fps >= goodFps ? new Color(0.45f, 1f, 0.45f)
                     : fps >= warnFps ? new Color(1f, 0.9f, 0.35f)
                     : new Color(1f, 0.4f, 0.4f);
    }

    Text CreateOverlay()
    {
        var canvasGo = new GameObject("FPS Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000; // stay on top of game UI
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var textGo = new GameObject("FPS Text", typeof(Text), typeof(Outline));
        textGo.transform.SetParent(canvasGo.transform, false);
        var text = textGo.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Bold;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = "-- FPS";

        var outline = textGo.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        var rt = text.rectTransform;
        Vector2 anchor = corner switch
        {
            Corner.TopLeft => new Vector2(0f, 1f),
            Corner.TopRight => new Vector2(1f, 1f),
            Corner.BottomLeft => new Vector2(0f, 0f),
            _ => new Vector2(1f, 0f)
        };
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = new Vector2(anchor.x == 0f ? margin.x : -margin.x, anchor.y == 0f ? margin.y : -margin.y);
        rt.sizeDelta = new Vector2(400f, 60f);
        text.alignment = anchor.x == 0f
            ? (anchor.y == 1f ? TextAnchor.UpperLeft : TextAnchor.LowerLeft)
            : (anchor.y == 1f ? TextAnchor.UpperRight : TextAnchor.LowerRight);

        return text;
    }
}
