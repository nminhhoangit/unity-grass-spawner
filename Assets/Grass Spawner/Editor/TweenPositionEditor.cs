using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TweenPosition))]
public class TweenPositionEditor : Editor
{
    static readonly Color HandleColor = new Color(0.3f, 0.8f, 1f, 1f);
    static readonly Color RemoveColor = new Color(1f, 0.35f, 0.3f, 1f);

    public override void OnInspectorGUI()
    {
        var mover = (TweenPosition)target;

        // Space toggle converts the points so the curve doesn't jump when switching.
        var newSpace = (TweenPosition.PathSpace)EditorGUILayout.EnumPopup(new GUIContent("Space", "Local: points follow this transform. World: absolute positions."), mover.space);
        if (newSpace != mover.space)
        {
            Undo.RecordObject(mover, "Change Path Space");
            mover.ConvertSpace(newSpace);
            EditorUtility.SetDirty(mover);
        }

        serializedObject.Update();
        var prop = serializedObject.GetIterator();
        bool enter = true;
        while (prop.NextVisible(enter))
        {
            enter = false;
            if (prop.name == "m_Script") { using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(prop); continue; }
            if (prop.name == "space") continue; // drawn above
            EditorGUILayout.PropertyField(prop, true);
        }
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Scene view: drag points to move them, Shift+click near the curve to add a point, " +
            "Ctrl/Cmd+click a point to remove it.", MessageType.None);
        EditorGUILayout.LabelField("Path length", $"{mover.Length:0.00} m   ({mover.Length / Mathf.Max(mover.duration, 0.01f):0.00} m/s)");

        if (mover.loopMode == TweenPosition.LoopMode.Loop && !mover.closed)
            EditorGUILayout.HelpBox("Loop mode with an open path jumps back to the start each lap. Enable Closed for a seamless loop.", MessageType.Warning);

        if (Application.isPlaying)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(mover.IsPlaying ? "Pause" : "Play")) { if (mover.IsPlaying) mover.Pause(); else mover.Play(); }
                if (GUILayout.Button("Restart")) mover.Restart();
            }
            EditorGUILayout.Slider("Progress", mover.Progress, 0f, 1f);
        }
    }

    void OnSceneGUI()
    {
        var mover = (TweenPosition)target;
        Event e = Event.current;
        var pts = mover.points;
        Matrix4x4 toWorld = Application.isPlaying ? mover.PathMatrix
                          : mover.space == TweenPosition.PathSpace.World ? Matrix4x4.identity : mover.transform.localToWorldMatrix;
        Matrix4x4 toPath = toWorld.inverse;

        using (new Handles.DrawingScope(Matrix4x4.identity))
        {
            // curve
            var s = mover.GetSamples();
            if (s.Count >= 2)
            {
                var world = new Vector3[s.Count];
                for (int i = 0; i < s.Count; i++) world[i] = toWorld.MultiplyPoint3x4(s[i]);
                Handles.color = HandleColor;
                Handles.DrawAAPolyLine(3f, world);
            }

            bool removeMode = e.control || e.command;
            bool addMode = e.shift && !removeMode;

            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 worldP = toWorld.MultiplyPoint3x4(pts[i]);
                float size = HandleUtility.GetHandleSize(worldP) * 0.1f;
                Handles.color = removeMode ? RemoveColor : HandleColor;

                if (removeMode)
                {
                    if (pts.Count > 2 && Handles.Button(worldP, Quaternion.identity, size, size * 1.5f, Handles.SphereHandleCap))
                    {
                        Undo.RecordObject(mover, "Remove Path Point");
                        pts.RemoveAt(i);
                        EditorUtility.SetDirty(mover);
                        return;
                    }
                    continue;
                }

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(worldP, size, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(mover, "Move Path Point");
                    pts[i] = toPath.MultiplyPoint3x4(moved);
                    EditorUtility.SetDirty(mover);
                }
                Handles.Label(worldP + Vector3.up * size * 2f, i.ToString(), EditorStyles.miniBoldLabel);
            }

            // Shift+click: insert a point at the midpoint of the segment closest to the mouse.
            if (addMode && pts.Count >= 2)
            {
                int seg = ClosestSegmentToMouse(mover, toWorld, e.mousePosition, out Vector3 insertWorld);
                Handles.color = new Color(HandleColor.r, HandleColor.g, HandleColor.b, 0.6f);
                Handles.SphereHandleCap(0, insertWorld, Quaternion.identity, HandleUtility.GetHandleSize(insertWorld) * 0.1f, EventType.Repaint);

                int id = GUIUtility.GetControlID(FocusType.Passive);
                if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
                if (e.type == EventType.MouseDown && e.button == 0 && HandleUtility.nearestControl == id)
                {
                    Undo.RecordObject(mover, "Add Path Point");
                    pts.Insert(seg + 1, toPath.MultiplyPoint3x4(insertWorld));
                    EditorUtility.SetDirty(mover);
                    e.Use();
                }
                SceneView.RepaintAll();
            }
        }
    }

    static int ClosestSegmentToMouse(TweenPosition mover, Matrix4x4 toWorld, Vector2 mouse, out Vector3 insertWorld)
    {
        var pts = mover.points;
        int count = mover.closed ? pts.Count : pts.Count - 1;
        int best = 0;
        float bestDist = float.MaxValue;
        insertWorld = toWorld.MultiplyPoint3x4(pts[0]);
        for (int i = 0; i < count; i++)
        {
            Vector3 a = toWorld.MultiplyPoint3x4(pts[i]);
            Vector3 b = toWorld.MultiplyPoint3x4(pts[(i + 1) % pts.Count]);
            float d = HandleUtility.DistanceToLine(a, b);
            if (d < bestDist) { bestDist = d; best = i; insertWorld = (a + b) * 0.5f; }
        }
        return best;
    }
}
