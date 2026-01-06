using UnityEngine;

/// <summary>
/// Otter movement system (Top-down, Y-up):
/// - Arrow direction is computed by projecting mouse ray onto a horizontal plane (true "point to mouse").
/// - Arrow length is based on screen-space distance (stable "joystick feel") mapped to world units.
/// Zones:
/// 1) <= inner radius: rotate only
/// 2) between rings: swim speed mapped by length
/// 3) >= outer radius: sprint with acceleration ramp
/// </summary>
public class MovementController : MonoBehaviour
{
    [Header("Camera / Input")]
    [SerializeField] private Camera inputCamera; // Assign explicitly in Inspector for stability.

    [Header("Ring Thresholds (World Units)")]
    [SerializeField] private float innerRingRadius = 1.0f;
    [SerializeField] private float outerRingRadius = 3.0f;

    [Header("Screen Distance -> World Distance")]
    [Tooltip("World units per pixel of screen-distance (tune this to match your ring sizes).")]
    [SerializeField] private float pixelsToWorld = 0.01f;

    [Header("Speed")]
    [SerializeField] private float maxSwimSpeed = 5f;          // Zone2 max
    [SerializeField] private float maxSprintSpeed = 8f;        // Zone3 max
    [SerializeField] private float sprintAcceleration = 12f;   // Speed ramp rate
    [SerializeField] private float rotationLerp = 12f;         // Rotation slerp factor

    [Header("Projection Plane")]
    [Tooltip("If true, project mouse ray to a fixed Y plane. If false, use otter's current Y.")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f; // water surface Y, usually 0

    [Header("Debug")]
    [SerializeField] private bool debugDraw = false;

    private DualRingUIController uiController;

    private bool isDragging;
    private Vector3 moveDirection;        // world XZ direction
    private float dragDistanceWorld;      // world distance used for zones
    private float currentSpeed;

    void Start()
    {
        if (inputCamera == null) inputCamera = Camera.main;
        uiController = FindObjectOfType<DualRingUIController>();

        if (uiController == null)
            Debug.LogWarning("DualRingUIController not found. UI arrow/rings will not update.");
    }

    void Update()
    {
        HandleInput();
        ApplyMovement();

        if (debugDraw)
            DebugDraw();
    }

    private void HandleInput()
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

        // ---------- 1) Direction: true pointing via ray -> plane ----------
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

        Vector3 toMouse = mouseWorld - transform.position;
        toMouse.y = 0f;

        if (toMouse.sqrMagnitude < 0.0001f)
        {
            moveDirection = Vector3.zero;
            dragDistanceWorld = 0f;
            uiController?.UpdateArrow(transform.position, Vector3.zero, 0f);
            return;
        }

        moveDirection = toMouse.normalized;

        // ---------- 2) Length: stable screen distance -> world units ----------
        Vector2 otterScreen = inputCamera.WorldToScreenPoint(transform.position);
        Vector2 mouseScreen = Input.mousePosition;
        float pixels = (mouseScreen - otterScreen).magnitude;

        dragDistanceWorld = pixels * pixelsToWorld;

        // Update UI arrow (center is otter)
        uiController?.UpdateArrow(transform.position, moveDirection, dragDistanceWorld);
    }

    private void ApplyMovement()
    {
        if (!isDragging)
            return;

        if (moveDirection.sqrMagnitude < 0.0001f)
            return;

        // Always rotate toward direction
        Quaternion targetRot = Quaternion.LookRotation(moveDirection, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationLerp * Time.deltaTime);

        // Zone 1: rotate only
        if (dragDistanceWorld <= innerRingRadius)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, sprintAcceleration * Time.deltaTime);
            return;
        }

        // Zone 2: swim (speed mapped by length)
        if (dragDistanceWorld < outerRingRadius)
        {
            float t = Mathf.InverseLerp(innerRingRadius, outerRingRadius, dragDistanceWorld);
            float targetSpeed = maxSwimSpeed * t;

            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, sprintAcceleration * Time.deltaTime);
            transform.position += moveDirection * currentSpeed * Time.deltaTime;
            return;
        }

        // Zone 3: sprint (ramp up)
        currentSpeed = Mathf.MoveTowards(currentSpeed, maxSprintSpeed, sprintAcceleration * Time.deltaTime);
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
