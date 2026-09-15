using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Subtle camera parallax driven by the mouse cursor (or touch), always looking at a focus point.
/// Put it on the camera. Its position and rotation at Start are the rest pose; the cursor's offset from
/// the screen center shifts the camera (Offset mode) or orbits it around the focus point (Orbit mode).
/// Works with both the legacy Input Manager and the Input System package.
/// </summary>
[DisallowMultipleComponent]
public class CameraParallax : MonoBehaviour
{
    public enum Mode { Offset, Orbit }

    [Header("Focus")]
    [Tooltip("Point the camera keeps looking at. Empty = the point Focus Distance ahead of the camera at Start.")]
    public Transform focusPoint;
    [Tooltip("Used when Focus Point is empty.")]
    [Min(0.1f)] public float focusDistance = 10f;

    [Header("Parallax")]
    [Tooltip("Off: the camera stays at its rest pose (zoom still works). On: the cursor / touch position shifts or orbits the camera.")]
    public bool parallaxEnabled = true;
    public Mode mode = Mode.Offset;
    [Tooltip("Offset mode: max camera shift in meters (x = horizontal, y = vertical) when the cursor is at the screen edge.")]
    public Vector2 maxOffset = new Vector2(1.5f, 0.75f);
    [Tooltip("Orbit mode: max rotation in degrees around the focus point (x = yaw, y = pitch) when the cursor is at the screen edge.")]
    public Vector2 maxAngle = new Vector2(12f, 6f);
    [Tooltip("Flip the horizontal / vertical direction.")]
    public bool invertX, invertY;
    [Tooltip("Dead zone around the screen center where nothing moves (0-1 of half the screen).")]
    [Range(0f, 0.5f)] public float deadZone = 0.02f;

    [Header("Drag to orbit")]
    [Tooltip("Hold the mouse button (or one finger) and drag to orbit the camera around the focus point.")]
    public bool dragEnabled = true;
    [Tooltip("Mouse button that drags: 0 = left, 1 = right, 2 = middle.")]
    [Range(0, 2)] public int dragButton = 0;
    [Tooltip("Horizontal drag rotates around the focus (yaw).")]
    public bool dragYaw = true;
    [Tooltip("Vertical drag changes the camera elevation (pitch).")]
    public bool dragPitch = true;
    [Tooltip("Degrees of rotation per pixel dragged.")]
    [Range(0.02f, 1f)] public float dragSensitivity = 0.25f;
    [Tooltip("Allowed camera elevation above the horizon while orbiting, in degrees (x = min, y = max).")]
    public Vector2 elevationLimits = new Vector2(5f, 80f);
    [Tooltip("How quickly the orbit follows the drag. Higher = snappier.")]
    [Range(0.5f, 30f)] public float dragSmoothing = 12f;
    public bool invertDragX, invertDragY;
    [Tooltip("Spring back to the rest view when the drag is released.")]
    public bool returnOnRelease = false;
    [Tooltip("Ignore drags that start over UI elements (toggles, buttons).")]
    public bool ignoreUI = true;

    [Header("Zoom (mouse wheel / pinch)")]
    public bool zoomEnabled = true;
    [Tooltip("Distance change per wheel notch, as a fraction of the current distance (0.1 = 10% per notch).")]
    [Range(0.01f, 0.5f)] public float zoomStep = 0.12f;
    [Tooltip("Closest / farthest distance from the focus point, in meters.")]
    public Vector2 distanceRange = new Vector2(2f, 40f);
    [Tooltip("How quickly the zoom settles. Higher = snappier.")]
    [Range(0.5f, 30f)] public float zoomSmoothing = 8f;
    public bool invertZoom;

    [Header("Feel")]
    [Tooltip("How quickly the camera follows the cursor. Higher = snappier.")]
    [Range(0.5f, 30f)] public float smoothing = 6f;
    [Tooltip("Ease the response: 1 = linear, >1 = slow near the center and faster at the edges.")]
    [Range(0.5f, 3f)] public float responseCurve = 1.4f;
    [Tooltip("Drift back to center when there is no pointer (e.g. on touch devices between touches).")]
    public bool returnToCenterWithoutPointer = true;
    public bool useUnscaledTime = true;

    Vector3 restPosition;
    Quaternion restRotation;
    Vector3 restFocus;
    Vector3 restDir;          // unit vector from focus to the rest position
    float targetDistance, currentDistance;
    Vector2 current;          // smoothed normalized cursor, -1..1
    float lastPinchDistance = -1f;

    // drag orbit state (degrees)
    float targetYaw, targetPitch, yaw, pitch;
    bool dragging;
    Vector2 lastDragPos;

    /// <summary>True while the user is dragging to orbit.</summary>
    public bool IsDragging => dragging;

    /// <summary>Current distance from the focus point (Offset mode measures it along the rest direction).</summary>
    public float Distance => currentDistance;

