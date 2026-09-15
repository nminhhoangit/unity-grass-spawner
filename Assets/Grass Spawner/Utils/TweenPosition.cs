using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moves a target Transform along a Catmull-Rom spline in a loop, at constant speed.
/// Put this on an empty "Path" GameObject (points are stored in its local space), assign the object to
/// move as Target, and edit the points with handles in the Scene view (see TweenPositionEditor).
/// Handy for driving a GrassInteractor ball through the grass.
/// </summary>
[DisallowMultipleComponent]
public class TweenPosition : MonoBehaviour
{
    public enum LoopMode { Loop, PingPong }
    public enum PathSpace { Local, World }

    [Header("Target")]
    [Tooltip("Transform to move. Leave empty to move this GameObject itself.")]
    public Transform target;
    [Tooltip("Rotate the target to face its direction of travel.")]
    public bool orientToPath = true;
    [Tooltip("Extra rotation applied after orienting (e.g. if the model's forward isn't +Z).")]
    public Vector3 rotationOffset;

    [Header("Path (edit with handles in the Scene view)")]
    [Tooltip("Local: points follow this transform (move the Path object to move the whole route). World: points are absolute world positions and ignore this transform.")]
    public PathSpace space = PathSpace.Local;
    public List<Vector3> points = new List<Vector3>
    {
        new Vector3(-5f, 0.5f, -5f), new Vector3(5f, 0.5f, -5f), new Vector3(5f, 0.5f, 5f), new Vector3(-5f, 0.5f, 5f)
    };
    [Tooltip("Closed: the curve returns to the first point. Required for Loop mode to be seamless.")]
    public bool closed = true;
    [Tooltip("0 = straight lines between points, 1 = fully smooth curve.")]
    [Range(0f, 1f)] public float smoothness = 1f;

    [Header("Motion")]
    public LoopMode loopMode = LoopMode.Loop;
    [Tooltip("Seconds for one full traversal of the path (constant speed along the curve).")]
    [Min(0.01f)] public float duration = 8f;
    [Tooltip("Optional easing over one traversal (x = 0..1 normalized progress). Leave linear for constant speed.")]
    public AnimationCurve ease = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    [Tooltip("Start offset along the path, 0..1.")]
    [Range(0f, 1f)] public float startOffset = 0f;
    public bool playOnStart = true;

    const int SamplesPerSegment = 16;

    readonly List<Vector3> samples = new List<Vector3>();   // local space, resampled curve
    readonly List<float> cumulative = new List<float>();    // arc length at each sample
    float totalLength;
    float distance;           // current arc-length position
    int direction = 1;        // for ping-pong
    bool playing;
    int cachedHash;
    Matrix4x4 pathToWorld;    // frozen frame of the path while moving self (or a child), so moving doesn't move the path
    bool usesFrozenFrame;
    Rigidbody targetBody;

    public bool IsPlaying => playing;
    public float Length { get { EnsureTable(); return totalLength; } }
    /// <summary>Normalized progress 0..1 along the curve.</summary>
    public float Progress => totalLength > 0f ? distance / totalLength : 0f;

    void Start()
    {
        EnsureTable();
        Transform t = target != null ? target : transform;
        // If the moved object is this transform or one of its children, moving it would also move the path's
        // frame and the object would run away. Freeze the frame once at start instead.
        usesFrozenFrame = space == PathSpace.Local && (t == transform || t.IsChildOf(transform));
        pathToWorld = transform.localToWorldMatrix;
        targetBody = t.GetComponent<Rigidbody>();
        distance = startOffset * totalLength;
        if (playOnStart) Play();
        Apply();
    }

    void Update()
    {
        if (!playing) return;
        EnsureTable();
        if (totalLength <= 0f) return;

        distance += totalLength / Mathf.Max(duration, 0.01f) * Time.deltaTime * direction;

        if (loopMode == LoopMode.Loop)
        {
            distance = Mathf.Repeat(distance, totalLength);
        }
        else
        {
            if (distance > totalLength) { distance = totalLength - (distance - totalLength); direction = -1; }
            else if (distance < 0f)    { distance = -distance; direction = 1; }
            distance = Mathf.Clamp(distance, 0f, totalLength);
        }
        Apply();
    }

    public void Play() => playing = true;
    public void Pause() => playing = false;
    public void Restart() { distance = startOffset * Length; direction = 1; Apply(); }
    /// <summary>Pauses and snaps back to the start of the path.</summary>
    public void Stop() { playing = false; Restart(); }

    /// <summary>Jump to a normalized position (0..1) on the curve.</summary>
    public void SetProgress(float u)
    {
        distance = Mathf.Clamp01(u) * Length;
        Apply();
    }

    /// <summary>Path-space to world-space matrix, honoring Space and the frozen frame.</summary>
    public Matrix4x4 PathMatrix => space == PathSpace.World ? Matrix4x4.identity
                                 : usesFrozenFrame ? pathToWorld : transform.localToWorldMatrix;

