using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Lightweight grass spawner for WebGL / mobile.
///
/// Scatters textured blade cards inside an area (rectangle or closed spline, in this object's local
/// XZ plane) and bakes them into a handful of static chunk meshes: one draw call per chunk, zero
/// per-frame CPU work, no GPU instancing or compute shaders required. Wind and interaction are
/// handled in the vertex shader (see Grass/Blade).
///
/// Input: any PNG with alpha (white silhouette works best; the shader tints it root -> tip).
/// The area can be edited with handles in the Scene view (see GrassSpawnerEditor).
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class GrassSpawner : MonoBehaviour
{
    public enum AreaShape { Rectangle, Spline }
    public enum ColorMode { Gradient, Solid }
    public enum GradientMode { BladeHeight, CardUV }
    public enum MaskChannel { R, G, B, A }
    public enum QualityPreset { MobileWebGL, PC }
    public enum GroundBlendMode { AlphaFade, Color }

    const string ChunkName = "GrassChunk";
    const string ShaderName = "Grass/Blade";
    const int SplineSubdivisions = 8; // curve samples per control point

    [Header("Input")]
    [Tooltip("PNG used for every blade. Alpha is the blade shape; RGB is multiplied into the color.")]
    public Texture2D bladeTexture;
    [Tooltip("Optional. Sampled across the area bounds (local XZ -> UV) and multiplied into the color, e.g. dry patches or paths.")]
    public Texture2D tintMap;
    [Tooltip("How many times the tint map repeats across the area. Set the texture's Wrap Mode to Repeat for values above 1.")]
    public Vector2 tintMapTiling = Vector2.one;
    [Tooltip("UV offset of the tint map (0-1 = one full texture).")]
    public Vector2 tintMapOffset = Vector2.zero;
    [Tooltip("Multiplied into the tint map. White = the texture as is; use it to recolor a grayscale mask (e.g. yellow for dry patches) without editing the texture.")]
    public Color tintMapColor = Color.white;
    [Tooltip("0 = ignore the tint map, 1 = full effect.")]
    [Range(0f, 1f)] public float tintMapStrength = 1f;
    [Tooltip("Softens the tint map by sampling a higher mip level. 0 = sharp, each step halves the resolution. Requires mipmaps enabled on the texture.")]
    [Range(0f, 8f)] public float tintMapBlur = 2f;
    [Tooltip("Optional. Only the shader is taken from it; every setting is controlled by this component. Leave empty to use Grass/Blade.")]
    public Material baseMaterial;

    [Header("Area (local XZ, edit with handles in the Scene view)")]
    public AreaShape areaShape = AreaShape.Rectangle;
    [Tooltip("Rectangle size, centered on this transform.")]
    [Min(0.1f)] public Vector2 areaSize = new Vector2(20f, 20f);
    [Tooltip("Control points of the closed spline (local XZ). Shift+click the curve to add, Ctrl/Cmd+click a point to remove.")]
    public List<Vector2> splinePoints = new List<Vector2>
    {
        new Vector2(-8f, -8f), new Vector2(8f, -8f), new Vector2(8f, 8f), new Vector2(-8f, 8f)
    };
    [Tooltip("0 = straight polygon through the points, 1 = fully smooth curve.")]
    [Range(0f, 1f)] public float splineSmoothness = 1f;
    [Tooltip("Width in meters over which grass thins out and gets shorter toward the boundary. 0 = hard edge.")]
    [Min(0f)] public float edgeFalloff = 1.5f;
    [Tooltip("Density right at the boundary as a fraction of the interior density. 0 = fades to nothing, 1 = same density everywhere (only height changes).")]
    [Range(0f, 1f)] public float edgeDensity = 0f;
    [Tooltip("Blade height right at the boundary as a fraction of normal height.")]
    [Range(0.1f, 1f)] public float edgeHeight = 0.55f;

    [Header("Color")]
    [Tooltip("Grass color is defined here, not on the material. Gradient runs from root (left) to tip (right).")]
    public ColorMode colorMode = ColorMode.Gradient;
    public Color solidColor = new Color(0.45f, 0.75f, 0.2f, 1f);
    [Tooltip("Multiply the PNG's RGB into the color. Off (default) uses only the alpha shape, so pre-shaded sprites don't bake their shadows into every blade.")]
    public bool useTextureColor = false;
    [Tooltip("Color along the blade: left = root, right = tip.")]
    public Gradient gradient = DefaultGradient();
    [Tooltip("BladeHeight: ramp follows real height in meters (0 = ground, 1 = tallest blade), so short blades stay in root colors. CardUV: every card runs root->tip over its own length.")]
    public GradientMode gradientMode = GradientMode.BladeHeight;

    [Header("Ground Blend")]
    [Tooltip("Blend the base of each blade into the ground so roots don't cut a hard line on the terrain.")]
    public bool blendToGround = false;
    [Tooltip("AlphaFade: the base becomes transparent (alpha 0 at the root) with a cheap screen dither, the ground shows through. Color: the base is tinted toward the color of the surface under each blade, sampled at Rebuild and baked into the mesh.")]
    public GroundBlendMode groundBlendMode = GroundBlendMode.AlphaFade;
    [Tooltip("How far up the blade the blend reaches, as a fraction of the color ramp (0..1).")]
    [Range(0.05f, 1f)] public float groundBlendHeight = 0.35f;
    [Range(0f, 1f)] public float groundBlendStrength = 1f;

    [Header("Shading")]
    [Tooltip("Random brightness difference between blades. 0 = perfectly uniform.")]
    [Range(0f, 1f)] public float colorVariation = 0.12f;
    [Tooltip("How much the scene lighting affects the grass: 0 = flat unlit colors, 1 = fully follows main light color/intensity and ambient.")]
    [Range(0f, 1f)] public float sunInfluence = 0.7f;
    [Tooltip("Bright bands that travel with the wind wave.")]
    [Range(0f, 1f)] public float windHighlight = 0.08f;
    [Tooltip("How dark trampled blades get.")]
    [Range(0f, 1f)] public float trampleDarken = 0.5f;

    [Header("View")]
    [Tooltip("Optimize the look for a perspective camera: all cards align to the camera's view direction as one plane (no per-blade swivel) so none are seen edge-on, and tips lean away from a camera looking down. Cross Quads is ignored while this is on (one card per blade is enough).")]
    public bool optimizeForPerspective = true;

    [Header("Wind")]
    [Tooltip("Direction on the XZ plane.")]
    public Vector2 windDirection = new Vector2(1f, 0.35f);
    [Range(0f, 2f)] public float windStrength = 0.35f;
    [Range(0f, 10f)] public float windSpeed = 2f;
    [Tooltip("Spatial frequency of the wave: higher = shorter ripples.")]
    [Range(0f, 4f)] public float windFrequency = 0.6f;

    [Header("Interaction")]
    [Tooltip("How far GrassInteractors push blades aside.")]
    [Range(0f, 3f)] public float pushStrength = 1f;
    [Tooltip("Grass stays flattened after an interactor passes and springs back over Recovery Time. Uses a small render texture per spawner (one cheap blit per frame).")]
    public bool trampleRecovery = true;
    [Tooltip("Base seconds for flattened grass to fully stand up again. Each GrassInteractor multiplies this with its Recovery Scale.")]
    [Min(0.05f)] public float recoveryTime = 2f;
    [Tooltip("Resolution of the trample map. 256 is plenty for a 20-40 m field.")]
    public int trampleMapResolution = 256;

    [Header("Density")]
    [Tooltip("Blades per square meter. 20k-60k total blades is a comfortable WebGL/mobile budget.")]
    [Min(0.01f)] public float density = 60f;
    [Tooltip("Hard cap on total blades regardless of density.")]
    [Min(1)] public int maxBlades = 60000;
    public int seed = 1234;

    [Header("Density Mask")]
    [Tooltip("Optional. Sampled across the area bounds; the chosen channel scales the local density (white = full, black = none). The texture must have Read/Write enabled.")]
    public Texture2D densityMask;
    public MaskChannel densityMaskChannel = MaskChannel.R;
    public bool densityMaskInvert = false;
    [Tooltip("When the ground hit is a Unity Terrain, use the weight of one terrain layer (paint layer) as density.")]
    public bool useTerrainLayer = false;
    [Tooltip("Index of the terrain layer in the Terrain's paint layers list.")]
    [Min(0)] public int terrainLayerIndex = 0;
    [Tooltip("0 = ignore the layer weight, 1 = density follows the layer weight exactly.")]
    [Range(0f, 1f)] public float terrainLayerInfluence = 1f;

    [Header("Lighting")]
    [Tooltip("Off = fully unlit: the grass ignores light color, intensity, ambient and received shadows and shows its colors as set. Casting shadows is unaffected.")]
    public bool affectedByLighting = false;
    [Tooltip("Blades cast real-time shadows (ShadowCaster pass). Costs an extra pass per shadow cascade: fine on PC, usually off on mobile.")]
    public bool castShadows = false;
    [Tooltip("URP only: darken blades inside main-light shadows with one shadow tap per vertex. Not supported with the Screen Space Shadows renderer feature.")]
    public bool receiveShadows = false;
    [Range(0f, 1f)] public float shadowStrength = 0.6f;

    [Header("Blade Shape")]
    public Vector2 heightRange = new Vector2(0.4f, 0.9f);
    public Vector2 widthRange = new Vector2(0.35f, 0.6f);
    [Tooltip("Two quads crossed at 90 degrees per blade. Better from all angles, doubles vertex count.")]
    public bool crossQuads = true;
    [Tooltip("Vertical subdivisions per card. 1 is cheapest; 2-3 bends more smoothly in strong wind.")]
    [Range(1, 4)] public int segments = 1;

    [Header("Ground")]
    [Tooltip("Raycast down onto these layers so blades sit on terrain/meshes. Falls back to this object's plane.")]
    public bool snapToGround = true;
    public LayerMask groundLayers = ~0;
    [Min(1f)] public float raycastHeight = 50f;
    [Tooltip("Skip blades whose ground normal is steeper than this (degrees).")]
    [Range(0f, 90f)] public float maxSlope = 60f;

    [Header("Chunking")]
    [Tooltip("Chunk edge length in meters. Smaller chunks = better frustum culling, more draw calls.")]
    [Min(1f)] public float chunkSize = 10f;

    [Header("Camera Culling")]
    [Tooltip("Per frame: hide chunks outside the camera frustum or beyond Max Distance, and drop the detail half of far chunks.")]
    public bool cameraCulling = true;
    [Tooltip("Camera used for culling. Empty = Camera.main.")]
    public Camera cullingCamera;
    [Tooltip("Chunks farther than this from the camera are not rendered. Blades shrink into the ground over Fade Range before it.")]
    [Min(1f)] public float maxDistance = 80f;
    [Min(0f)] public float fadeRange = 15f;
    [Tooltip("Beyond this distance only half of the blades in a chunk are drawn (density LOD). Set >= Max Distance to disable.")]
    [Min(0f)] public float detailDistance = 35f;

    [Header("Runtime")]
    [Tooltip("On: grass is rebuilt at runtime in Start (editor preview is temporary). Off: what you build in the editor is baked into the scene (chunks saved, meshes stored in an asset) and used as-is at runtime and in builds, no rebuild and no startup cost.")]
    public bool buildOnStart = true;
    [SerializeField, HideInInspector] string bakedAssetPath;
    // Remembers that a temporary editor preview existed, so it is rebuilt after Play mode / scene reload.
    [SerializeField, HideInInspector] bool hasEditorPreview;

    /// <summary>True when the current chunks are saved scene objects (built in the editor with Build On Start off).</summary>
    public bool HasBakedChunks
    {
        get
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var c = transform.GetChild(i).gameObject;
                if (c.name.StartsWith(ChunkName) && (c.hideFlags & HideFlags.DontSave) == 0) return true;
            }
            return false;
        }
    }

    sealed class ChunkInfo
    {
        public Renderer baseRenderer;   // ~50% of the blades, always drawn when visible
        public Renderer detailRenderer; // the other ~50%, dropped beyond detailDistance
    }

    readonly List<ChunkInfo> chunks = new List<ChunkInfo>();
    readonly Plane[] frustum = new Plane[6];
    Material runtimeMaterial;
    Texture2D colorRamp;

    // trample map (trail + recovery)
    RenderTexture trampleA, trampleB;
    Material trampleBlit;
    Rect trampleBounds;          // local XZ bounds covered by the map
    float lastStampTime = -1f;   // last time an interactor was stamped into the map
    float pendingDecay;          // decay accumulated while the blit was skipped (e.g. spawner off-screen)
    float rootMinY, rootMaxY;    // root height range, to ignore interactors far above/below the grass
    static readonly Vector4[] trampleUpload = new Vector4[GrassInteractor.MaxInteractors];
    static readonly Vector4[] trampleRecoveryUpload = new Vector4[GrassInteractor.MaxInteractors];
    float longestRecovery;       // longest recovery stamped so far, for the idle check
    // Shared across all spawners: globals are uploaded once per frame no matter how many instances exist.
    static Light sun;
    static bool searchedSun;
    static int lastUploadFrame = -1;

    public int BladeCount { get; private set; }
    public int VertexCount { get; private set; }
    public int ChunkCount => chunks.Count;
    /// <summary>Cards baked per blade: cross quads are pointless when cards are yawed toward the camera.</summary>
    public int QuadsPerBlade => crossQuads && !optimizeForPerspective ? 2 : 1;
    public int VisibleChunkCount { get; private set; }

    /// <summary>Approximate GPU memory of the baked meshes in bytes (36 B per vertex + 16-bit or 32-bit indices).</summary>
    public long EstimatedMeshBytes(int vertexCount)
    {
        long indices = (long)vertexCount * 3 / 2; // 6 indices per 4-vertex quad
        return (long)vertexCount * 36 + indices * (vertexCount > 65535 ? 4 : 2);
    }

