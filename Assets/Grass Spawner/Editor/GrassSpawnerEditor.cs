using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GrassSpawner))]
public class GrassSpawnerEditor : Editor
{
    static readonly Color OutlineColor = new Color(0.5f, 1f, 0.3f, 1f);
    static readonly Color HandleColor = new Color(1f, 0.9f, 0.3f, 1f);
    static readonly Color RemoveColor = new Color(1f, 0.35f, 0.3f, 1f);

    bool areaDirty;

    public override void OnInspectorGUI()
    {
        var spawner = (GrassSpawner)target;

        DrawInspectorFields(spawner);

        EditorGUILayout.Space();
        if (spawner.bladeTexture == null)
            EditorGUILayout.HelpBox("Assign a blade texture (PNG with alpha) to spawn grass.", MessageType.Warning);

        if (spawner.areaShape == GrassSpawner.AreaShape.Spline)
        {
            EditorGUILayout.HelpBox(
                "Scene view: drag points to move them, Shift+click on the curve to add a point, " +
                "Ctrl/Cmd+click a point to remove it.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Rectangle Points"))
                {
                    Undo.RecordObject(spawner, "Reset Spline Points");
                    float hx = spawner.areaSize.x * 0.5f, hz = spawner.areaSize.y * 0.5f;
                    spawner.splinePoints = new List<Vector2>
                    {
                        new Vector2(-hx, -hz), new Vector2(hx, -hz), new Vector2(hx, hz), new Vector2(-hx, hz)
                    };
                    EditorUtility.SetDirty(spawner);
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Reset to Circle Points"))
                {
                    Undo.RecordObject(spawner, "Reset Spline Points");
                    float r = Mathf.Min(spawner.areaSize.x, spawner.areaSize.y) * 0.5f;
                    spawner.splinePoints = new List<Vector2>();
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i / 8f * Mathf.PI * 2f;
                        spawner.splinePoints.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
                    }
                    EditorUtility.SetDirty(spawner);
                    SceneView.RepaintAll();
                }
            }
        }
        else
        {
            EditorGUILayout.HelpBox("Scene view: drag the edge handles to resize the rectangle.", MessageType.None);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Quality preset", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Mobile / WebGL", "40k blades, 1 segment, no shadows, short culling. The lightweight default.")))
                ApplyPresetAndRebuild(spawner, GrassSpawner.QualityPreset.MobileWebGL);
            if (GUILayout.Button(new GUIContent("PC", "250k blades, 2 segments, cast + receive shadows, long culling, 512 trample map.")))
                ApplyPresetAndRebuild(spawner, GrassSpawner.QualityPreset.PC);
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Rebuild Grass", GUILayout.Height(28)))
            {
                spawner.Rebuild();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Clear", GUILayout.Height(28), GUILayout.Width(80)))
            {
                spawner.Clear();
                SceneView.RepaintAll();
            }
        }

        if (spawner.BladeCount > 0)
        {
            EditorGUILayout.HelpBox(spawner.HasBakedChunks
                ? "Baked into the scene: used as-is at runtime and in builds (Build On Start is off)."
                : "Preview only: rebuilt at runtime on Start (Build On Start is on).", MessageType.None);
            EditorGUILayout.HelpBox(
                $"{spawner.BladeCount:N0} blades   {spawner.VertexCount:N0} vertices   " +
                $"{spawner.ChunkCount} chunks, {spawner.VisibleChunkCount} visible (up to 2 draw calls each)\n" +
                $"~{spawner.EstimatedMeshBytes(spawner.VertexCount) / (1024f * 1024f):F1} MB mesh data on the GPU",
                MessageType.Info);
        }
        else
        {
            int est = spawner.EstimateBladeCount();
            int vertsPerBlade = spawner.QuadsPerBlade * (spawner.segments + 1) * 2;
            EditorGUILayout.HelpBox(
                $"Estimated: {est:N0} blades, {est * vertsPerBlade:N0} vertices, " +
                $"~{spawner.EstimatedMeshBytes(est * vertsPerBlade) / (1024f * 1024f):F1} MB mesh data", MessageType.None);
        }
    }

    /// <summary>Default inspector, but hides color fields that don't apply to the selected Color Mode
    /// and re-applies the color live when it changes.</summary>
    void DrawInspectorFields(GrassSpawner spawner)
    {
        serializedObject.Update();
        bool perspectiveBefore = spawner.optimizeForPerspective;
        EditorGUI.BeginChangeCheck();

        var prop = serializedObject.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (prop.name == "m_Script") { using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(prop); continue; }

            if ((prop.name == "tintMapTiling" || prop.name == "tintMapOffset" || prop.name == "tintMapStrength" || prop.name == "tintMapBlur" || prop.name == "tintMapColor")
                && spawner.tintMap == null) continue;
            if (prop.name == "solidColor" && spawner.colorMode != GrassSpawner.ColorMode.Solid) continue;
            if ((prop.name == "gradient" || prop.name == "gradientMode") && spawner.colorMode != GrassSpawner.ColorMode.Gradient) continue;
            if (prop.name == "areaSize" && spawner.areaShape != GrassSpawner.AreaShape.Rectangle) continue;
            if ((prop.name == "splinePoints" || prop.name == "splineSmoothness") && spawner.areaShape != GrassSpawner.AreaShape.Spline) continue;
            if ((prop.name == "edgeDensity" || prop.name == "edgeHeight") && spawner.edgeFalloff <= 0f) continue;
            if (prop.name == "crossQuads" && spawner.optimizeForPerspective) continue;
            if ((prop.name == "recoveryTime" || prop.name == "trampleMapResolution") && !spawner.trampleRecovery) continue;
            if ((prop.name == "densityMaskChannel" || prop.name == "densityMaskInvert") && spawner.densityMask == null) continue;
            if ((prop.name == "terrainLayerIndex" || prop.name == "terrainLayerInfluence") && !spawner.useTerrainLayer) continue;
            if (prop.name == "shadowStrength" && !spawner.receiveShadows) continue;
            if ((prop.name == "sunInfluence" || prop.name == "receiveShadows" || prop.name == "shadowStrength") && !spawner.affectedByLighting) continue;
            if ((prop.name == "groundBlendMode" || prop.name == "groundBlendHeight" || prop.name == "groundBlendStrength")
                && !spawner.blendToGround) continue;
            if (prop.name == "groundBlendMode" && spawner.blendToGround && spawner.groundBlendMode == GrassSpawner.GroundBlendMode.Color)
                EditorGUILayout.HelpBox("Color mode bakes the ground color under each blade into the mesh: click Rebuild after switching to it or after changing ground materials / terrain paint.", MessageType.None);
            if ((prop.name == "cullingCamera" || prop.name == "maxDistance" || prop.name == "fadeRange" || prop.name == "detailDistance")
                && !spawner.cameraCulling) continue;

            EditorGUILayout.PropertyField(prop, true);
        }

        bool changed = EditorGUI.EndChangeCheck();
        serializedObject.ApplyModifiedProperties();
        if (changed)
        {
            spawner.ApplySettings(); // instant preview, no rebuild needed
            if (spawner.optimizeForPerspective != perspectiveBefore && spawner.BladeCount > 0 && spawner.crossQuads)
                spawner.Rebuild(); // card count per blade changed
            SceneView.RepaintAll();
        }
    }

    static void ApplyPresetAndRebuild(GrassSpawner spawner, GrassSpawner.QualityPreset preset)
    {
        Undo.RecordObject(spawner, "Apply Grass Preset");
        spawner.ApplyPreset(preset);
        EditorUtility.SetDirty(spawner);
        if (spawner.BladeCount > 0) spawner.Rebuild();
        SceneView.RepaintAll();
    }

    // ------------------------------------------------------------ scene handles

    void OnSceneGUI()
    {
        var spawner = (GrassSpawner)target;
        Transform t = spawner.transform;
        Event e = Event.current;

        using (new Handles.DrawingScope(Matrix4x4.identity))
        {
            if (spawner.areaShape == GrassSpawner.AreaShape.Rectangle)
                DrawRectangleHandles(spawner, t);
            else
                DrawSplineHandles(spawner, t, e);
        }

        // Rebuild once the drag is released, only if grass was already built (keeps dragging responsive).
        if (areaDirty && e.type == EventType.MouseUp)
        {
            areaDirty = false;
            if (spawner.BladeCount > 0)
                spawner.Rebuild();
        }
    }

    void DrawRectangleHandles(GrassSpawner spawner, Transform t)
    {
        float hx = spawner.areaSize.x * 0.5f, hz = spawner.areaSize.y * 0.5f;

        // Edge midpoints: local (dir) and the axis they resize.
        Vector3[] localMid = { new Vector3(hx, 0, 0), new Vector3(-hx, 0, 0), new Vector3(0, 0, hz), new Vector3(0, 0, -hz) };
        Vector3[] localDir = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

        Handles.color = HandleColor;
        for (int i = 0; i < 4; i++)
        {
            Vector3 worldMid = t.TransformPoint(localMid[i]);
            Vector3 worldDir = t.TransformDirection(localDir[i]);
            float size = HandleUtility.GetHandleSize(worldMid) * 0.08f;

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.Slider(worldMid, worldDir, size, Handles.CubeHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(spawner, "Resize Grass Area");
                Vector3 local = t.InverseTransformPoint(moved);
                if (i < 2) spawner.areaSize.x = Mathf.Max(0.1f, Mathf.Abs(local.x) * 2f);
                else       spawner.areaSize.y = Mathf.Max(0.1f, Mathf.Abs(local.z) * 2f);
                EditorUtility.SetDirty(spawner);
                areaDirty = true;
            }
        }
    }

    void DrawSplineHandles(GrassSpawner spawner, Transform t, Event e)
    {
        var pts = spawner.splinePoints;

        // Outline
        var outline = spawner.GetOutline();
        if (outline.Count >= 2)
        {
            var world = new Vector3[outline.Count + 1];
            for (int i = 0; i < outline.Count; i++) world[i] = t.TransformPoint(new Vector3(outline[i].x, 0f, outline[i].y));
            world[outline.Count] = world[0];
            Handles.color = OutlineColor;
            Handles.DrawAAPolyLine(3f, world);
        }

        bool removeMode = e.control || e.command;
        bool addMode = e.shift && !removeMode;

        // Control point handles
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 worldP = t.TransformPoint(new Vector3(pts[i].x, 0f, pts[i].y));
            float size = HandleUtility.GetHandleSize(worldP) * 0.1f;
            Handles.color = removeMode ? RemoveColor : HandleColor;

            if (removeMode)
            {
                if (pts.Count > 3 && Handles.Button(worldP, Quaternion.identity, size, size * 1.5f, Handles.SphereHandleCap))
                {
                    Undo.RecordObject(spawner, "Remove Spline Point");
                    pts.RemoveAt(i);
                    EditorUtility.SetDirty(spawner);
                    areaDirty = true;
                    if (spawner.BladeCount > 0) spawner.Rebuild();
                    return;
                }
                continue;
            }

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(worldP, size, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                // Keep the point on the spawner's local XZ plane.
                Plane plane = new Plane(t.up, t.position);
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                if (plane.Raycast(ray, out float enter)) moved = ray.GetPoint(enter);

                Undo.RecordObject(spawner, "Move Spline Point");
                Vector3 local = t.InverseTransformPoint(moved);
                pts[i] = new Vector2(local.x, local.z);
                EditorUtility.SetDirty(spawner);
                areaDirty = true;
            }
            Handles.Label(worldP + Vector3.up * size * 2f, i.ToString(), EditorStyles.miniBoldLabel);
        }

        // Shift+click near the curve: insert a point on the closest segment.
        if (addMode)
        {
            Plane plane = new Plane(t.up, t.position);
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (plane.Raycast(ray, out float enter))
            {
                Vector3 hit = ray.GetPoint(enter);
                Vector3 local = t.InverseTransformPoint(hit);
                Vector2 p = new Vector2(local.x, local.z);
                int insertAfter = ClosestSegment(pts, p);

                Handles.color = new Color(HandleColor.r, HandleColor.g, HandleColor.b, 0.6f);
                Handles.SphereHandleCap(0, hit, Quaternion.identity, HandleUtility.GetHandleSize(hit) * 0.1f, EventType.Repaint);

                int id = GUIUtility.GetControlID(FocusType.Passive);
                if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
                if (e.type == EventType.MouseDown && e.button == 0 && HandleUtility.nearestControl == id)
                {
                    Undo.RecordObject(spawner, "Add Spline Point");
                    pts.Insert(insertAfter + 1, p);
                    EditorUtility.SetDirty(spawner);
                    areaDirty = true;
                    e.Use();
                }
            }
            SceneView.RepaintAll(); // keep the preview point following the mouse
        }
    }

    static int ClosestSegment(List<Vector2> pts, Vector2 p)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
            Vector2 ab = b - a;
            float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
            float u = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            float d = (a + ab * u - p).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------- menu

    [MenuItem("GameObject/3D Object/Grass Field", false, 10)]
    static void CreateGrassField(MenuCommand cmd)
    {
        var go = new GameObject("Grass Field");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        var spawner = go.AddComponent<GrassSpawner>();
        if (spawner.baseMaterial == null)
            spawner.baseMaterial = GrassSpawner.FindPackageAsset<Material>("GrassBlade");
        if (spawner.bladeTexture == null)
        {
            spawner.bladeTexture = GrassSpawner.FindPackageAsset<Texture2D>("Grass 1");
            if (spawner.bladeTexture == null)
            {
                // first texture in the package whose name starts with "Grass" (blade sprites), else any texture
                var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { GrassSpawner.PackageFolder });
                string fallback = null;
                foreach (var g in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(g);
                    string file = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (file.StartsWith("Grass")) { fallback = path; break; }
                    fallback ??= path;
                }
                if (fallback != null) spawner.bladeTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(fallback);
            }
        }
        Undo.RegisterCreatedObjectUndo(go, "Create Grass Field");
        Selection.activeObject = go;
    }
}
