using UnityEngine;

/// <summary>
/// Loops a rotation around an axis using quaternions (no Euler drift, no gimbal issues).
/// Spin: continuous rotation, one full turn per Duration. PingPong: swings between -Angle and +Angle.
/// The rotation is applied relative to the pose captured at Start, so you can pre-rotate the object freely.
/// </summary>
[DisallowMultipleComponent]
public class TweenRotation : MonoBehaviour
{
    public enum Mode { Spin, PingPong }
    public enum AxisSpace { Local, World }

    [Header("Target")]
    [Tooltip("Transform to rotate. Empty = this GameObject.")]
    public Transform target;

    [Header("Rotation")]
    public Mode mode = Mode.Spin;
    [Tooltip("Axis to rotate around.")]
    public Vector3 axis = Vector3.up;
    [Tooltip("Local: the axis follows the object's rest orientation. World: the axis is fixed in world space.")]
    public AxisSpace space = AxisSpace.Local;
    [Tooltip("Spin: seconds per full 360. PingPong: seconds for one swing from -Angle to +Angle.")]
    [Min(0.01f)] public float duration = 4f;
    [Tooltip("PingPong only: swing amplitude in degrees (rotates between -Angle and +Angle).")]
    [Range(0f, 180f)] public float angle = 45f;
    public bool reverse = false;
    [Tooltip("Optional easing over one cycle (Spin: one turn, PingPong: one swing). Linear = constant speed.")]
    public AnimationCurve ease = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    [Tooltip("Start phase, 0..1 of a cycle.")]
    [Range(0f, 1f)] public float startOffset = 0f;

    [Header("Playback")]
    public bool playOnStart = true;
    public bool useUnscaledTime = false;

    Quaternion restRotation;
    Vector3 axisWS;
    float time;      // seconds into the current cycle
    int direction = 1;
    bool playing;

    public bool IsPlaying => playing;
    /// <summary>Normalized progress 0..1 of the current cycle.</summary>
    public float Progress => Mathf.Repeat(time / duration, 1f);

    Transform Target => target != null ? target : transform;

    void Start()
    {
        CaptureRestPose();
        time = startOffset * duration;
        if (playOnStart) Play();
        Apply();
    }

    /// <summary>Re-captures the current rotation as the rest pose (the rotation the tween is applied on top of).</summary>
    public void CaptureRestPose()
    {
        Transform t = Target;
        restRotation = t.rotation;
        Vector3 a = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
        axisWS = space == AxisSpace.World ? a : restRotation * a;
    }

    public void Play() => playing = true;
    public void Pause() => playing = false;
    public void Restart() { time = startOffset * duration; direction = 1; Apply(); }
    /// <summary>Pauses and snaps back to the rest pose captured at Start (the scene's initial rotation).</summary>
    public void Stop() { playing = false; Restart(); }

    void Update()
    {
        if (!playing) return;
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        if (mode == Mode.Spin)
        {
            time = Mathf.Repeat(time + dt, duration);
        }
        else
        {
            time += dt * direction;
            if (time > duration) { time = duration - (time - duration); direction = -1; }
            else if (time < 0f) { time = -time; direction = 1; }
            time = Mathf.Clamp(time, 0f, duration);
        }
        Apply();
    }

    void Apply()
    {
        float u = Mathf.Clamp01(time / duration);
        float eased = ease != null && ease.length > 1 ? Mathf.Clamp01(ease.Evaluate(u)) : u;

        float degrees = mode == Mode.Spin
            ? eased * 360f                          // 0 -> 360 over one cycle
            : Mathf.Lerp(-angle, angle, eased);     // -angle -> +angle over one swing
        if (reverse) degrees = -degrees;

        // quaternion around a fixed axis composed with the rest pose: no accumulation, no drift
        Target.rotation = Quaternion.AngleAxis(degrees, axisWS) * restRotation;
    }

    void OnDrawGizmosSelected()
    {
        Transform t = Target;
        Vector3 a = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
        Vector3 ws = Application.isPlaying ? axisWS : (space == AxisSpace.World ? a : t.rotation * a);
        Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.9f);
        Gizmos.DrawLine(t.position - ws * 0.75f, t.position + ws * 0.75f);
        Gizmos.DrawWireSphere(t.position + ws * 0.75f, 0.05f);
    }
}
