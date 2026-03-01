using UnityEngine;

/// <summary>
/// Smooth follow camera. Tracks the target from the authored offset.
/// No opinions on where the camera must be — scene position is initial state.
/// </summary>
[DefaultExecutionOrder(500)]
[RequireComponent(typeof(Camera))]
public class TopDownCameraControllerRb : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private Rigidbody targetRb;
    [SerializeField] private CrestMovementControllerRB movement;
    [SerializeField] private MoveStateController stateController;

    [Header("Follow dynamics")]
    [Tooltip("0 = auto-derived from movement speeds. >0 = manual sharpness (1/sec).")]
    [SerializeField] private float manualFollowSharpness = 0f;
    [SerializeField] private float maxSpeed = 0f;

    [Header("Jitter suppression")]
    [SerializeField] private float deadZoneWorld = 0.03f;

    [Header("Sprint zoom")]
    [SerializeField] private float sprintOrthoSize = 1.5f;
    [SerializeField] private float zoomSmoothTime = 0.20f;

    private Camera cam;
    private Vector3 offset;     // derived from authored scene positions
    private float lockedY;      // camera stays at initial height (no wave bobbing)
    private Vector3 followVel;
    private float baseOrthoSize;
    private float orthoVel;

    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (target != null && targetRb == null)
            targetRb = target.GetComponent<Rigidbody>();
        if (target != null && movement == null)
            movement = target.GetComponent<CrestMovementControllerRB>();
        if (target != null && stateController == null)
            stateController = target.GetComponent<MoveStateController>();

        // Offset derived from authored positions — camera stays exactly where you put it.
        if (target != null)
            offset = transform.position - target.position;

        lockedY = transform.position.y;

        if (cam.orthographic)
            baseOrthoSize = Mathf.Max(0.01f, cam.orthographicSize);

        followVel = Vector3.zero;
    }

    // ---------------- Helpers ----------------

    private float GetSpeedXZ()
    {
        if (movement != null) return movement.GetSpeed();
        if (targetRb == null) return 0f;
        Vector3 v = targetRb.linearVelocity; v.y = 0f;
        return v.magnitude;
    }

    private float ComputeAutoSharpness(float speed)
    {
        float swim = (movement != null) ? Mathf.Max(0.01f, movement.GetSwimSpeed()) : 2.4f;
        float sprint = (movement != null) ? Mathf.Max(swim, movement.GetSprintSpeed()) : 4.2f;

        float t = Mathf.Clamp01(speed / swim);
        float u = Mathf.InverseLerp(swim, sprint, speed);

        float baseS = Mathf.Lerp(7f, 10f, t);
        return Mathf.Lerp(baseS, 13f, Mathf.Clamp01(u));
    }

    private bool IsSprinting()
    {
        return stateController != null && stateController.CurrentState == MoveStateController.MoveState.Sprint;
    }

    // ---------------- Main loop ----------------

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;

        // -------- 1) Smooth follow (XZ only, Y locked) --------
        Vector3 desired = target.position + offset;
        desired.y = lockedY;

        // Dead zone on XZ
        if (deadZoneWorld > 1e-6f)
        {
            Vector3 delta = desired - transform.position;
            delta.y = 0f;
            if (delta.magnitude < deadZoneWorld)
                desired = new Vector3(transform.position.x, desired.y, transform.position.z);
        }

        float speed = GetSpeedXZ();
        float sharp = (manualFollowSharpness > 1e-4f)
            ? manualFollowSharpness
            : ComputeAutoSharpness(speed);

        float smoothTime = Mathf.Max(0.0001f, 1f / Mathf.Max(0.01f, sharp));

        transform.position = (maxSpeed > 0f)
            ? Vector3.SmoothDamp(transform.position, desired, ref followVel, smoothTime, maxSpeed, dt)
            : Vector3.SmoothDamp(transform.position, desired, ref followVel, smoothTime, Mathf.Infinity, dt);

        // -------- 2) Sprint zoom --------
        if (cam != null && cam.orthographic)
        {
            float targetOrtho = IsSprinting() ? Mathf.Max(baseOrthoSize, sprintOrthoSize) : baseOrthoSize;
            cam.orthographicSize = Mathf.SmoothDamp(
                cam.orthographicSize, targetOrtho,
                ref orthoVel, Mathf.Max(0.0001f, zoomSmoothTime),
                Mathf.Infinity, dt);
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        deadZoneWorld = Mathf.Max(0f, deadZoneWorld);
        manualFollowSharpness = Mathf.Max(0f, manualFollowSharpness);
        maxSpeed = Mathf.Max(0f, maxSpeed);
        sprintOrthoSize = Mathf.Max(0.01f, sprintOrthoSize);
        zoomSmoothTime = Mathf.Max(0.0001f, zoomSmoothTime);
    }
#endif
}
