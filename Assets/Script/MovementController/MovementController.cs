// MovementController.cs
using UnityEngine;

[DefaultExecutionOrder(200)]
public class MovementController : MonoBehaviour
{
    public enum MoveZone { None, Aim, Swim, Sprint }

    [Header("References")]
    [SerializeField] private Camera inputCamera;
    [SerializeField] private DualRingUIController ui;

    [Header("Plane (Water Surface)")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Speeds (forward only)")]
    [SerializeField] private float swimSpeed = 2.4f;
    [SerializeField] private float sprintSpeed = 4.2f;
    [SerializeField] private float speedAcceleration = 10f; // how quickly currentSpeed reaches targetSpeed

    [Header("Turning (RootMotionSection style)")]
    [Tooltip("At 0 speed, max turn speed (deg/s).")]
    [SerializeField] private float turnSpeedAtStop = 540f;

    [Tooltip("At max (sprint) speed, max turn speed (deg/s). Should be LOWER than turnSpeedAtStop.")]
    [SerializeField] private float turnSpeedAtMaxSpeed = 180f;

    [Tooltip("At 0 speed, turn acceleration (deg/s^2).")]
    [SerializeField] private float turnAccelAtStop = 4500f;

    [Tooltip("At max (sprint) speed, turn acceleration (deg/s^2). Should be LOWER than turnAccelAtStop.")]
    [SerializeField] private float turnAccelAtMaxSpeed = 1200f;

    [Tooltip("Angle (deg) below which turnCurve is 0.")]
    [SerializeField] private float turnCurveStartAngle = 10f;

    [Tooltip("Angle (deg) above which turnCurve is 1 (full turn speed).")]
    [SerializeField] private float turnCurveFullAngle = 60f;

    [Tooltip("If abs(angle) exceeds this, we stop moving and prioritize turning. Set <=0 to disable.")]
    [SerializeField] private float moveWhenFacingAngle = 95f;

    [Header("Turn slows movement")]
    [Tooltip("Minimum multiplier applied to forward speed when turning hard.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float turnSlowMin = 0.45f;

    [Tooltip("How strongly angular speed reduces forward speed (0 disables).")]
    [SerializeField] private float turnSlowByOmega = 1.0f;

    [Header("Input smoothing (screen space)")]
    [SerializeField] private float inputSmoothing = 18f;

    [Header("Carrot / Guide")]
    [Tooltip("Distance ahead (world units) for Aim zone guide.")]
    [SerializeField] private float carrotRadiusAim = 1.5f;

    [Tooltip("Distance ahead (world units) for Swim zone guide.")]
    [SerializeField] private float carrotRadiusSwim = 2.2f;

    [Tooltip("Distance ahead (world units) for Sprint zone guide.")]
    [SerializeField] private float carrotRadiusSprint = 3.0f;

    [Header("Debug")]
    [SerializeField] private bool drawDebug = true;

    // ===== runtime state =====
    private bool isDragging;
    private Vector2 dirScreen;        // raw screen-space direction (normalized)
    private float radiusPx;           // raw radius (px)
    private Vector2 dirScreenSm;      // smoothed
    private float radiusPxSm;         // smoothed

    private MoveZone zone = MoveZone.None;

    private float currentSpeed;
    private float currentOmegaDeg;    // signed angular velocity (deg/s)

    private Vector3 worldDir = Vector3.forward;  // desired direction on XZ
    private Vector3 velocity;                  // current forward velocity in world
    private Vector3 carrotWorld;               // guide point ahead in desired direction
    private Vector3 centerWorld;               // anchor projection on plane

    private void Reset()
    {
        inputCamera = Camera.main;
    }

    private void Awake()
    {
        if (inputCamera == null) inputCamera = Camera.main;
    }

    private void Update()
    {
        HandleInput();
        UpdateIntentAndCarrot();
        ApplyRootMotionTurnAndMove();
    }

    // ---------------- Input ----------------

    private void HandleInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;

            // snap smoothing on start (prevents jump)
            Vector2 rawDir = GetDirFromCenter(Input.mousePosition, out float rawR);
            dirScreen = rawDir;
            radiusPx = rawR;
            dirScreenSm = rawDir;
            radiusPxSm = rawR;

            // do not hard reset speed here; let acceleration handle continuity
        }
        else if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
            zone = MoveZone.None;
            // keep direction for idle head guide, but speed will decay to 0
        }

        if (!isDragging) return;

        Vector2 newDir = GetDirFromCenter(Input.mousePosition, out float newR);
        dirScreen = newDir;
        radiusPx = newR;

        // smooth in screen space
        float dt = Time.deltaTime;
        float k = inputSmoothing <= 0f ? 1f : (1f - Mathf.Exp(-inputSmoothing * dt));
        dirScreenSm = Vector2.Lerp(dirScreenSm, dirScreen, k);
        radiusPxSm = Mathf.Lerp(radiusPxSm, radiusPx, k);

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

        float clampedPx = Mathf.Clamp(radiusPxSm, 0f, outerPx);

        if (clampedPx <= innerPx) zone = MoveZone.Aim;
        else if (clampedPx < outerPx) zone = MoveZone.Swim;
        else zone = MoveZone.Sprint;

        //ui?.UpdateArrowFromScreen(dirScreenSm, clampedPx);
        ui?.SetArrowInput(dirScreenSm, clampedPx);
    }

