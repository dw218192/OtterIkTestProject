using UnityEngine;

/// <summary>
/// Final (jitter-fixed) Movement Controller for top-down orthographic camera setups.
///
/// Key fixes:
/// 1) Runs AFTER camera follow using DefaultExecutionOrder.
/// 2) Uses LateUpdate so ray->plane projection is consistent with final camera transform.
/// 3) Arrow length is WORLD distance from center to mouse projection on XZ plane (camera-size independent).
/// 4) DualRingUIController clamps the VISUAL arrow length to outerRingRadius (Option A).
/// </summary>
[DefaultExecutionOrder(200)]
public class MovementController : MonoBehaviour
{
    [Header("Camera / Input")]
    [SerializeField] private Camera inputCamera; // Assign explicitly if possible for stability.

    [Header("Ring Thresholds (World Units)")]
    [SerializeField] private float innerRingRadius = 1.0f;
    [SerializeField] private float outerRingRadius = 3.0f;

    [Header("Speed")]
    [SerializeField] private float maxSwimSpeed = 5f;          // Zone2 max
    [SerializeField] private float maxSprintSpeed = 8f;        // Zone3 max
    [SerializeField] private float acceleration = 12f;         // Speed ramp rate
    [SerializeField] private float rotationLerp = 12f;         // Rotation slerp factor

    [Header("Projection Plane")]
    [Tooltip("If true, project mouse ray to a fixed Y plane (recommended for water surface).")]
    [SerializeField] private bool useFixedPlaneY = true;

    [Tooltip("Water surface Y, usually 0.")]
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Optional Input Smoothing (extra anti-jitter)")]
    [Tooltip("0 = no smoothing, higher = smoother but slightly more lag.")]
    [SerializeField] private float inputSmoothing = 0f;

    [Header("Debug")]
    [SerializeField] private bool debugDraw = false;

    private DualRingUIController uiController;

    private bool isDragging;
    private Vector3 moveDirection;     // world XZ direction
    private float dragDistanceWorld;   // world distance used for zones
    private float currentSpeed;

    // For smoothing
    private Vector3 smoothedDir;
    private float smoothedDist;

    private void Start()
    {
        if (inputCamera == null) inputCamera = Camera.main;
        uiController = FindObjectOfType<DualRingUIController>();

        if (uiController == null)
            Debug.LogWarning("DualRingUIController not found. UI arrow/rings will not update.");

        smoothedDir = Vector3.forward;
        smoothedDist = 0f;
    }

    /// <summary>
    /// LateUpdate to ensure camera follow (usually LateUpdate) has already updated the camera transform.
    /// DefaultExecutionOrder(200) further increases the chance we run after the camera script.
    /// </summary>
    private void LateUpdate()
    {
        HandleInputLate();
        ApplyMovementLate();

        if (debugDraw)
            DebugDraw();
    }

    private void HandleInputLate()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            currentSpeed = 0f;

            uiController?.SetCenter(transform.position);
        }

        if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
            currentSpeed = 0f;
            moveDirection = Vector3.zero;
            dragDistanceWorld = 0f;

            smoothedDist = 0f;
            // keep smoothedDir as-is

            uiController?.HideArrow();
            return;
        }

        if (!isDragging)
            return;

        if (inputCamera == null)
        {
            uiController?.HideArrow();
            return;
        }

        // 1) Ray -> plane (world mouse point)
        float planeY = useFixedPlaneY ? fixedPlaneY : transform.position.y;
        Plane plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
        Ray ray = inputCamera.ScreenPointToRay(Input.mousePosition);

        if (!plane.Raycast(ray, out float hit))
        {
            moveDirection = Vector3.zero;
            dragDistanceWorld = 0f;
            uiController?.HideArrow();
            return;
        }

        Vector3 mouseWorld = ray.GetPoint(hit);

        // 2) Direction + Length in WORLD (XZ only)
        Vector3 toMouse = mouseWorld - transform.position;
        toMouse.y = 0f;

        if (toMouse.sqrMagnitude < 0.0001f)
        {
            moveDirection = Vector3.zero;
            dragDistanceWorld = 0f;
            uiController?.UpdateArrow(transform.position, Vector3.zero, 0f);
            return;
        }

        Vector3 targetDir = toMouse.normalized;
        float targetDist = toMouse.magnitude;

        // Optional smoothing (helps if mouse is noisy or camera follow still introduces tiny variance)
        if (Application.isPlaying && inputSmoothing > 0f)
        {
            float k = 1f - Mathf.Exp(-inputSmoothing * Time.deltaTime);
            smoothedDir = Vector3.Slerp(smoothedDir, targetDir, k);
            smoothedDist = Mathf.Lerp(smoothedDist, targetDist, k);

            moveDirection = smoothedDir;
            dragDistanceWorld = smoothedDist;
        }
        else
        {
            moveDirection = targetDir;
            dragDistanceWorld = targetDist;

            smoothedDir = targetDir;
            smoothedDist = targetDist;
        }

        // UI arrow: Option A (UI clamps visually to outerRingRadius)
        uiController?.UpdateArrow(transform.position, moveDirection, dragDistanceWorld);
    }

    private void ApplyMovementLate()
    {
        if (!isDragging)
            return;

        if (moveDirection.sqrMagnitude < 0.0001f)
            return;

        // Rotate toward direction
        Quaternion targetRot = Quaternion.LookRotation(moveDirection, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationLerp * Time.deltaTime);

        // Zone 1: rotate only
        if (dragDistanceWorld <= innerRingRadius)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, acceleration * Time.deltaTime);
            return;
        }

        // Zone 2: swim (speed mapped by distance)
        if (dragDistanceWorld < outerRingRadius)
        {
            float t = Mathf.InverseLerp(innerRingRadius, outerRingRadius, dragDistanceWorld);
            float targetSpeed = maxSwimSpeed * t;

            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, acceleration * Time.deltaTime);
            transform.position += moveDirection * currentSpeed * Time.deltaTime;
            return;
        }

        // Zone 3: sprint
        currentSpeed = Mathf.MoveTowards(currentSpeed, maxSprintSpeed, acceleration * Time.deltaTime);
        transform.position += moveDirection * currentSpeed * Time.deltaTime;
    }

    private void DebugDraw()
    {
        Debug.DrawLine(transform.position, transform.position + moveDirection * 2f, Color.green);
    }

    // ===== Exposed for UI/other systems =====
    public float GetInnerRingRadius() => innerRingRadius;
    public float GetOuterRingRadius() => outerRingRadius;
    public bool IsDragging() => isDragging;
    public float GetDragDistanceWorld() => dragDistanceWorld;
    public Vector3 GetMoveDirection() => moveDirection;
}