    /// <summary>Current focus point in world space.</summary>
    public Vector3 Focus => focusPoint != null ? focusPoint.position : restFocus;

    void Start()
    {
        CaptureRestPose();
    }

    /// <summary>Re-captures the current transform as the rest pose (call after moving the camera by script).</summary>
    public void CaptureRestPose()
    {
        restPosition = transform.position;
        restRotation = transform.rotation;
        restFocus = focusPoint != null ? focusPoint.position : transform.position + transform.forward * focusDistance;
        Vector3 offset = restPosition - restFocus;
        currentDistance = targetDistance = Mathf.Max(offset.magnitude, 0.01f);
        restDir = offset / currentDistance;
        current = Vector2.zero;
    }

    /// <summary>Clears the drag orbit so the camera returns to the rest view.</summary>
    public void ResetOrbit(bool immediate = false)
    {
        targetYaw = targetPitch = 0f;
        if (immediate) { yaw = pitch = 0f; }
    }

    /// <summary>Set the zoom distance directly (clamped to Distance Range).</summary>
    public void SetDistance(float distance, bool immediate = false)
    {
        targetDistance = Mathf.Clamp(distance, distanceRange.x, distanceRange.y);
        if (immediate) currentDistance = targetDistance;
    }

    void LateUpdate()
    {
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        // drag orbit input
        if (dragEnabled) ReadDrag();
        else dragging = false;
        if (returnOnRelease && !dragging) { targetYaw = 0f; targetPitch = 0f; }
        yaw = Mathf.Lerp(yaw, targetYaw, 1f - Mathf.Exp(-dragSmoothing * dt));
        pitch = Mathf.Lerp(pitch, targetPitch, 1f - Mathf.Exp(-dragSmoothing * dt));

        Vector2 target = Vector2.zero;
        if (parallaxEnabled && !dragging) // while dragging, the pointer drives the orbit, not the parallax
        {
            target = ReadPointer(out bool hasPointer);
            if (!hasPointer && !returnToCenterWithoutPointer) target = current;
        }
        else if (dragging) target = current;

        current = Vector2.Lerp(current, target, 1f - Mathf.Exp(-smoothing * dt));

        // zoom: dolly along the rest direction toward / away from the focus point
        if (zoomEnabled)
        {
            float notches = ReadZoom();
            if (notches != 0f)
            {
                if (invertZoom) notches = -notches;
                targetDistance = Mathf.Clamp(targetDistance * Mathf.Pow(1f - zoomStep, notches), distanceRange.x, distanceRange.y);
            }
        }
        currentDistance = Mathf.Lerp(currentDistance, targetDistance, 1f - Mathf.Exp(-zoomSmoothing * dt));

        Vector3 focus = Focus;

        // orbit: yaw around world up, then pitch around the yawed right axis, applied on top of the rest view
        Quaternion yawQ = Quaternion.AngleAxis(yaw, Vector3.up);
        Vector3 yawedRight = yawQ * (restRotation * Vector3.right);
        Quaternion orbit = Quaternion.AngleAxis(pitch, yawedRight) * yawQ;
        Quaternion viewRot = orbit * restRotation;
        Vector3 right = viewRot * Vector3.right;
        Vector3 up = viewRot * Vector3.up;
        Vector3 dir = orbit * restDir;
        Vector3 basePos = focus + dir * currentDistance; // rest pose at the current zoom and orbit

        if (mode == Mode.Offset)
        {
            // scale the shift with distance so the parallax feels the same when zoomed in
            float k = currentDistance / Mathf.Max(focusDistanceAtRest, 0.01f);
            Vector3 pos = basePos + right * (current.x * maxOffset.x * k) + up * (current.y * maxOffset.y * k);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, up));
        }
        else
        {
            Quaternion pYaw = Quaternion.AngleAxis(current.x * maxAngle.x, up);
            Quaternion pPitch = Quaternion.AngleAxis(-current.y * maxAngle.y, right);
            Vector3 pos = focus + pYaw * pPitch * (dir * currentDistance);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, up));
        }
    }

    float focusDistanceAtRest => (restPosition - restFocus).magnitude;

    /// <summary>Reads mouse / one-finger drag and accumulates it into the orbit yaw and pitch targets.</summary>
    void ReadDrag()
    {
        bool pressed = false, down = false;
        Vector2 pos = Vector2.zero;
        int touches = 0;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var ts = Touchscreen.current;
        if (ts != null && ts.primaryTouch.press.isPressed)
        {
            touches = 0; foreach (var t in ts.touches) if (t.press.isPressed) touches++;
            pressed = touches == 1;
            down = ts.primaryTouch.press.wasPressedThisFrame;
            pos = ts.primaryTouch.position.ReadValue();
        }
        else if (Mouse.current != null)
        {
            var btn = dragButton == 1 ? Mouse.current.rightButton : dragButton == 2 ? Mouse.current.middleButton : Mouse.current.leftButton;
            pressed = btn.isPressed; down = btn.wasPressedThisFrame;
            pos = Mouse.current.position.ReadValue();
        }
#else
        if (Input.touchCount > 0)
        {
            touches = Input.touchCount;
            var t = Input.GetTouch(0);
            pressed = touches == 1;
            down = t.phase == TouchPhase.Began;
            pos = t.position;
        }
        else
        {
            pressed = Input.GetMouseButton(dragButton);
            down = Input.GetMouseButtonDown(dragButton);
            pos = Input.mousePosition;
        }
#endif

        if (down)
        {
            bool overUI = ignoreUI && UnityEngine.EventSystems.EventSystem.current != null
                          && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            dragging = !overUI;
            lastDragPos = pos;
            return;
        }
        if (!pressed) { dragging = false; return; }
        if (!dragging) return;

        Vector2 delta = pos - lastDragPos;
        lastDragPos = pos;
        float sx = invertDragX ? -1f : 1f, sy = invertDragY ? -1f : 1f;
        if (dragYaw) targetYaw += delta.x * dragSensitivity * sx;
        if (dragPitch) targetPitch += delta.y * dragSensitivity * sy; // drag up = camera rises

        // keep the total elevation (rest + pitch) inside the limits
        float restElevation = Mathf.Asin(Mathf.Clamp(restDir.y, -1f, 1f)) * Mathf.Rad2Deg;
        targetPitch = Mathf.Clamp(targetPitch, elevationLimits.x - restElevation, elevationLimits.y - restElevation);
    }

    /// <summary>Zoom input in "wheel notches" (positive = zoom in). Mouse wheel, or pinch on touch screens.</summary>
    float ReadZoom()
    {
        float notches = 0f;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        if (Mouse.current != null)
            notches = Mouse.current.scroll.ReadValue().y / 120f; // Input System reports pixels, ~120 per notch
        var ts = Touchscreen.current;
        if (ts != null && ts.touches.Count >= 2 && ts.touches[0].press.isPressed && ts.touches[1].press.isPressed)
        {
            float d = Vector2.Distance(ts.touches[0].position.ReadValue(), ts.touches[1].position.ReadValue());
            if (lastPinchDistance > 0f) notches += (d - lastPinchDistance) / (Screen.dpi > 0 ? Screen.dpi : 160f) * 4f;
            lastPinchDistance = d;
        }
        else lastPinchDistance = -1f;
#else
        notches = Input.mouseScrollDelta.y;
        if (Input.touchCount >= 2)
        {
            float d = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position);
            if (lastPinchDistance > 0f) notches += (d - lastPinchDistance) / (Screen.dpi > 0 ? Screen.dpi : 160f) * 4f;
            lastPinchDistance = d;
        }
        else lastPinchDistance = -1f;