    private Vector2 GetDirFromCenter(Vector3 mousePos, out float radius)
    {
        Vector2 center = (ui != null) ? ui.ScreenCenterPx : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 delta = (Vector2)mousePos - center;
        radius = delta.magnitude;
        return (radius > 1e-4f) ? (delta / radius) : Vector2.up;
    }

    // ---------------- Intent / carrot ----------------

    private void UpdateIntentAndCarrot()
    {
        // center on plane (for debug + carrot stability)
        centerWorld = GetCenterWorld();

        // update desired worldDir only when dragging (otherwise keep last)
        if (isDragging && inputCamera != null)
        {
            worldDir = ScreenDirToWorldXZ(dirScreenSm, inputCamera.transform);
            if (worldDir.sqrMagnitude < 1e-6f)
                worldDir = transform.forward;

            worldDir.y = 0f;
            worldDir = worldDir.sqrMagnitude > 1e-6f ? worldDir.normalized : Vector3.forward;
        }

        // choose carrot radius by zone
        float r = carrotRadiusAim;
        if (zone == MoveZone.Swim) r = carrotRadiusSwim;
        else if (zone == MoveZone.Sprint) r = carrotRadiusSprint;

        Vector3 anchor = transform.position;
        anchor.y = PlaneY;

        carrotWorld = anchor + worldDir * r;
        carrotWorld.y = PlaneY;
    }

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : transform.position.y;

    private Vector3 GetCenterWorld()
    {
        Vector3 p = transform.position;
        p.y = PlaneY;
        return p;
    }

    // Maps a screen-space direction (x = right, y = up) into world XZ using camera basis.
    // This is robust for your top-down orthographic camera.
    private static Vector3 ScreenDirToWorldXZ(Vector2 screenDir, Transform cam)
    {
        Vector3 right = cam.right;
        Vector3 up = cam.up;

        Vector3 w = right * screenDir.x + up * screenDir.y;

        // Project to XZ plane
        w.y = 0f;
        if (w.sqrMagnitude < 1e-6f)
        {
            // Fallback: camera-forward projected
            w = cam.forward;
            w.y = 0f;
        }

        return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
    }

    // ---------------- RootMotionSection-style turn + move ----------------

    private void ApplyRootMotionTurnAndMove()
    {
        float dt = Time.deltaTime;

        // Determine target speed from zone (forward-only; no reverse).
        float targetSpeed = 0f;
        if (isDragging)
        {
            if (zone == MoveZone.Swim) targetSpeed = swimSpeed;
            else if (zone == MoveZone.Sprint) targetSpeed = sprintSpeed;
            else targetSpeed = 0f; // Aim or None
        }

        // Smooth speed
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, speedAcceleration * dt);