#if UNITY_EDITOR
    void Reset()
    {
        // Called when the component is added: pick up the default material so tweaks persist across rebuilds.
        if (baseMaterial == null) baseMaterial = FindPackageAsset<Material>("GrassBlade");
    }

    /// <summary>Root folder of this package (the folder containing Scripts/), found from the script itself so
    /// the package can be moved or renamed freely.</summary>
    public static string PackageFolder
    {
        get
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("GrassSpawner t:MonoScript");
            foreach (var g in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                if (!path.EndsWith("/GrassSpawner.cs")) continue;
                string dir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
                return dir.EndsWith("/Scripts") ? dir.Substring(0, dir.Length - "/Scripts".Length) : dir;
            }
            return "Assets";
        }
    }

    /// <summary>Finds an asset by name inside this package folder (any subfolder).</summary>
    public static T FindPackageAsset<T>(string name) where T : Object
    {
        string root = PackageFolder;
        foreach (var g in UnityEditor.AssetDatabase.FindAssets($"{name} t:{typeof(T).Name}", new[] { root }))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == name)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
        }
        return null;
    }
#endif

    void OnValidate()
    {
        if (runtimeMaterial != null) ApplySettings();
    }

    void OnEnable()
    {
        if (Application.isPlaying) return;
        // Editor: after a domain reload or scene load, re-attach the (non-serialized) material to baked chunks.
        AdoptExistingChunks();
#if UNITY_EDITOR
        // Preview chunks are DontSave objects: they vanish when Play mode starts or the scene reloads.
        // Rebuild them next editor tick (colliders are not reliable during OnEnable of a scene load).
        if (hasEditorPreview && chunks.Count == 0 && !HasBakedChunks)
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null && !Application.isPlaying && hasEditorPreview && chunks.Count == 0 && !HasBakedChunks)
                    Rebuild();
            };
        }
