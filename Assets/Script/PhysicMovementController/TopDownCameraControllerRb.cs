using UnityEngine;

/// <summary>
/// Optimized top-down orthographic follow camera (RB-friendly).
///
/// Design goals (your latest requirements):
/// 1) Minimal tuning: follow response auto-derived from CrestMovementControllerRB speeds.
/// 2) Composition guarantee: keep the otter inside DualRingUI inner ring via SCREEN-SPACE pixel clamp.
/// 3) Single "follow lock strength" knob:
///    - higher => tighter centering + snappier follow
///    - lower  => looser (allowed offset within ring) + softer follow
/// 4) Default ortho size stays 1. Sprint zooms to 1.5, then returns to 1 when leaving sprint.
/// </summary>
[DefaultExecutionOrder(500)]
[RequireComponent(typeof(Camera))]
public class TopDownCameraControllerRb : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Rigidbody targetRb; // optional
    [SerializeField] private CrestMovementControllerRB movement; // speeds / velocity helpers
    [SerializeField] private MoveStateController stateController; // sprint state

    [Header("DualRingUI (for screen-space constraint)")]
    [SerializeField] private DualRingUIControllerRB dualRingUI;

    [Header("Top-down offset (camera does not rotate)")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);

    [Header("Water plane (for screen->world projection)")]
    [Tooltip("World Y of water plane (usually 0). Used to convert screen clamp back to world correction.")]
    [SerializeField] private float planeY = 0f;

    [Header("Core Control: Follow Lock Strength")]
    [Tooltip("0 = loose (allow inner ring offset), 1 = very tight (nearly always centered).")]
    [Range(0f, 1f)]
    [SerializeField] private float followLockStrength = 0.75f;

    [Header("Jitter suppression")]
    [Tooltip("Dead zone radius in WORLD units (XZ). Helps remove idle micro-stutter.")]
    [SerializeField] private float deadZoneWorld = 0.03f;

    [Header("Follow dynamics (mostly auto)")]
    [Tooltip("0 = auto. If >0, manual sharpness (1/sec). Larger => snappier.")]
    [SerializeField] private float manualFollowSharpness = 0f;

    [Tooltip("Optional max camera speed (units/sec). 0 = infinite.")]
    [SerializeField] private float maxSpeed = 0f;

    [Header("Constraint (composition)")]
    [Tooltip("Keep a margin inside inner ring (0.9 keeps 10% padding).")]
    [Range(0.5f, 1f)]
    [SerializeField] private float innerRingMargin01 = 0.90f;

    [Tooltip("Smooth time (seconds) for the constraint correction. Smaller => stiffer constraint.")]
    [SerializeField] private float constraintSmoothTime = 0.06f;

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;

    [Tooltip("Base ortho size. Requirement says default is 1 and should not change.")]
    [SerializeField] private float baseOrthoSize = 1.0f;

    [Tooltip("If true, read camera.orthographicSize on Start as base (so inspector value wins).")]
    [SerializeField] private bool useCurrentOrthoAsBase = true;

    [Header("Sprint zoom")]
    [Tooltip("Ortho size while sprinting.")]
    [SerializeField] private float sprintOrthoSize = 1.5f;

    [Tooltip("Smooth time (seconds) for ortho size changes.")]
    [SerializeField] private float zoomSmoothTime = 0.20f;

    private Camera cam;

    // SmoothDamp internal velocities
    private Vector3 followVel;
    private Vector3 constraintVel;
    private float orthoVel;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (forceOrthographic) cam.orthographic = true;
    }

    private void Start()
    {
        if (target != null && targetRb == null)
            targetRb = target.GetComponent<Rigidbody>();

        if (target != null && movement == null)
            movement = target.GetComponent<CrestMovementControllerRB>();

        if (target != null && stateController == null)
            stateController = target.GetComponent<MoveStateController>();

        // Base ortho size: keep default = 1 (or inspector), do not change in normal states.
        if (cam.orthographic)
        {
            if (useCurrentOrthoAsBase)
                baseOrthoSize = Mathf.Max(0.01f, cam.orthographicSize);
            else
                cam.orthographicSize = Mathf.Max(0.01f, baseOrthoSize);
        }

        if (target != null)
        {
            transform.position = target.position + offset;
            followVel = Vector3.zero;
            constraintVel = Vector3.zero;
        }
    }

    // ---------------- Helpers ----------------

    private float GetSpeedXZ()
    {
        if (movement != null) return movement.GetSpeed(); // already XZ magnitude
        if (targetRb == null) return 0f;
        Vector3 v = targetRb.velocity;
        v.y = 0f;
        return v.magnitude;
    }

    /// <summary>
    /// Auto follow sharpness (1/sec), derived from movement speeds and current speed.
    /// Returns a reasonable baseline; then we multiply by lock strength to "lock harder" when desired.
    /// </summary>
    private float ComputeAutoSharpness(float speed)
    {
        float swim = (movement != null) ? Mathf.Max(0.01f, movement.GetSwimSpeed()) : 2.4f;
        float sprint = (movement != null) ? Mathf.Max(swim, movement.GetSprintSpeed()) : 4.2f;

        // Normalize in [0..1] around swim
        float t = Mathf.Clamp01(speed / swim);

        // Baseline ranges: idle -> swim -> sprint (feel free to tweak these 3 numbers)
        float idleSharp = 7f;
        float swimSharp = 10f;
        float sprintSharp = 13f;

        float u = Mathf.InverseLerp(swim, sprint, speed);
        float baseS = Mathf.Lerp(idleSharp, swimSharp, t);
        float s = Mathf.Lerp(baseS, sprintSharp, Mathf.Clamp01(u));
        return s;
    }

    private static float SharpnessToSmoothTime(float sharpness)
    {
        sharpness = Mathf.Max(0.01f, sharpness);
        return 1f / sharpness;
    }

    private bool IsSprinting()
    {
        return stateController != null && stateController.CurrentState == MoveStateController.MoveState.Sprint;
    }

    private Vector3 ScreenToWorldOnPlane(Vector2 screenPx)
    {
        Ray r = cam.ScreenPointToRay(new Vector3(screenPx.x, screenPx.y, 0f));

        // If ray is parallel to plane, just fall back (should not happen in typical top-down)
        float dy = r.direction.y;
        if (Mathf.Abs(dy) < 1e-6f)
        {
            Vector3 p = r.origin;
            p.y = planeY;
            return p;
        }

        float t = (planeY - r.origin.y) / dy;
        return r.origin + r.direction * t;
    }

    // ---------------- Main loop ----------------

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;

        // -------- 1) Smooth follow (hand-feel) --------
        Vector3 desired = target.position + offset;

        // Dead zone in XZ (kills micro jitter at idle)
        if (deadZoneWorld > 1e-6f)
        {
            Vector3 delta = desired - transform.position;
            delta.y = 0f;
            if (delta.magnitude < deadZoneWorld)
                desired = transform.position;
        }

        float speed = GetSpeedXZ();

        // Choose sharpness: manual override or auto-derived
        float sharp = (manualFollowSharpness > 1e-4f)
            ? manualFollowSharpness
            : ComputeAutoSharpness(speed);

        // Lock strength also increases follow stiffness
        // lock=0 => *0.9, lock=1 => *2.2 (tighter)
        float lockMul = Mathf.Lerp(0.9f, 2.2f, followLockStrength);
        sharp *= lockMul;

        float smoothTime = Mathf.Max(0.0001f, SharpnessToSmoothTime(sharp));

        if (maxSpeed > 0f)
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref followVel, smoothTime, maxSpeed, dt);
        else
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref followVel, smoothTime, Mathf.Infinity, dt);

        // -------- 2) Screen-space composition constraint (guarantee) --------
        // Keep target within inner ring (pixels), but tighter when lockStrength is higher.
        if (dualRingUI != null && cam != null)
        {
            Vector2 centerPx = dualRingUI.ScreenCenterPx;
            float innerPx = Mathf.Max(1f, dualRingUI.InnerRadiusPx);

            // Allowed radius in px:
            // lock=0 => allowed = innerPx * margin (loose)
            // lock=1 => allowed = 0 (strict center)
            float allowedPx = innerPx * innerRingMargin01 * (1f - followLockStrength);

            // Always ensure at least a tiny epsilon to avoid degenerate math,
            // but note: when lock=1 we truly want near-zero.
            allowedPx = Mathf.Max(0.001f, allowedPx);

            Vector2 sp = (Vector2)cam.WorldToScreenPoint(target.position);
            Vector2 d = sp - centerPx;
            float dist = d.magnitude;

            if (dist > allowedPx && dist > 1e-4f)
            {
                Vector2 spClamped = centerPx + d * (allowedPx / dist);

                // Convert both screen points to world points on the water plane.
                Vector3 wp = ScreenToWorldOnPlane(sp);
                Vector3 wpClamped = ScreenToWorldOnPlane(spClamped);

                // Moving camera by (wp - wpClamped) pushes the target back toward clamp.
                Vector3 correction = (wp - wpClamped);
                correction.y = 0f;

                // Soft constraint: SmoothDamp this correction to avoid snapping.
                Vector3 desiredCam = transform.position + correction;

                float cst = Mathf.Max(0.0001f, constraintSmoothTime);
                transform.position = Vector3.SmoothDamp(transform.position, desiredCam, ref constraintVel, cst, Mathf.Infinity, dt);
            }
        }

        // -------- 3) Sprint zoom (vision) --------
        if (cam != null && cam.orthographic)
        {
            float targetOrtho = IsSprinting() ? Mathf.Max(baseOrthoSize, sprintOrthoSize) : baseOrthoSize;

            cam.orthographicSize = Mathf.SmoothDamp(
                cam.orthographicSize,
                targetOrtho,
                ref orthoVel,
                Mathf.Max(0.0001f, zoomSmoothTime),
                Mathf.Infinity,
                dt
            );
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        deadZoneWorld = Mathf.Max(0f, deadZoneWorld);
        manualFollowSharpness = Mathf.Max(0f, manualFollowSharpness);
        maxSpeed = Mathf.Max(0f, maxSpeed);

        baseOrthoSize = Mathf.Max(0.01f, baseOrthoSize);
        sprintOrthoSize = Mathf.Max(baseOrthoSize, sprintOrthoSize);

        constraintSmoothTime = Mathf.Max(0.0001f, constraintSmoothTime);
        zoomSmoothTime = Mathf.Max(0.0001f, zoomSmoothTime);

        innerRingMargin01 = Mathf.Clamp(innerRingMargin01, 0.5f, 1f);
        followLockStrength = Mathf.Clamp01(followLockStrength);

        offset = new Vector3(offset.x, Mathf.Max(0.01f, offset.y), offset.z);
    }
#endif
}