        // Turning target direction: always toward "worldDir" (which is the same direction we use for carrot).
        Vector3 up = Vector3.up;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 desired = worldDir;
        desired.y = 0f;
        if (desired.sqrMagnitude < 1e-6f) desired = fwd;
        desired.Normalize();

        float ang = Vector3.SignedAngle(fwd, desired, up);
        float absAng = Mathf.Abs(ang);

        // Turn ability decreases with speed
        float maxSpeed = Mathf.Max(swimSpeed, sprintSpeed, 0.0001f);
        float speed01 = Mathf.Clamp01(currentSpeed / maxSpeed);

        float turnSpeed = Mathf.Lerp(turnSpeedAtStop, turnSpeedAtMaxSpeed, speed01);
        float turnAccel = Mathf.Lerp(turnAccelAtStop, turnAccelAtMaxSpeed, speed01);

        // Gecko-like curve from angle to target omega
        float curve = Mathf.InverseLerp(turnCurveStartAngle, turnCurveFullAngle, absAng);
        curve = Mathf.Clamp01(curve);

        float targetOmega = Mathf.Sign(ang) * (turnSpeed * curve);

        // Smooth omega
        currentOmegaDeg = Mathf.MoveTowards(currentOmegaDeg, targetOmega, turnAccel * dt);

        // Optionally stop moving if not facing enough
        bool blockMoveForFacing = (moveWhenFacingAngle > 0f) && (absAng > moveWhenFacingAngle);

        // Turn slows movement (using omega ratio)
        float omegaRatio = (turnSpeed > 1e-3f) ? Mathf.Clamp01(Mathf.Abs(currentOmegaDeg) / turnSpeed) : 0f;
        float slowByOmega = Mathf.Lerp(1f, turnSlowMin, omegaRatio * Mathf.Clamp01(turnSlowByOmega));

        float moveSpeed = (blockMoveForFacing ? 0f : currentSpeed) * slowByOmega;

        // Apply rotation (yaw only)
        if (Mathf.Abs(currentOmegaDeg) > 1e-4f)
        {
            transform.Rotate(up, currentOmegaDeg * dt, Space.World);
        }

        // Apply translation (forward only, on plane)
        Vector3 newForward = transform.forward;
        newForward.y = 0f;
        if (newForward.sqrMagnitude < 1e-6f) newForward = desired;
        newForward.Normalize();

        velocity = newForward * moveSpeed;

        if (moveSpeed > 1e-5f)
        {
            Vector3 pos = transform.position;
            pos += velocity * dt;
            pos.y = PlaneY;
            transform.position = pos;
        }
        else
        {
            // keep y on plane even when idle
            Vector3 pos = transform.position;
            pos.y = PlaneY;
            transform.position = pos;
        }

        // Keep carrot locked to input intent rather than body forward (so head can lead)
        // (carrotWorld already updated from worldDir in UpdateIntentAndCarrot)
    }

    private void OnDrawGizmos()
    {
        if (!drawDebug) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(GetCenterWorld(), 0.05f);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(GetCenterWorld(), carrotWorld);
        Gizmos.DrawSphere(carrotWorld, 0.06f);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(transform.position, transform.position + velocity);
    }

    // ===== Exposed for IK / camera =====
    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;
    public bool IsSprinting() => zone == MoveZone.Sprint;

    /// <summary>World-space guide point ahead of the character in input direction.</summary>
    public Vector3 GetCarrotWorld() => carrotWorld;

    /// <summary>Desired input direction on XZ (unit length when valid).</summary>
    public Vector3 GetWorldDir() => worldDir;

    /// <summary>Current world velocity (forward-only, may be zero).</summary>
    public Vector3 GetVelocity() => velocity;

    public float GetCurrentSpeed() => currentSpeed;

    /// <summary>Signed angular velocity (deg/s) applied to the body.</summary>
    public float GetAngularVelocityDeg() => currentOmegaDeg;
}