    /// <summary>Converts the stored points between Local and World so the curve stays where it is.</summary>
    public void ConvertSpace(PathSpace newSpace)
    {
        if (newSpace == space) return;
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < points.Count; i++)
            points[i] = newSpace == PathSpace.World ? m.MultiplyPoint3x4(points[i]) : m.inverse.MultiplyPoint3x4(points[i]);
        space = newSpace;
    }

    void Apply()
    {
        Transform t = target != null ? target : transform;
        float u = totalLength > 0f ? distance / totalLength : 0f;
        float eased = ease != null && ease.length > 1 ? Mathf.Clamp01(ease.Evaluate(u)) : u;
        Matrix4x4 m = PathMatrix;
        Vector3 posWorld = m.MultiplyPoint3x4(EvaluateLocal(eased));

        Quaternion rot = t.rotation;
        if (orientToPath)
        {
            float aheadU = loopMode == LoopMode.Loop ? Mathf.Repeat(eased + 0.005f * direction, 1f) : Mathf.Clamp01(eased + 0.005f * direction);
            Vector3 dir = m.MultiplyPoint3x4(EvaluateLocal(aheadU)) - posWorld;
            if (direction < 0) dir = -dir;
            if (dir.sqrMagnitude > 1e-8f)
                rot = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(rotationOffset);
        }

        if (targetBody != null && !targetBody.isKinematic)
        {
            // a dynamic body would fight the scripted motion; drive it kinematically
            targetBody.isKinematic = true;
        }
        if (targetBody != null)
        {
            targetBody.MovePosition(posWorld);
            if (orientToPath) targetBody.MoveRotation(rot);
        }
        else
        {
            t.SetPositionAndRotation(posWorld, orientToPath ? rot : t.rotation);
        }
    }

    // ------------------------------------------------------------------ curve

    /// <summary>World-space position at normalized arc-length u (0..1).</summary>
    public Vector3 Evaluate(float u) => PathMatrix.MultiplyPoint3x4(EvaluateLocal(Mathf.Clamp01(u)));

    Vector3 EvaluateLocal(float u)
    {
        EnsureTable();
        if (samples.Count == 0) return Vector3.zero;
        if (samples.Count == 1 || totalLength <= 0f) return samples[0];

        float d = u * totalLength;
        // binary search the cumulative table
        int lo = 0, hi = cumulative.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) >> 1;
            if (cumulative[mid] <= d) lo = mid; else hi = mid;
        }
        float segLen = cumulative[hi] - cumulative[lo];
        float f = segLen > 1e-6f ? (d - cumulative[lo]) / segLen : 0f;
        return Vector3.Lerp(samples[lo], samples[hi], f);
    }

    /// <summary>Resampled curve in path space (for gizmos / editor).</summary>
    public IReadOnlyList<Vector3> GetSamples() { EnsureTable(); return samples; }

    void EnsureTable()
    {
        int hash = ComputeHash();
        if (hash == cachedHash && samples.Count > 0) return;
        cachedHash = hash;
        BuildTable();
    }

    int ComputeHash()
    {
        unchecked
        {
            int h = points.Count * 397 ^ (closed ? 1 : 0) ^ ((int)space << 1);
            h = h * 31 + smoothness.GetHashCode();
            for (int i = 0; i < points.Count; i++) h = h * 31 + points[i].GetHashCode();
            return h;
        }
    }

    void BuildTable()
    {
        samples.Clear();
        cumulative.Clear();
        totalLength = 0f;
        int n = points.Count;
        if (n == 0) return;
        if (n == 1) { samples.Add(points[0]); cumulative.Add(0f); return; }

        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            Vector3 p0 = GetPoint(i - 1), p1 = GetPoint(i), p2 = GetPoint(i + 1), p3 = GetPoint(i + 2);
            for (int s = 0; s < SamplesPerSegment; s++)
            {
                float t = (float)s / SamplesPerSegment;
                Vector3 curve = CatmullRom(p0, p1, p2, p3, t);
                samples.Add(Vector3.Lerp(Vector3.Lerp(p1, p2, t), curve, smoothness));
            }
        }
        samples.Add(closed ? samples[0] : points[n - 1]);

        cumulative.Add(0f);
        for (int i = 1; i < samples.Count; i++)
        {
            totalLength += Vector3.Distance(samples[i - 1], samples[i]);
            cumulative.Add(totalLength);
        }
    }

    Vector3 GetPoint(int i)
    {
        int n = points.Count;
        if (closed) return points[(i % n + n) % n];
        return points[Mathf.Clamp(i, 0, n - 1)];
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t
                     + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                     + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // ----------------------------------------------------------------- gizmos

    void OnDrawGizmos()
    {
        var s = GetSamples();
        if (s.Count < 2) return;
        Gizmos.matrix = Application.isPlaying ? PathMatrix
                      : space == PathSpace.World ? Matrix4x4.identity : transform.localToWorldMatrix;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        for (int i = 1; i < s.Count; i++) Gizmos.DrawLine(s[i - 1], s[i]);
    }
}
