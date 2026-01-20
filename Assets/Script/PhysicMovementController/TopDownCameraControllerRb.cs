using UnityEngine;

/// <summary>
/// Top-down orthographic follow camera optimized for impulse-driven (RB) movement.
///
/// Design goals:
/// - Dual-ring UI stays at camera/screen center.
/// - The otter may drift from the UI center up to a configurable limit (in pixels),
///   ONLY along the arrow/intent direction.
/// - The camera uses speed-based look-ahead (more speed => more lead), then smooths to it.
/// - Camera smooth time adapts with otter speed (slow=steadier, fast=snappier).
///
/// Notes:
/// - Works best if the target Rigidbody Interpolation = Interpolate.
/// - This script does not rotate the camera; it only follows in world space.
/// </summary>
[DefaultExecutionOrder(500)]
[RequireComponent(typeof(Camera))]
public class TopDownCameraControllerRb : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Rigidbody targetRb;                // optional, auto-found
    [SerializeField] private MovementControllerRB movement;     // optional, auto-found (for look-ahead + speed)

    [Header("Follow")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);

    [Tooltip("Pivot smoothing (seconds). Larger = smoother (less jitter), smaller = snappier.")]
    [SerializeField] private float pivotSmoothTime = 0.18f;

    [Header("Speed-adaptive Camera Smooth")]
    [Tooltip("Swim/slow smoothing time (bigger = steadier).")]
    [SerializeField] private float smoothTimeSwim = 0.14f;

    [Tooltip("Sprint/fast smoothing time (smaller = snappier).")]
    [SerializeField] private float smoothTimeSprint = 0.07f;

    [Tooltip("Optional max camera speed (units/sec). 0 = infinite.")]
    [SerializeField] private float maxSpeed = 0f;

    [Header("Jitter Suppression")]
    [Tooltip("Dead zone radius around current pivot. If target stays inside, pivot won't move (reduces tiny jitter).")]
    [SerializeField] private float deadZoneRadius = 0.06f;

    [Tooltip("Max pivot movement per second (units/sec). 0 disables. Helps clamp sudden impulses.")]
    [SerializeField] private float pivotMaxSpeed = 0f;

    [Header("Look-ahead (Speed-based, Intent Direction)")]
    [Tooltip("Enable speed-based look-ahead (camera leads the otter).")]
    [SerializeField] private bool enableLookAhead = true;

    [Tooltip("Max look-ahead distance in WORLD units (before pixel clamp).")]
    [SerializeField] private float maxLookAheadWorld = 1.0f;

    [Tooltip("Speed at which look-ahead reaches maxLookAheadWorld.")]
    [SerializeField] private float speedForMaxLookAhead = 4.0f;

    [Tooltip("How quickly look-ahead responds (bigger = faster).")]
    [SerializeField] private float lookAheadLerp = 12f;

    [Header("Look-ahead Clamp (Keep Otter Near UI Center)")]
    [Tooltip("Max allowed otter screen offset in PIXELS. Typically <= inner ring radius.")]
    [SerializeField] private float maxOffsetPx = 210f; // for inner=300, start at 180~210

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;
    [SerializeField] private bool forceOrthoSize = false;
    [SerializeField] private float orthographicSize = 5f;

    private Camera cam;

    // Smoothed pivot
    private Vector3 pivotPos;
    private Vector3 pivotVel; // SmoothDamp internal

    // Camera smooth
    private Vector3 camVel;   // SmoothDamp internal

    // Look-ahead smooth
    private Vector3 lookAheadCurrent;

    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (forceOrthographic) cam.orthographic = true;
        if (forceOrthoSize) cam.orthographicSize = orthographicSize;

        if (target != null && targetRb == null) targetRb = target.GetComponent<Rigidbody>();
        if (target != null && movement == null) movement = target.GetComponent<MovementControllerRB>();

        if (target != null)
        {
            Vector3 startPos = GetTargetPos();
            pivotPos = startPos;
            transform.position = pivotPos + offset;

            pivotVel = Vector3.zero;
            camVel = Vector3.zero;
            lookAheadCurrent = Vector3.zero;
        }
    }

    private Vector3 GetTargetPos()
    {
        // With Rigidbody interpolation, target.position in LateUpdate is already render-interpolated.
        return target != null ? target.position : Vector3.zero;
    }

    private float UnitsPerPixelOrFallback()
    {
        if (cam != null && cam.orthographic)
            return (2f * cam.orthographicSize) / Mathf.Max(1, Screen.height);
        return 0.01f;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;
        Vector3 rawTarget = GetTargetPos();

        // --- Pivot filter (removes high-frequency jitter) ---
        Vector3 toTarget = rawTarget - pivotPos;

        // Dead zone: ignore tiny oscillations around pivot
        if (deadZoneRadius > 0f && toTarget.magnitude < deadZoneRadius)
            rawTarget = pivotPos;

        // Smooth pivot toward (possibly dead-zoned) target
        float pivotSmooth = Mathf.Max(0.0001f, pivotSmoothTime);
        if (pivotMaxSpeed > 0f)
            pivotPos = Vector3.SmoothDamp(pivotPos, rawTarget, ref pivotVel, pivotSmooth, pivotMaxSpeed, dt);
        else
            pivotPos = Vector3.SmoothDamp(pivotPos, rawTarget, ref pivotVel, pivotSmooth, Mathf.Infinity, dt);

        // --- Look-ahead: size from SPEED, direction from INTENT (arrow) ---
        Vector3 desiredLA = Vector3.zero;

        if (enableLookAhead && movement != null && maxLookAheadWorld > 0f)
        {
            // Only allow offset when user has intent (dragging), to avoid drifting UI when idle.
            if (movement.IsDragging())
            {
                Vector3 dir = movement.GetWorldDir();
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-6f)
                {
                    dir.Normalize();

                    float speed = movement.GetSpeed();
                    float t = (speedForMaxLookAhead <= 0.01f) ? 1f : Mathf.Clamp01(speed / speedForMaxLookAhead);

                    desiredLA = dir * (maxLookAheadWorld * t);

                    // Clamp by maxOffsetPx (screen-space budget) -> world-space
                    float maxOffsetWorld = Mathf.Max(0f, maxOffsetPx) * UnitsPerPixelOrFallback();
                    desiredLA = Vector3.ClampMagnitude(desiredLA, maxOffsetWorld);
                }
            }
        }

        float kLA = 1f - Mathf.Exp(-Mathf.Max(0.01f, lookAheadLerp) * dt);
        lookAheadCurrent = Vector3.Lerp(lookAheadCurrent, desiredLA, kLA);

        Vector3 desiredCamPos = pivotPos + offset + lookAheadCurrent;

        // --- Camera smoothing adapts with speed (slow=steady, fast=snappy) ---
        float camSmooth = smoothTimeSwim;
        if (movement != null)
        {
            float sprint = Mathf.Max(0.01f, movement.GetSprintSpeed());
            float speed01 = Mathf.Clamp01(movement.GetSpeed() / sprint);
            camSmooth = Mathf.Lerp(smoothTimeSwim, smoothTimeSprint, speed01);
        }
        camSmooth = Mathf.Max(0.0001f, camSmooth);

        if (maxSpeed > 0f)
            transform.position = Vector3.SmoothDamp(transform.position, desiredCamPos, ref camVel, camSmooth, maxSpeed, dt);
        else
            transform.position = Vector3.SmoothDamp(transform.position, desiredCamPos, ref camVel, camSmooth, Mathf.Infinity, dt);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        pivotSmoothTime = Mathf.Max(0f, pivotSmoothTime);
        smoothTimeSwim = Mathf.Max(0.0001f, smoothTimeSwim);
        smoothTimeSprint = Mathf.Max(0.0001f, smoothTimeSprint);
        maxLookAheadWorld = Mathf.Max(0f, maxLookAheadWorld);
        speedForMaxLookAhead = Mathf.Max(0.01f, speedForMaxLookAhead);
        lookAheadLerp = Mathf.Max(0f, lookAheadLerp);
        maxOffsetPx = Mathf.Max(0f, maxOffsetPx);
    }
#endif
}
