using UnityEngine;

/// <summary>
/// Top-down orthographic follow camera for RB movement.
///
/// Goal (strict centering):
/// - Otter stays at screen center.
/// - Dual-ring UI stays at screen center.
/// Therefore: camera desired position is ALWAYS target position + offset (no look-ahead, no drift).
///
/// Notes:
/// - Best with target Rigidbody Interpolation = Interpolate.
/// - This script does not rotate the camera.
/// </summary>
[DefaultExecutionOrder(500)]
[RequireComponent(typeof(Camera))]
public class TopDownCameraControllerRb : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Rigidbody targetRb; // optional, auto-found

    [Header("Follow")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);

    [Tooltip("Camera smoothing time (seconds). Larger = smoother, smaller = snappier.")]
    [SerializeField] private float smoothTime = 0.10f;

    [Tooltip("Optional max camera speed (units/sec). 0 = infinite.")]
    [SerializeField] private float maxSpeed = 0f;

    [Header("Jitter Suppression (optional)")]
    [Tooltip("Dead zone radius around current camera pivot. If target stays inside, camera won't move (reduces tiny jitter).")]
    [SerializeField] private float deadZoneRadius = 0.00f;

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;
    [SerializeField] private bool forceOrthoSize = false;
    [SerializeField] private float orthographicSize = 5f;

    private Camera cam;
    private Vector3 camVel; // SmoothDamp internal

    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (forceOrthographic) cam.orthographic = true;
        if (forceOrthoSize) cam.orthographicSize = orthographicSize;

        if (target != null && targetRb == null)
            targetRb = target.GetComponent<Rigidbody>();

        if (target != null)
        {
            Vector3 start = GetTargetPos() + offset;
            transform.position = start;
            camVel = Vector3.zero;
        }
    }

    private Vector3 GetTargetPos()
    {
        // With Rigidbody interpolation, target.position in LateUpdate is already render-interpolated.
        return target != null ? target.position : Vector3.zero;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;

        Vector3 targetPos = GetTargetPos();
        Vector3 desired = targetPos + offset;

        // Optional dead zone to suppress tiny jitter:
        if (deadZoneRadius > 0f)
        {
            Vector3 toDesired = desired - transform.position;
            if (toDesired.magnitude < deadZoneRadius)
                desired = transform.position;
        }

        float st = Mathf.Max(0.0001f, smoothTime);

        if (maxSpeed > 0f)
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref camVel, st, maxSpeed, dt);
        else
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref camVel, st, Mathf.Infinity, dt);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        smoothTime = Mathf.Max(0.0001f, smoothTime);
        maxSpeed = Mathf.Max(0f, maxSpeed);
        deadZoneRadius = Mathf.Max(0f, deadZoneRadius);
        orthographicSize = Mathf.Max(0.01f, orthographicSize);
    }
#endif
}