#endif
        return notches;
    }

    /// <summary>Cursor position as -1..1 from the screen center, with dead zone and response curve applied.</summary>
    Vector2 ReadPointer(out bool hasPointer)
    {
        Vector2 screen;
        hasPointer = false;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            screen = Touchscreen.current.primaryTouch.position.ReadValue();
            hasPointer = true;
        }
        else if (Mouse.current != null)
        {
            screen = Mouse.current.position.ReadValue();
            hasPointer = true;
        }
        else screen = new Vector2(Screen.width, Screen.height) * 0.5f;
#else
        if (Input.touchCount > 0)
        {
            screen = Input.GetTouch(0).position;
            hasPointer = true;
        }
        else if (Input.mousePresent)
        {
            screen = Input.mousePosition;
            hasPointer = true;
        }
        else screen = new Vector2(Screen.width, Screen.height) * 0.5f;
#endif

        Vector2 n = new Vector2(
            Mathf.Clamp((screen.x / Screen.width) * 2f - 1f, -1f, 1f),
            Mathf.Clamp((screen.y / Screen.height) * 2f - 1f, -1f, 1f));

        if (!hasPointer) return Vector2.zero;

        n.x = Shape(n.x); n.y = Shape(n.y);
        if (invertX) n.x = -n.x;
        if (invertY) n.y = -n.y;
        return n;
    }

    float Shape(float v)
    {
        float a = Mathf.Abs(v);
        if (a <= deadZone) return 0f;
        a = (a - deadZone) / (1f - deadZone);           // remap past the dead zone to 0..1
        a = Mathf.Pow(a, responseCurve);
        return Mathf.Sign(v) * a;
    }

    void OnDrawGizmosSelected()
    {
        Vector3 f = Application.isPlaying ? Focus : (focusPoint != null ? focusPoint.position : transform.position + transform.forward * focusDistance);
        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(f, 0.25f);
        Gizmos.DrawLine(transform.position, f);
    }
}