#endif
    }

    void Start()
    {
        if (!Application.isPlaying) return;
        if (buildOnStart) Rebuild();
        else AdoptExistingChunks(); // keep whatever was baked in the editor
    }

    void OnDisable()
    {
        // Temporary preview chunks go away; baked chunks stay in the scene and are re-adopted on enable.
        DestroyChunkObjects(previewOnly: true);
        ReleaseRuntimeResources();
    }

    void LateUpdate()
    {
        UploadGlobals();
        UpdateCulling();
        UpdateTrampleMap();
    }

    // -------------------------------------------------------------------- area

    List<Vector2> cachedOutline;
    int cachedOutlineHash;

    /// <summary>Closed outline of the spawn area in local XZ. Cached; recomputed only when the area settings change.</summary>
    public List<Vector2> GetOutline()
    {
        int hash = ComputeAreaHash();
        if (cachedOutline != null && hash == cachedOutlineHash) return cachedOutline;
        cachedOutline = BuildOutline();
        cachedOutlineHash = hash;
        return cachedOutline;
    }

    int ComputeAreaHash()
    {
        unchecked
        {
            int h = (int)areaShape * 397 ^ areaSize.GetHashCode();
            h = h * 31 + splineSmoothness.GetHashCode();
            h = h * 31 + splinePoints.Count;
            for (int i = 0; i < splinePoints.Count; i++) h = h * 31 + splinePoints[i].GetHashCode();
            return h;
        }
    }

    List<Vector2> BuildOutline()
    {
        var pts = new List<Vector2>();
        if (areaShape == AreaShape.Rectangle)
        {
            float hx = areaSize.x * 0.5f, hz = areaSize.y * 0.5f;
            pts.Add(new Vector2(-hx, -hz)); pts.Add(new Vector2(hx, -hz));
            pts.Add(new Vector2(hx, hz));   pts.Add(new Vector2(-hx, hz));
            return pts;
        }

        int n = splinePoints.Count;
        if (n < 3) { pts.AddRange(splinePoints); return pts; }

        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = splinePoints[(i - 1 + n) % n];
            Vector2 p1 = splinePoints[i];
            Vector2 p2 = splinePoints[(i + 1) % n];
            Vector2 p3 = splinePoints[(i + 2) % n];
            for (int s = 0; s < SplineSubdivisions; s++)
            {
                float t = (float)s / SplineSubdivisions;
                Vector2 curve = CatmullRom(p0, p1, p2, p3, t);
                Vector2 straight = Vector2.Lerp(p1, p2, t);
                pts.Add(Vector2.Lerp(straight, curve, splineSmoothness));
            }
        }
        return pts;
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t
                     + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                     + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    public static Rect GetBounds(List<Vector2> outline)
    {
        if (outline.Count == 0) return new Rect(0, 0, 1, 1);
        Vector2 min = outline[0], max = outline[0];
        foreach (var p in outline) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    public static float GetArea(List<Vector2> outline)
    {
        double a = 0;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
            a += (double)outline[j].x * outline[i].y - (double)outline[i].x * outline[j].y;
        return Mathf.Abs((float)(a * 0.5));
    }

    /// <summary>Shortest distance from a point to the closed outline (polygon edges).</summary>
    static float DistanceToOutline(List<Vector2> outline, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            Vector2 a = outline[j], b = outline[i];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float u = len2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            float d = (a + ab * u - p).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    static bool Contains(List<Vector2> outline, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            Vector2 a = outline[i], b = outline[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Estimated blade count for the current settings (used by the inspector).</summary>
    public int EstimateBladeCount()
    {
        return Mathf.Min(maxBlades, Mathf.CeilToInt(GetArea(GetOutline()) * density));
    }

    // ------------------------------------------------------------------- build

    [ContextMenu("Rebuild Grass")]
    public void Rebuild()
    {
        Clear();

        if (bladeTexture == null)
        {
            Debug.LogWarning($"[{name}] GrassSpawner: assign a blade texture PNG first.", this);
            return;
        }

        var outline = GetOutline();
        if (outline.Count < 3)
        {
            Debug.LogWarning($"[{name}] GrassSpawner: the spline needs at least 3 points.", this);
            return;
        }
        Rect bounds = GetBounds(outline);
        bool isRect = areaShape == AreaShape.Rectangle;

        runtimeMaterial = CreateMaterial(bounds);

        var rng = new System.Random(seed);
        int total = Mathf.Min(maxBlades, Mathf.CeilToInt(GetArea(outline) * density));
        int cx = Mathf.Max(1, Mathf.CeilToInt(bounds.width / chunkSize));
        int cz = Mathf.Max(1, Mathf.CeilToInt(bounds.height / chunkSize));

        var builders = new ChunkBuilder[cx * cz];
        var detailBuilders = new ChunkBuilder[cx * cz];
        // Expected blades per builder (half the blades go to each of base/detail), with headroom to avoid List regrowth.
        int perBuilder = Mathf.CeilToInt(total / (float)(cx * cz) * 0.5f * 1.3f) + 16;
        int vertsPerBlade = QuadsPerBlade * (segments + 1) * 2;
        for (int i = 0; i < builders.Length; i++)
        {
            builders[i] = new ChunkBuilder(perBuilder * vertsPerBlade);
            detailBuilders[i] = new ChunkBuilder(perBuilder * vertsPerBlade);
        }

        float slopeCos = Mathf.Cos(maxSlope * Mathf.Deg2Rad);
        int placed = 0;
        trampleBounds = bounds;
        terrainAlphaCache.Clear();
        materialColorCache.Clear();
        textureAverageCache.Clear();
        bool bakeGround = BakesGroundColor;
        bool useMask = densityMask != null;
        if (useMask && !densityMask.isReadable)
        {
            Debug.LogWarning($"[{name}] GrassSpawner: density mask '{densityMask.name}' needs Read/Write enabled in its import settings; ignoring it.", this);
            useMask = false;
        }
        rootMinY = float.MaxValue; rootMaxY = float.MinValue;
        int attempts = 0;
        int maxAttempts = total * 20; // rejection sampling guard for very thin shapes

        while (placed < total && attempts < maxAttempts)
        {
            attempts++;
            float x = Mathf.Lerp(bounds.xMin, bounds.xMax, (float)rng.NextDouble());
            float z = Mathf.Lerp(bounds.yMin, bounds.yMax, (float)rng.NextDouble());
            if (!isRect && !Contains(outline, new Vector2(x, z)))
                continue;

            // Soft boundary: blades near the outline are dropped with increasing probability and get shorter.
            float edgeK = 1f;
            if (edgeFalloff > 0f)
            {
                float edgeDist = isRect
                    ? Mathf.Min(bounds.xMax - x, x - bounds.xMin, bounds.yMax - z, z - bounds.yMin)
                    : DistanceToOutline(outline, new Vector2(x, z));
                float t = Mathf.Clamp01(edgeDist / edgeFalloff);
                edgeK = t * t * (3f - 2f * t); // smoothstep, 0 at the boundary -> 1 inside
                float keep = Mathf.Lerp(edgeDensity, 1f, edgeK);
                if ((float)rng.NextDouble() > keep) continue;
            }

            Vector3 root = new Vector3(x, 0f, z);
            Collider hitCollider = null;
            RaycastHit groundHit = default;
            if (snapToGround && !SnapToGround(ref root, slopeCos, out hitCollider, out groundHit))
                continue;
            Vector3 hitPoint = hitCollider != null ? groundHit.point : transform.TransformPoint(root);

            // Density masks: texture across the area, and/or the weight of a terrain paint layer under the blade.
            float densityW = 1f;
            if (useMask) densityW *= SampleDensityMask(x, z, bounds);
            if (useTerrainLayer && hitCollider is TerrainCollider tc)
                densityW *= Mathf.Lerp(1f, SampleTerrainLayer(tc, hitPoint), terrainLayerInfluence);
            if (densityW < 1f && (float)rng.NextDouble() > densityW) continue;

            rootMinY = Mathf.Min(rootMinY, root.y); rootMaxY = Mathf.Max(rootMaxY, root.y);

            float rand = (float)rng.NextDouble();
            float height = Mathf.Lerp(heightRange.x, heightRange.y, (float)rng.NextDouble()) * Mathf.Lerp(edgeHeight, 1f, edgeK);
            float width = Mathf.Lerp(widthRange.x, widthRange.y, (float)rng.NextDouble());
            float yaw = (float)rng.NextDouble() * Mathf.PI * 2f;

            int ix = Mathf.Clamp((int)((x - bounds.xMin) / chunkSize), 0, cx - 1);
            int iz = Mathf.Clamp((int)((z - bounds.yMin) / chunkSize), 0, cz - 1);
            // alternate blades between the base and detail sets so far chunks can drop half their density
            var cb = (placed & 1) == 0 ? builders[iz * cx + ix] : detailBuilders[iz * cx + ix];

            Color32 groundCol = bakeGround ? (Color32)SampleGroundColor(hitCollider, groundHit) : new Color32(255, 255, 255, 255);

            int quads = QuadsPerBlade;
            for (int q = 0; q < quads; q++)
            {
                float a = yaw + q * Mathf.PI * 0.5f;
                Vector3 right = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (width * 0.5f);
                cb.AddCard(root, right, height, segments, rand, groundCol, bakeGround);
            }
            placed++;
        }

        BladeCount = placed;
        VertexCount = 0;
        float bloat = heightRange.y * 1.5f; // room for wind / push so tips aren't frustum-culled
        bool bake = !Application.isPlaying && !buildOnStart; // editor build meant to persist in the scene
        if (!Application.isPlaying) hasEditorPreview = !bake;   // preview: restore it after Play / reload
        for (int i = 0; i < builders.Length; i++)
        {
            if (builders[i].VertexCount == 0 && detailBuilders[i].VertexCount == 0) continue;
            VertexCount += builders[i].VertexCount + detailBuilders[i].VertexCount;
            var info = new ChunkInfo
            {
                baseRenderer = builders[i].VertexCount > 0 ? CreateChunk(i, builders[i], bloat, false, bake) : null,
                detailRenderer = detailBuilders[i].VertexCount > 0 ? CreateChunk(i, detailBuilders[i], bloat, true, bake) : null
            };
            chunks.Add(info);
        }
        VisibleChunkCount = chunks.Count;
        if (placed == 0) { rootMinY = rootMaxY = 0f; }
        if (bake) SaveBakedMeshes();
        ApplyRendererSettings();
        UpdateCulling();
        SetupTrampleMap();
    }

    /// <summary>Picks up chunk children already in the scene (baked in the editor) and gives them a material.</summary>
    public void AdoptExistingChunks()
    {
        if (chunks.Count > 0 || bladeTexture == null) return;

        var byIndex = new Dictionary<int, ChunkInfo>();
        int totalVerts = 0;
        Bounds world = default; bool hasBounds = false;
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i).gameObject;
            if (!child.name.StartsWith(ChunkName)) continue;
            var mr = child.GetComponent<MeshRenderer>();
            var mf = child.GetComponent<MeshFilter>();
            if (mr == null || mf == null || mf.sharedMesh == null) continue;

            bool detail = child.name.StartsWith(ChunkName + "Detail");
            int us = child.name.LastIndexOf('_');
            int index = us >= 0 && int.TryParse(child.name.Substring(us + 1), out int parsed) ? parsed : i;
            if (!byIndex.TryGetValue(index, out var info)) byIndex[index] = info = new ChunkInfo();
            if (detail) info.detailRenderer = mr; else info.baseRenderer = mr;

            totalVerts += mf.sharedMesh.vertexCount;
            var b = mf.sharedMesh.bounds; // local (spawner) space, padded by bloat
            if (!hasBounds) { world = b; hasBounds = true; } else world.Encapsulate(b);
        }
        if (byIndex.Count == 0) return;

        foreach (var kv in byIndex) chunks.Add(kv.Value);
        VertexCount = totalVerts;
        BladeCount = totalVerts / Mathf.Max(QuadsPerBlade * (segments + 1) * 2, 1);

        var outline = GetOutline();
        Rect bounds = outline.Count >= 3 ? GetBounds(outline) : Rect.MinMaxRect(world.min.x, world.min.z, world.max.x, world.max.z);
        trampleBounds = bounds;
        float bloat = heightRange.y * 1.5f;
        rootMinY = world.min.y + bloat * 0.5f; rootMaxY = world.max.y - bloat * 0.5f - heightRange.y;
        if (rootMaxY < rootMinY) rootMaxY = rootMinY;

        runtimeMaterial = CreateMaterial(bounds);
        foreach (var c in chunks)
        {
            if (c.baseRenderer != null) c.baseRenderer.sharedMaterial = runtimeMaterial;
            if (c.detailRenderer != null) c.detailRenderer.sharedMaterial = runtimeMaterial;
        }
        VisibleChunkCount = chunks.Count;
        ApplyRendererSettings();
        UpdateCulling();
        SetupTrampleMap();
    }

    /// <summary>Removes all grass: preview and baked chunks, the baked mesh asset, and runtime resources.</summary>
    public void Clear()
    {
        DestroyChunkObjects(previewOnly: false);
        DeleteBakedAsset();
        ReleaseRuntimeResources();
        if (!Application.isPlaying) hasEditorPreview = false;
        BladeCount = 0;
        VertexCount = 0;
    }

    void DestroyChunkObjects(bool previewOnly)
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (!child.name.StartsWith(ChunkName)) continue;
            bool isPreview = (child.hideFlags & HideFlags.DontSave) != 0;
            if (previewOnly && !isPreview) continue;
            var mf = child.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && !IsPersistentAsset(mf.sharedMesh)) DestroySafe(mf.sharedMesh);
            DestroySafe(child);
        }
        chunks.Clear();
        VisibleChunkCount = 0;
    }

    void ReleaseRuntimeResources()
    {
        if (runtimeMaterial != null) DestroySafe(runtimeMaterial);
        runtimeMaterial = null;
        if (colorRamp != null) DestroySafe(colorRamp);
        colorRamp = null;
        ReleaseTrampleMap();
    }

    static bool IsPersistentAsset(Object o)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.Contains(o);
