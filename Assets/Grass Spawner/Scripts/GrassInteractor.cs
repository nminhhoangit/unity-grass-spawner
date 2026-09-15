using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Put this on anything that should push grass aside (player, ball, wheels...).
/// Up to 8 active interactors are uploaded to the grass shader each frame as world-space spheres.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class GrassInteractor : MonoBehaviour
{
    public const int MaxInteractors = 8;

    [Tooltip("Radius of influence in meters. Blades inside are pushed away from the center and flattened.")]
    [Min(0.01f)] public float radius = 1.0f;
    [Tooltip("Offset from this transform, in local space (e.g. lower the sphere to the feet).")]
    public Vector3 offset = Vector3.zero;
    [Tooltip("How flat this interactor presses the grass. 1 = fully flattened, 0.3 = light brush.")]
    [Range(0f, 1f)] public float strength = 1f;
    [Tooltip("Multiplies the spawner's Recovery Time for trails left by this interactor. 2 = grass stays down twice as long (heavy object), 0.5 = springs back twice as fast.")]
    [Min(0.05f)] public float recoveryScale = 1f;

    static readonly List<GrassInteractor> active = new List<GrassInteractor>(MaxInteractors);
    static readonly Vector4[] buffer = new Vector4[MaxInteractors];
    static readonly Vector4[] paramBuffer = new Vector4[MaxInteractors];
    static readonly int interactorsId = Shader.PropertyToID("_GrassInteractors");
    static readonly int paramsId = Shader.PropertyToID("_GrassInteractorParams");
    static readonly int countId = Shader.PropertyToID("_GrassInteractorCount");
    static int lastUploadFrame = -1;

    public Vector3 WorldCenter => transform.TransformPoint(offset);

    /// <summary>Currently enabled interactors (read-only).</summary>
    public static IReadOnlyList<GrassInteractor> Active => active;

    void OnEnable()
    {
        if (!active.Contains(this)) active.Add(this);
    }

    void OnDisable()
    {
        active.Remove(this);
        lastUploadFrame = -1; // force re-upload so a removed interactor stops affecting grass
    }

    /// <summary>Uploads the current interactor set to shader globals. Safe to call many times per frame.</summary>
    public static void UploadGlobals()
    {
        if (lastUploadFrame == Time.frameCount) return;
        lastUploadFrame = Time.frameCount;

        int count = 0;
        for (int i = 0; i < active.Count && count < MaxInteractors; i++)
        {
            var it = active[i];
            if (it == null) continue;
            Vector3 p = it.WorldCenter;
            paramBuffer[count] = new Vector4(it.strength, 0f, 0f, 0f);
            buffer[count++] = new Vector4(p.x, p.y, p.z, it.radius);
        }
        for (int i = count; i < MaxInteractors; i++) { buffer[i] = Vector4.zero; paramBuffer[i] = Vector4.zero; }

        Shader.SetGlobalVectorArray(interactorsId, buffer);
        Shader.SetGlobalVectorArray(paramsId, paramBuffer);
        Shader.SetGlobalFloat(countId, count);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(WorldCenter, radius);
    }
}
