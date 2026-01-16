using UnityEngine;

/// <summary>
/// Stable top-down orthographic follow camera.
/// Fixes jitter by:
/// 1) Running AFTER MovementControllerRB (execution order)
/// 2) Using SmoothDamp instead of Lerp
/// 3) Optional velocity look-ahead (default off)
/// </summary>
[DefaultExecutionOrder(500)] // run after MovementControllerRB (which is 200)
[RequireComponent(typeof(Camera))]
public class TopDownCameraControllerRb : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private MovementControllerRB movement;

    [Header("Follow")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);

    [Tooltip("Smaller = snappier, larger = smoother. Start with 0.08~0.15")]
    [SerializeField] private float smoothTime = 0.10f;

    [Tooltip("Optional max speed for SmoothDamp. 0 = infinite")]
    [SerializeField] private float maxSpeed = 0f;

    [Header("Optional Look-ahead (Velocity)")]
    [SerializeField] private bool enableVelocityLookAhead = false;
    [SerializeField] private float velLookAheadDistance = 0.6f;

    [Tooltip("Extra smoothing for look-ahead vector (higher = snappier)")]
    [SerializeField] private float velLookAheadLerp = 12f;

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;
    [SerializeField] private bool forceOrthoSize = false;
    [SerializeField] private float orthographicSize = 5f;

    private Camera cam;
    private Vector3 followVelocity;          // SmoothDamp internal velocity
    private Vector3 velLookAheadCurrent;     // smoothed look-ahead

    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (forceOrthographic)
            cam.orthographic = true;

        if (forceOrthoSize)
            cam.orthographicSize = orthographicSize;

        if (movement == null && target != null)
            movement = target.GetComponent<MovementControllerRB>();

        // Initialize camera position to avoid a "first-frame snap"
        if (target != null)
        {
            transform.position = target.position + offset;
            followVelocity = Vector3.zero;
            velLookAheadCurrent = Vector3.zero;
        }
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;

        // Velocity look-ahead (optional, default off for maximum stability)
        Vector3 desiredVelLA = Vector3.zero;
        if (enableVelocityLookAhead && movement != null && velLookAheadDistance > 0f)
        {
            Vector3 v = movement.GetVelocity();
            v.y = 0f;
            if (v.sqrMagnitude > 0.01f)
                desiredVelLA = v.normalized * velLookAheadDistance;
        }

        float kVel = 1f - Mathf.Exp(-Mathf.Max(0.01f, velLookAheadLerp) * dt);
        velLookAheadCurrent = Vector3.Lerp(velLookAheadCurrent, desiredVelLA, kVel);

        Vector3 desiredPos = target.position + offset + velLookAheadCurrent;

        if (maxSpeed > 0f)
            transform.position = Vector3.SmoothDamp(transform.position, desiredPos, ref followVelocity, smoothTime, maxSpeed, dt);
        else
            transform.position = Vector3.SmoothDamp(transform.position, desiredPos, ref followVelocity, smoothTime, Mathf.Infinity, dt);
    }
}