#else
        return false;
#endif
    }

    // ------------------------------------------------------------- baking

#if UNITY_EDITOR
    static string BakedFolder => PackageFolder + "/Baked";
#endif

    void SaveBakedMeshes()
    {
#if UNITY_EDITOR
        DeleteBakedAsset();
        if (chunks.Count == 0) return;

        if (!UnityEditor.AssetDatabase.IsValidFolder(BakedFolder))
        {
            System.IO.Directory.CreateDirectory(BakedFolder);
            UnityEditor.AssetDatabase.Refresh();
        }
        string sceneName = string.IsNullOrEmpty(gameObject.scene.name) ? "Scene" : gameObject.scene.name;
        string safeName = string.Concat((sceneName + "_" + name).Split(System.IO.Path.GetInvalidFileNameChars()));
        bakedAssetPath = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{BakedFolder}/{safeName}_Grass.asset");

        // one container asset with every chunk mesh as a sub-asset
        var container = new GrassBakedMeshes();
        UnityEditor.AssetDatabase.CreateAsset(container, bakedAssetPath);
        foreach (var c in chunks)
        {
            AddMeshToAsset(c.baseRenderer);
            AddMeshToAsset(c.detailRenderer);
        }
        UnityEditor.AssetDatabase.SaveAssets();
        UnityEditor.EditorUtility.SetDirty(this);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

#if UNITY_EDITOR
    void AddMeshToAsset(Renderer r)
    {
        if (r == null) return;
        var mesh = r.GetComponent<MeshFilter>().sharedMesh;
        if (mesh == null) return;
        mesh.hideFlags = HideFlags.None;
        UnityEditor.AssetDatabase.AddObjectToAsset(mesh, bakedAssetPath);
    }
#endif

    void DeleteBakedAsset()
    {
#if UNITY_EDITOR
        // Never touch project assets from play mode: the scene state is restored on exit, the asset would not be.
        if (Application.isPlaying || string.IsNullOrEmpty(bakedAssetPath)) return;
        if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(bakedAssetPath) != null)
            UnityEditor.AssetDatabase.DeleteAsset(bakedAssetPath);
        bakedAssetPath = null;
#endif
    }

    bool SnapToGround(ref Vector3 localRoot, float slopeCos, out Collider hitCollider, out RaycastHit hit)
    {
        hitCollider = null;
        Vector3 worldStart = transform.TransformPoint(localRoot + Vector3.up * raycastHeight);
        if (Physics.Raycast(worldStart, Vector3.down, out hit, raycastHeight * 2f, groundLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.normal.y < slopeCos) return false;
            localRoot = transform.InverseTransformPoint(hit.point);
            hitCollider = hit.collider;
            return true;
        }
        return true; // nothing underneath: stay on the spawner's plane
    }

    // ------------------------------------------------------------ density masks

    readonly Dictionary<TerrainCollider, float[,,]> terrainAlphaCache = new Dictionary<TerrainCollider, float[,,]>();

    float SampleDensityMask(float x, float z, Rect bounds)
    {
        float u = Mathf.InverseLerp(bounds.xMin, bounds.xMax, x);
        float v = Mathf.InverseLerp(bounds.yMin, bounds.yMax, z);
        Color c = densityMask.GetPixelBilinear(u, v);
        float m = densityMaskChannel == MaskChannel.R ? c.r : densityMaskChannel == MaskChannel.G ? c.g : densityMaskChannel == MaskChannel.B ? c.b : c.a;
        return densityMaskInvert ? 1f - m : m;
    }

    float SampleTerrainLayer(TerrainCollider tc, Vector3 worldPoint)
    {
        var terrain = tc.GetComponent<Terrain>();
        if (terrain == null || terrain.terrainData == null) return 1f;
        var td = terrain.terrainData;
        if (terrainLayerIndex >= td.alphamapLayers) return 1f;

        if (!terrainAlphaCache.TryGetValue(tc, out var maps))
        {
            maps = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight); // one readback per terrain per rebuild
            terrainAlphaCache[tc] = maps;
        }
        Vector3 local = worldPoint - terrain.transform.position;
        int ax = Mathf.Clamp(Mathf.RoundToInt(local.x / td.size.x * (td.alphamapWidth - 1)), 0, td.alphamapWidth - 1);
        int ay = Mathf.Clamp(Mathf.RoundToInt(local.z / td.size.z * (td.alphamapHeight - 1)), 0, td.alphamapHeight - 1);
        return maps[ay, ax, terrainLayerIndex];
    }

    Material CreateMaterial(Rect bounds)
    {
        Material mat;
        if (baseMaterial != null)
        {
            mat = new Material(baseMaterial);
        }
        else
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"GrassSpawner: shader '{ShaderName}' not found. GrassBlade.shader must live in a Resources folder.", this);
                shader = Shader.Find("Unlit/Transparent Cutout");
            }
            mat = new Material(shader);
        }
        mat.name = "Grass (runtime)";
        mat.hideFlags = HideFlags.DontSave;
        mat.mainTexture = bladeTexture;
        // shader computes uv = (rootLocal.xz + bounds.xy) * bounds.zw
        mat.SetVector("_TintBounds", new Vector4(-bounds.xMin, -bounds.yMin,
            1f / Mathf.Max(bounds.width, 1e-3f), 1f / Mathf.Max(bounds.height, 1e-3f)));
        runtimeMaterial = mat;
        ApplySettings();
        return mat;
    }

    // ------------------------------------------------------------------- color

    const int RampWidth = 64;
    static readonly int ColorRampId = Shader.PropertyToID("_ColorRamp");
    static readonly int TintMapId = Shader.PropertyToID("_TintMap");
    static readonly int UseTextureColorId = Shader.PropertyToID("_UseTextureColor");
    static readonly int GradientByHeightId = Shader.PropertyToID("_GradientByHeight");
    static readonly int MaxBladeHeightId = Shader.PropertyToID("_MaxBladeHeight");
    static readonly int ColorVariationId = Shader.PropertyToID("_ColorVariation");
    static readonly int LightInfluenceId = Shader.PropertyToID("_LightInfluence");
    static readonly int WindHighlightId = Shader.PropertyToID("_WindHighlight");
    static readonly int BendDarkenId = Shader.PropertyToID("_BendDarken");
    static readonly int ViewFacingId = Shader.PropertyToID("_ViewFacing");
    static readonly int WindDirId = Shader.PropertyToID("_WindDir");
    static readonly int WindStrengthId = Shader.PropertyToID("_WindStrength");
    static readonly int WindSpeedId = Shader.PropertyToID("_WindSpeed");
    static readonly int WindFrequencyId = Shader.PropertyToID("_WindFrequency");
    static readonly int InteractStrengthId = Shader.PropertyToID("_InteractStrength");
    static readonly int TrampleMapId = Shader.PropertyToID("_TrampleMap");
    static readonly int ShadowStrengthId = Shader.PropertyToID("_ShadowStrength");
    static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
    static readonly int GroundBlendId = Shader.PropertyToID("_GroundBlend");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    const string ReceiveShadowsKeyword = "_GRASS_RECEIVE_SHADOWS";
    static readonly int MapBoundsId = Shader.PropertyToID("_MapBounds");
    static readonly int TrampleInteractorsId = Shader.PropertyToID("_TrampleInteractors");
    static readonly int TrampleCountId = Shader.PropertyToID("_TrampleCount");
    static readonly int TrampleRecoveryId = Shader.PropertyToID("_TrampleRecovery");
    static readonly int DtId = Shader.PropertyToID("_Dt");
    static readonly int MinDecayId = Shader.PropertyToID("_MinDecay");
    static readonly int TintTilingId = Shader.PropertyToID("_TintTiling");
    static readonly int TintParamsId = Shader.PropertyToID("_TintParams");
    static readonly int TintColorId = Shader.PropertyToID("_TintColor");

    static Gradient DefaultGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.13f, 0.42f, 0.10f), 0f),
                new GradientColorKey(new Color(0.60f, 0.88f, 0.25f), 1f)
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    /// <summary>Kept for compatibility; forwards to <see cref="ApplySettings"/>.</summary>
    public void ApplyColor() => ApplySettings();

    /// <summary>Pushes every look setting (color ramp, tint map, shading, wind, interaction) to the runtime material.
    /// Cheap; safe to call on every inspector change, no rebuild needed.</summary>
    public void ApplySettings()
    {
        if (runtimeMaterial == null) return;

        // shading
        runtimeMaterial.SetFloat(ColorVariationId, colorVariation);
        runtimeMaterial.SetFloat(LightInfluenceId, affectedByLighting ? sunInfluence : 0f);
        runtimeMaterial.SetFloat(WindHighlightId, windHighlight);
        runtimeMaterial.SetFloat(BendDarkenId, trampleDarken);

        // view
        runtimeMaterial.SetFloat(ViewFacingId, optimizeForPerspective ? 1f : 0f);

        // wind
        Vector2 wd = windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized : Vector2.right;
        runtimeMaterial.SetVector(WindDirId, new Vector4(wd.x, 0f, wd.y, 0f));
        runtimeMaterial.SetFloat(WindStrengthId, windStrength);
        runtimeMaterial.SetFloat(WindSpeedId, windSpeed);
        runtimeMaterial.SetFloat(WindFrequencyId, windFrequency);

        // ground blend
        runtimeMaterial.SetColor(GroundColorId, Color.white); // ground color comes from baked vertex colors
        runtimeMaterial.SetVector(GroundBlendId, new Vector4(groundBlendHeight, blendToGround ? groundBlendStrength : 0f,
            groundBlendMode == GroundBlendMode.AlphaFade ? 1f : 0f, 0f));

        // lighting
        runtimeMaterial.SetFloat(ShadowStrengthId, shadowStrength);
        bool receive = receiveShadows && affectedByLighting;
        if (receive) runtimeMaterial.EnableKeyword(ReceiveShadowsKeyword); else runtimeMaterial.DisableKeyword(ReceiveShadowsKeyword);
        ApplyRendererSettings();

        // interaction
        runtimeMaterial.SetFloat(InteractStrengthId, pushStrength);
        if (!trampleRecovery || trampleA == null) runtimeMaterial.SetTexture(TrampleMapId, Texture2D.blackTexture);
        if (trampleRecovery && trampleA == null && chunks.Count > 0) SetupTrampleMap();

        if (colorRamp == null)
        {
            colorRamp = new Texture2D(RampWidth, 1, TextureFormat.RGBA32, false)
            {
                name = "GrassColorRamp",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
        }

        var pixels = new Color32[RampWidth];
        for (int i = 0; i < RampWidth; i++)
        {
            float t = i / (RampWidth - 1f);
            Color c = colorMode == ColorMode.Solid ? solidColor : (gradient != null ? gradient.Evaluate(t) : solidColor);
            c.a = 1f;
            pixels[i] = c;
        }
        colorRamp.SetPixels32(pixels);
        colorRamp.Apply(false, false);

        runtimeMaterial.SetTexture(ColorRampId, colorRamp);
        runtimeMaterial.SetFloat(UseTextureColorId, useTextureColor ? 1f : 0f);
        runtimeMaterial.SetFloat(GradientByHeightId, gradientMode == GradientMode.BladeHeight ? 1f : 0f);
        runtimeMaterial.SetFloat(MaxBladeHeightId, Mathf.Max(heightRange.y, 0.01f));

        // Tint map + tiling/offset live here too so inspector changes preview instantly without a rebuild.
        runtimeMaterial.SetTexture(TintMapId, tintMap != null ? tintMap : Texture2D.whiteTexture);
        runtimeMaterial.SetVector(TintTilingId, new Vector4(tintMapTiling.x, tintMapTiling.y, tintMapOffset.x, tintMapOffset.y));
        runtimeMaterial.SetVector(TintParamsId, new Vector4(tintMap != null ? tintMapStrength : 0f, tintMapBlur, 0f, 0f));
        runtimeMaterial.SetColor(TintColorId, tintMapColor);
    }

    Renderer CreateChunk(int index, ChunkBuilder cb, float bloat, bool detail, bool persistent)
    {
        var go = new GameObject((detail ? ChunkName + "Detail" : ChunkName) + "_" + index);
        go.hideFlags = persistent ? HideFlags.NotEditable : HideFlags.DontSave | HideFlags.NotEditable;
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;

        // Baked meshes must keep their CPU copy so they can be serialized into the asset.
        var mesh = cb.ToMesh(bloat, keepReadable: persistent);
        mesh.name = (detail ? "GrassChunkDetail_" : "GrassChunk_") + index;
        mesh.hideFlags = persistent ? HideFlags.None : HideFlags.DontSave;

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = runtimeMaterial;
        mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        mr.receiveShadows = receiveShadows;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        mr.allowOcclusionWhenDynamic = false;
        return mr;
    }

    // ------------------------------------------------------------ ground color

    readonly Dictionary<Material, Color> materialColorCache = new Dictionary<Material, Color>();
    readonly Dictionary<Texture2D, Color> textureAverageCache = new Dictionary<Texture2D, Color>();

    bool BakesGroundColor => blendToGround && groundBlendMode == GroundBlendMode.Color;

    /// <summary>Color of the surface under a blade: terrain layers by weight, or material color x texture.</summary>
    Color SampleGroundColor(Collider c, RaycastHit hit)
    {
        if (c == null) return Color.white;

        if (c is TerrainCollider tc)
        {
            var terrain = tc.GetComponent<Terrain>();
            var td = terrain != null ? terrain.terrainData : null;
            if (td == null || td.alphamapLayers == 0) return Color.white;
            if (!terrainAlphaCache.TryGetValue(tc, out var maps))
            {
                maps = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight);
                terrainAlphaCache[tc] = maps;
            }
            Vector3 local = hit.point - terrain.transform.position;
            int ax = Mathf.Clamp(Mathf.RoundToInt(local.x / td.size.x * (td.alphamapWidth - 1)), 0, td.alphamapWidth - 1);
            int ay = Mathf.Clamp(Mathf.RoundToInt(local.z / td.size.z * (td.alphamapHeight - 1)), 0, td.alphamapHeight - 1);
            Color sum = Color.black; float wsum = 0f;
            var layers = td.terrainLayers;
            for (int l = 0; l < layers.Length && l < td.alphamapLayers; l++)
            {
                float w = maps[ay, ax, l];
                if (w <= 0.001f || layers[l] == null) continue;
                Color lc = AverageTextureColor(layers[l].diffuseTexture);
                sum += lc * w; wsum += w;
            }
            return wsum > 0f ? sum / wsum : Color.white;
        }

        var r = c.GetComponent<Renderer>();
        var m = r != null ? r.sharedMaterial : null;
        if (m == null) return Color.white;

        if (!materialColorCache.TryGetValue(m, out Color baseColor))
        {
            baseColor = m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : m.HasProperty(ColorId) ? m.GetColor(ColorId) : Color.white;
            baseColor.a = 1f;
            materialColorCache[m] = baseColor;
        }

        var tex = m.mainTexture as Texture2D;
        if (tex != null && tex.isReadable)
        {
            // Mesh colliders give real texture coordinates; primitive colliders don't -> use the average color.
            Color tc2 = c is MeshCollider ? tex.GetPixelBilinear(hit.textureCoord.x, hit.textureCoord.y) : AverageTextureColor(tex);
            return baseColor * tc2;
        }
        return baseColor;
    }

    Color AverageTextureColor(Texture2D tex)
    {
        if (tex == null) return Color.white;
        if (textureAverageCache.TryGetValue(tex, out var cached)) return cached;
        Color avg = Color.white;
        if (tex.isReadable)
        {
            int mip = Mathf.Max(tex.mipmapCount - 1, 0); // smallest mip ~ average
            var px = tex.GetPixels32(mip);
            long r = 0, g = 0, b = 0;
            foreach (var p in px) { r += p.r; g += p.g; b += p.b; }
            if (px.Length > 0) avg = new Color(r / (255f * px.Length), g / (255f * px.Length), b / (255f * px.Length), 1f);
        }
        textureAverageCache[tex] = avg;
        return avg;
    }

    /// <summary>Pushes shadow flags to every chunk renderer (called from ApplySettings and after building/adopting).</summary>
    void ApplyRendererSettings()
    {
        foreach (var c in chunks)
        {
            SetupRenderer(c.baseRenderer);
            SetupRenderer(c.detailRenderer);
        }
    }

    void SetupRenderer(Renderer r)
    {
        if (r == null) return;
        r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        r.receiveShadows = receiveShadows && affectedByLighting;
    }

    // ----------------------------------------------------------------- presets

    /// <summary>Applies a quality preset. Geometry settings need a Rebuild to take effect; the editor button does that.</summary>
    public void ApplyPreset(QualityPreset preset)
    {
        switch (preset)
        {
            case QualityPreset.MobileWebGL:
                maxBlades = 40000;
                segments = 1;
                optimizeForPerspective = true;
                cameraCulling = true; maxDistance = 60f; fadeRange = 12f; detailDistance = 25f;
                affectedByLighting = false; castShadows = false; receiveShadows = false;
                trampleMapResolution = 256;
                break;
            case QualityPreset.PC:
                maxBlades = 250000;
                segments = 2;
                optimizeForPerspective = true;
                cameraCulling = true; maxDistance = 160f; fadeRange = 25f; detailDistance = 70f;
                affectedByLighting = true; castShadows = true; receiveShadows = true; shadowStrength = 0.6f;
                trampleMapResolution = 512;
                break;
        }
        ApplySettings();
    }

    // ------------------------------------------------------------- trample map

    void SetupTrampleMap()
    {
        ReleaseTrampleMap();
        if (!trampleRecovery || runtimeMaterial == null) return;

        var shader = Shader.Find("Hidden/Grass/TrampleMap");
        if (shader == null)
        {
            Debug.LogWarning("GrassSpawner: shader 'Hidden/Grass/TrampleMap' not found; trample recovery disabled.", this);
            return;
        }
        trampleBlit = new Material(shader) { hideFlags = HideFlags.DontSave };

        int res = Mathf.Clamp(Mathf.ClosestPowerOfTwo(trampleMapResolution), 32, 1024);
        // Half float keeps slow recoveries smooth; 8-bit works too but needs a minimum decay step (see UpdateTrampleMap).
        var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        trampleA = NewTrampleRT(res, format);
        trampleB = NewTrampleRT(res, format);

        // start empty: direction (0,0) encodes to 0.5, amount 0, recovery 0
        var prev = RenderTexture.active;
        RenderTexture.active = trampleA; GL.Clear(false, true, new Color(0.5f, 0.5f, 0f, 0f));
        RenderTexture.active = trampleB; GL.Clear(false, true, new Color(0.5f, 0.5f, 0f, 0f));
        RenderTexture.active = prev;
        lastStampTime = -1f; pendingDecay = 0f; longestRecovery = recoveryTime;

        runtimeMaterial.SetTexture(TrampleMapId, trampleA);
    }

    static RenderTexture NewTrampleRT(int res, RenderTextureFormat format)
    {
        var rt = new RenderTexture(res, res, 0, format, RenderTextureReadWrite.Linear)
        {
            name = "GrassTrampleMap",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            hideFlags = HideFlags.DontSave
        };
        rt.Create();
        return rt;
    }

    void ReleaseTrampleMap()
    {
        if (trampleA != null) { trampleA.Release(); DestroySafe(trampleA); trampleA = null; }
        if (trampleB != null) { trampleB.Release(); DestroySafe(trampleB); trampleB = null; }
        if (trampleBlit != null) { DestroySafe(trampleBlit); trampleBlit = null; }
        if (runtimeMaterial != null) runtimeMaterial.SetTexture(TrampleMapId, Texture2D.blackTexture);
    }

    void UpdateTrampleMap()
    {
        if (!Application.isPlaying || !trampleRecovery || trampleA == null || runtimeMaterial == null) return;

        float dt = Time.deltaTime;

        // Interactors in spawner-local space; ignore ones far above or below the grass (flying over it).
        int count = 0;
        var active = GrassInteractor.Active;
        for (int i = 0; i < active.Count && count < GrassInteractor.MaxInteractors; i++)
        {
            var it = active[i];
            if (it == null) continue;
            Vector3 local = transform.InverseTransformPoint(it.WorldCenter);
            float r = it.radius;
            if (local.y < rootMinY - r * 2f || local.y > rootMaxY + r * 2f) continue;
            if (local.x < trampleBounds.xMin - r || local.x > trampleBounds.xMax + r ||
                local.z < trampleBounds.yMin - r || local.z > trampleBounds.yMax + r) continue;
            float rec = Mathf.Max(recoveryTime * it.recoveryScale, 0.05f);
            trampleRecoveryUpload[count] = new Vector4(rec, 0f, 0f, 0f);
            trampleUpload[count++] = new Vector4(local.x, local.z, r, it.strength);
            longestRecovery = Mathf.Max(longestRecovery, rec);
        }
        for (int i = count; i < trampleUpload.Length; i++) { trampleUpload[i] = Vector4.zero; trampleRecoveryUpload[i] = Vector4.zero; }

        if (count > 0) lastStampTime = Time.time;

        // Nothing to do: no interactor in range and every trail has fully recovered -> map is all zero.
        bool mapIsEmpty = lastStampTime < 0f || Time.time - lastStampTime > longestRecovery + 0.1f;
        if (count == 0 && mapIsEmpty) { pendingDecay = 0f; return; }

        // Off-screen: skip the blit but remember how much time passed so trails recover correctly on return.
        if (cameraCulling && VisibleChunkCount == 0 && count == 0) { pendingDecay += dt; return; }

        float elapsed = dt + pendingDecay;
        pendingDecay = 0f;

        trampleBlit.SetVector(MapBoundsId, new Vector4(trampleBounds.xMin, trampleBounds.yMin, trampleBounds.width, trampleBounds.height));
        trampleBlit.SetVectorArray(TrampleInteractorsId, trampleUpload);
        trampleBlit.SetVectorArray(TrampleRecoveryId, trampleRecoveryUpload);
        trampleBlit.SetFloat(TrampleCountId, count);
        trampleBlit.SetFloat(DtId, elapsed);
        trampleBlit.SetFloat(MinDecayId, trampleA.format == RenderTextureFormat.ARGB32 ? 1f / 255f : 0f); // 8-bit needs a real step

        Graphics.Blit(trampleA, trampleB, trampleBlit);
        (trampleA, trampleB) = (trampleB, trampleA);
        runtimeMaterial.SetTexture(TrampleMapId, trampleA);
    }

    // ----------------------------------------------------------------- culling

    static readonly int GrassFadeId = Shader.PropertyToID("_GrassFade");

    void UpdateCulling()
    {
        if (chunks.Count == 0) return;

        Camera cam = cullingCamera != null ? cullingCamera : Camera.main;
        // In edit mode (not playing) always show everything so the scene view is predictable.
        bool cull = cameraCulling && cam != null && Application.isPlaying;

        if (!cull)
        {
            if (runtimeMaterial != null) runtimeMaterial.SetVector(GrassFadeId, new Vector4(1e9f, 0f, 0f, 0f));
            foreach (var c in chunks)
            {
                if (c.baseRenderer != null) c.baseRenderer.forceRenderingOff = false;
                if (c.detailRenderer != null) c.detailRenderer.forceRenderingOff = false;
            }
            VisibleChunkCount = chunks.Count;
            return;
        }

        float fadeStart = Mathf.Max(0f, maxDistance - fadeRange);
        float invRange = fadeRange > 1e-3f ? 1f / fadeRange : 1e6f;
        runtimeMaterial.SetVector(GrassFadeId, new Vector4(fadeStart, invRange, 0f, 0f));

        GeometryUtility.CalculateFrustumPlanes(cam, frustum);
        Vector3 camPos = cam.transform.position;
        float maxSq = maxDistance * maxDistance;
        float detailSq = detailDistance * detailDistance;
        int visible = 0;

        foreach (var c in chunks)
        {
            Renderer any = c.baseRenderer != null ? c.baseRenderer : c.detailRenderer;
            Bounds b = any.bounds; // world-space, already padded for wind (valid while hidden via forceRenderingOff)
            float distSq = b.SqrDistance(camPos);

            bool show = distSq <= maxSq && GeometryUtility.TestPlanesAABB(frustum, b);
            bool showDetail = show && distSq <= detailSq;

            if (c.baseRenderer != null) c.baseRenderer.forceRenderingOff = !show;
            if (c.detailRenderer != null) c.detailRenderer.forceRenderingOff = !showDetail;
            if (show) visible++;
        }
        VisibleChunkCount = visible;
    }

    static void DestroySafe(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    // ---------------------------------------------------------------- globals

    static void UploadGlobals()
    {
        if (lastUploadFrame == Time.frameCount) return;
        lastUploadFrame = Time.frameCount;

        GrassInteractor.UploadGlobals();

        if (sun == null || !sun.isActiveAndEnabled)
        {
            sun = RenderSettings.sun;
            if (sun == null && !searchedSun)
            {
                searchedSun = true; // search once; set RenderSettings.sun to pick up a late-added light
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            }
        }
        Vector3 dir = sun != null ? sun.transform.forward : new Vector3(0.3f, -1f, 0.2f).normalized;
        Shader.SetGlobalVector("_GrassLightDir", dir);

        // Light color * intensity and scene ambient, so the grass follows time-of-day / lighting changes.
        Color lightColor = sun != null && sun.isActiveAndEnabled ? sun.color * sun.intensity : Color.white;
        lightColor.a = 1f;
        Shader.SetGlobalColor("_GrassLightColor", lightColor);
        Shader.SetGlobalColor("_GrassAmbientColor", SampleAmbient());
    }

    /// <summary>Scene ambient for an upward-facing surface, from RenderSettings (works in Built-in and URP).</summary>
    static readonly Vector3[] ambientDir = { Vector3.up };
    static readonly Color[] ambientOut = new Color[1];
    static Color SampleAmbient()
    {
        switch (RenderSettings.ambientMode)
        {
            case UnityEngine.Rendering.AmbientMode.Flat:
                return RenderSettings.ambientLight;
            case UnityEngine.Rendering.AmbientMode.Trilight:
                return Color.Lerp(RenderSettings.ambientEquatorColor, RenderSettings.ambientSkyColor, 0.75f);
            default: // Skybox: use the ambient probe when it has been generated, else the sky color / intensity
                RenderSettings.ambientProbe.Evaluate(ambientDir, ambientOut);
                Color c = ambientOut[0];
                if (c.maxColorComponent < 1e-3f) c = RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity;
                c.a = 1f;
                return c;
        }
    }

    // ----------------------------------------------------------------- gizmos

    void OnDrawGizmos()
    {
        DrawOutlineGizmo(new Color(0.5f, 1f, 0.3f, 0.9f));
    }

    void OnDrawGizmosSelected()
    {
        if (areaShape == AreaShape.Rectangle)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.5f, 1f, 0.3f, 0.08f);
            Gizmos.DrawCube(Vector3.zero, new Vector3(areaSize.x, 0.01f, areaSize.y));
        }
    }

    void DrawOutlineGizmo(Color color)
    {
        var outline = GetOutline();
        if (outline.Count < 2) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = color;
        for (int i = 0; i < outline.Count; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
            Gizmos.DrawLine(new Vector3(a.x, 0f, a.y), new Vector3(b.x, 0f, b.y));
        }
    }

    // -------------------------------------------------------------- mesh data

    sealed class ChunkBuilder
    {
        readonly List<Vector3> verts;
        readonly List<Vector2> uv0;
        readonly List<Vector4> uv1;
        readonly List<int> tris;

        readonly List<Color32> colors;

        public ChunkBuilder(int vertexCapacity)
        {
            verts = new List<Vector3>(vertexCapacity);
            uv0 = new List<Vector2>(vertexCapacity);
            uv1 = new List<Vector4>(vertexCapacity);
            tris = new List<int>(vertexCapacity * 3 / 2);
            colors = new List<Color32>();
        }

        public int VertexCount => verts.Count;

        public void AddCard(Vector3 root, Vector3 right, float height, int segments, float rand, Color32 groundColor, bool storeColor)
        {
            int rows = segments + 1;
            int baseIndex = verts.Count;
            var extra = new Vector4(rand, root.x, root.y, root.z);

            for (int r = 0; r < rows; r++)
            {
                float v = (float)r / segments;
                Vector3 p = root + Vector3.up * (height * v);
                verts.Add(p - right); uv0.Add(new Vector2(0f, v)); uv1.Add(extra);
                verts.Add(p + right); uv0.Add(new Vector2(1f, v)); uv1.Add(extra);
                if (storeColor) { colors.Add(groundColor); colors.Add(groundColor); }
            }

            for (int r = 0; r < segments; r++)
            {
                int i0 = baseIndex + r * 2;
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1);
                tris.Add(i0 + 1); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }
        }

        public Mesh ToMesh(float bloat, bool keepReadable)
        {
            var mesh = new Mesh();
            mesh.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            if (colors.Count == verts.Count) mesh.SetColors(colors); // only when ground colors were baked (missing = white)
            mesh.SetTriangles(tris, 0, false);
            mesh.RecalculateBounds();
            var b = mesh.bounds;
            b.Expand(new Vector3(bloat * 2f, bloat, bloat * 2f));
            mesh.bounds = b;
            mesh.UploadMeshData(!keepReadable); // free the CPU copy unless the mesh will be saved as an asset
            return mesh;
        }
    }
}
