using UnityEngine;

/// <summary>
/// Rigidbody movement:
/// - Input -> target speed (speed-driven).
/// - Discrete impulse propulsion (kicks) to close the speed gap.
/// - Visual facing follows intent direction (intent-facing).
///
/// Add WaterDrag to the same Rigidbody for water resistance.
/// </summary>
[DefaultExecutionOrder(200)]
public class MovementControllerRB : MonoBehaviour
{
    public enum MoveZone { None, Aim, Swim, SprintRamp }

    [Header("References")]
    [SerializeField] private Camera inputCamera;
    [SerializeField] private DualRingUIControllerRB ui;
    [SerializeField] private Rigidbody rb;

    [Header("Plane (Water Surface)")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Target Speeds")]
    [Tooltip("Max target speed when the cursor reaches the OUTER ring.")]
    [SerializeField] private float swimSpeed = 2.4f;

    [Tooltip("Max target speed when the cursor is far outside the OUTER ring.")]
    [SerializeField] private float sprintSpeed = 4.2f;

    [Tooltip("How many pixels outside the OUTER ring are needed to reach sprintSpeed.")]
    [SerializeField] private float sprintRampExtraPx = 200f;

    [Header("Kick Tuning (base)")]
    [SerializeField] private float kickRateMaxSwim = 3.2f;
    [SerializeField] private float kickRateMaxSprintRamp = 5.0f;
    [SerializeField] private float impulseMaxSwim = 1.6f;
    [SerializeField] private float impulseMaxSprintRamp = 2.4f;
    [Tooltip("dv (m/s) required to reach max kick rate.")]
    [SerializeField] private float dvForMaxRate = 1.2f;
    [Tooltip("dv (m/s) required to reach max impulse.")]
    [SerializeField] private float dvForMaxImpulse = 1.0f;

    [Header("Fast Stop")]
    [Tooltip("Extra braking acceleration (m/s^2) applied opposite to current velocity when intent is 0.")]
    [SerializeField] private float idleBrakeAccel = 8.0f;
    [Tooltip("When speed falls below this, snap horizontal velocity to 0.")]
    [SerializeField] private float stopSpeedEpsilon = 0.08f;

    [Header("Facing")]
    [Tooltip("Rotate this transform toward intent direction (visual only). 0 disables.")]
    [SerializeField] private float faceSmoothing = 28f;

    [Header("Input smoothing (screen space)")]
    [SerializeField] private float inputSmoothing = 18f;

    [Header("Carrot / guide")]
    [SerializeField] private float carrotRadiusAim = 1.5f;
    [SerializeField] private float carrotRadiusSwim = 2.2f;
    [SerializeField] private float carrotRadiusSprint = 3.0f;

    [Header("Debug")]
    [SerializeField] private bool drawDebug = true;

    // Runtime input
    private bool isDragging;
    private Vector2 dirScreen;
    private float radiusPx;
    private Vector2 dirScreenSm;
    private float radiusPxSm;

    private MoveZone zone = MoveZone.None;

    // Intent/telemetry
    private Vector3 worldDir = Vector3.forward; // unit on XZ
    private float targetSpeed;
    private float dvdt;
    private float prevVproj;
    private float currentOmegaDeg;
    private float prevYaw;

    private Vector3 carrotWorld;
    private Vector3 centerWorld;

    // Kick scheduler
    private float kickCooldown;

    // Runtime overrides (set by a state controller)
    private bool propulsionOverrideEnabled;
    private float overrideKickRateMax;
    private float overrideImpulseMax;
    private float overrideIdleBrake;
    private bool overrideDisableKicks;

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : (rb != null ? rb.position.y : transform.position.y);

    private void Reset()
    {
        inputCamera = Camera.main;
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (inputCamera == null) inputCamera = Camera.main;
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.useGravity = false;
            rb.constraints |= RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        prevYaw = transform.eulerAngles.y;
    }

    private void Update()
    {
        HandleInput();
        UpdateIntentAndCarrot();
        // Facing is applied in FixedUpdate via Rigidbody rotation.
    }

    private void FixedUpdate()
    {
        UpdateFacingRigidbody();
        ApplyPulsePropulsion();
        ApplyIdleBrakeAndPlaneLock();
        UpdateTelemetry();
    }

    // ---------------- Input ----------------

    private void HandleInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;

            Vector2 rawDir = GetDirFromCenter(Input.mousePosition, out float rawR);
            dirScreen = rawDir;
            radiusPx = rawR;
            dirScreenSm = rawDir;
            radiusPxSm = rawR;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
            zone = MoveZone.None;
            targetSpeed = 0f;
        }

        if (!isDragging) return;

        Vector2 newDir = GetDirFromCenter(Input.mousePosition, out float newR);
        dirScreen = newDir;
        radiusPx = newR;

        float dt = Time.deltaTime;
        float k = inputSmoothing <= 0f ? 1f : (1f - Mathf.Exp(-inputSmoothing * dt));
        dirScreenSm = Vector2.Lerp(dirScreenSm, dirScreen, k);
        radiusPxSm = Mathf.Lerp(radiusPxSm, radiusPx, k);

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

        float clampedToOuter = Mathf.Clamp(radiusPxSm, 0f, outerPx);
        if (clampedToOuter <= innerPx) zone = MoveZone.Aim;
        else if (clampedToOuter < outerPx) zone = MoveZone.Swim;
        else zone = MoveZone.SprintRamp;

        ui?.SetArrowInput(dirScreenSm, Mathf.Min(radiusPxSm, outerPx));
    }

    private Vector2 GetDirFromCenter(Vector3 mousePos, out float radius)
    {
        Vector2 center = (ui != null) ? ui.ScreenCenterPx : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 delta = (Vector2)mousePos - center;
        radius = delta.magnitude;
        return (radius > 1e-4f) ? (delta / radius) : Vector2.up;
    }

    private static Vector3 ScreenDirToWorldXZ(Vector2 screenDir, Transform cam)
    {
        Vector3 w = cam.right * screenDir.x + cam.up * screenDir.y;
        w.y = 0f;
        if (w.sqrMagnitude < 1e-6f)
        {
            w = cam.forward;
            w.y = 0f;
        }
        return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
    }

    // ---------------- Intent / carrot ----------------

    private void UpdateIntentAndCarrot()
    {
        centerWorld = GetCenterWorld();

        if (isDragging && inputCamera != null)
        {
            worldDir = ScreenDirToWorldXZ(dirScreenSm, inputCamera.transform);
            if (worldDir.sqrMagnitude < 1e-6f) worldDir = transform.forward;
            worldDir.y = 0f;
            worldDir = worldDir.sqrMagnitude > 1e-6f ? worldDir.normalized : Vector3.forward;
        }

        // targetSpeed mapping
        targetSpeed = 0f;
        if (isDragging)
        {
            float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
            float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

            float r = radiusPxSm;
            if (r <= innerPx)
            {
                targetSpeed = 0f;
            }
            else if (r < outerPx)
            {
                float t = Mathf.InverseLerp(innerPx, outerPx, r);
                targetSpeed = Mathf.Lerp(0f, swimSpeed, t);
            }
            else
            {
                float t = (sprintRampExtraPx <= 1e-3f) ? 1f : Mathf.Clamp01((r - outerPx) / sprintRampExtraPx);
                targetSpeed = Mathf.Lerp(swimSpeed, sprintSpeed, t);
            }
        }

        float carrotR = (zone == MoveZone.Aim) ? carrotRadiusAim : (zone == MoveZone.Swim ? carrotRadiusSwim : carrotRadiusSprint);
        carrotWorld = centerWorld + worldDir * carrotR;
        carrotWorld.y = PlaneY;
    }

    private Vector3 GetCenterWorld()
    {
        Vector3 pos = (rb != null) ? rb.position : transform.position;
        pos.y = PlaneY;
        return pos;
    }

    private void UpdateFacingVisual()
    {
        if (faceSmoothing <= 0f) return;

        Vector3 dir = worldDir;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        Quaternion target = Quaternion.LookRotation(dir.normalized, Vector3.up);
        float k = 1f - Mathf.Exp(-faceSmoothing * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, k);
    }

    /// <summary>
    /// Rotate the Rigidbody (root) toward the current intent direction over time.
    /// </summary>
    private void UpdateFacingRigidbody()
    {
        if (rb == null) return;
        if (faceSmoothing <= 0f) return;

        // Choose a desired facing direction.
        // - While dragging (including Aim zone), face intent.
        // - If not dragging, optionally face horizontal velocity (looks natural when drifting).
        Vector3 dir = Vector3.zero;
        if (isDragging)
        {
            dir = worldDir;
        }
        else
        {
            Vector3 v = rb.velocity;
            v.y = 0f;
            if (v.sqrMagnitude > 1e-4f) dir = v.normalized;
        }

        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        Quaternion target = Quaternion.LookRotation(dir.normalized, Vector3.up);
        float dt = Time.fixedDeltaTime;
        float k = 1f - Mathf.Exp(-faceSmoothing * dt);
        Quaternion next = Quaternion.Slerp(rb.rotation, target, k);
        rb.MoveRotation(next);
    }

    // ---------------- Physics ----------------

    private void ApplyPulsePropulsion()
    {
        if (rb == null) return;

        if (!isDragging || targetSpeed <= 1e-4f)
        {
            kickCooldown = Mathf.Max(0f, kickCooldown - Time.fixedDeltaTime);
            return;
        }

        if (overrideDisableKicks)
        {
            kickCooldown = Mathf.Max(0f, kickCooldown - Time.fixedDeltaTime);
            return;
        }

        Vector3 v = rb.velocity;
        v.y = 0f;
        float vProj = Vector3.Dot(v, worldDir);
        float dv = targetSpeed - vProj;
        if (dv <= 0f)
        {
            kickCooldown = Mathf.Max(0f, kickCooldown - Time.fixedDeltaTime);
            return;
        }

        // Select limits
        bool inSprintRamp = (zone == MoveZone.SprintRamp) && (targetSpeed > swimSpeed + 0.01f);
        float rateMax = inSprintRamp ? kickRateMaxSprintRamp : kickRateMaxSwim;
        float impMax = inSprintRamp ? impulseMaxSprintRamp : impulseMaxSwim;

        if (propulsionOverrideEnabled)
        {
            rateMax = overrideKickRateMax;
            impMax = overrideImpulseMax;
        }

        rateMax = Mathf.Max(0f, rateMax);
        impMax = Mathf.Max(0f, impMax);

        // dv -> desired rate and impulse (both capped)
        float rate01 = (dvForMaxRate <= 1e-3f) ? 1f : Mathf.Clamp01(dv / dvForMaxRate);
        float imp01 = (dvForMaxImpulse <= 1e-3f) ? 1f : Mathf.Clamp01(dv / dvForMaxImpulse);

        float desiredRate = rateMax * rate01;
        float desiredImpulse = impMax * imp01;

        float dt = Time.fixedDeltaTime;
        kickCooldown -= dt;

        if (desiredRate <= 1e-3f || desiredImpulse <= 1e-4f) return;

        // Only allow up to a few kicks per tick (prevents spikes on low framerate)
        int kickCount = 0;
        while (kickCooldown <= 0f && kickCount < 3)
        {
            rb.AddForce(worldDir * desiredImpulse, ForceMode.Impulse);

            float interval = 1f / desiredRate;
            // small jitter avoids perfect mechanical rhythm
            interval *= Random.Range(0.92f, 1.08f);
            kickCooldown += interval;
            kickCount++;
        }
    }

    private void ApplyIdleBrakeAndPlaneLock()
    {
        if (rb == null) return;

        // Lock plane Y
        Vector3 vel = rb.velocity;
        if (useFixedPlaneY)
        {
            if (Mathf.Abs(vel.y) > 1e-4f) vel.y = 0f;
            rb.velocity = vel;

            Vector3 pos = rb.position;
            if (Mathf.Abs(pos.y - fixedPlaneY) > 1e-4f)
            {
                pos.y = fixedPlaneY;
                rb.MovePosition(pos);
            }
        }

        // Fast stop when no intent
        bool hasIntent = isDragging && targetSpeed > 1e-4f;
        if (hasIntent) return;

        Vector3 v = rb.velocity;
        v.y = 0f;
        float spd = v.magnitude;
        if (spd < stopSpeedEpsilon)
        {
            rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            return;
        }

        float brake = propulsionOverrideEnabled ? overrideIdleBrake : idleBrakeAccel;
        brake = Mathf.Max(0f, brake);
        if (brake <= 1e-3f) return;

        Vector3 a = (-v / spd) * brake;
        rb.AddForce(a, ForceMode.Acceleration);
    }

    private void UpdateTelemetry()
    {
        if (rb == null)
        {
            dvdt = 0f;
            return;
        }

        Vector3 v = rb.velocity;
        v.y = 0f;
        float vProj = Vector3.Dot(v, worldDir);
        dvdt = (vProj - prevVproj) / Mathf.Max(Time.fixedDeltaTime, 1e-5f);
        prevVproj = vProj;

        // Approx yaw angular velocity (deg/s). Use fixed delta because this runs in FixedUpdate.
        float yaw = (rb != null) ? rb.rotation.eulerAngles.y : transform.eulerAngles.y;
        float dy = Mathf.DeltaAngle(prevYaw, yaw);
        currentOmegaDeg = dy / Mathf.Max(Time.fixedDeltaTime, 1e-5f);
        prevYaw = yaw;
    }

    private void OnDrawGizmos()
    {
        if (!drawDebug) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(GetCenterWorld(), 0.05f);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(GetCenterWorld(), carrotWorld);
        Gizmos.DrawSphere(carrotWorld, 0.06f);

        if (rb != null)
        {
            Gizmos.color = Color.green;
            Vector3 hv = rb.velocity;
            hv.y = 0f;
            Gizmos.DrawLine(transform.position, transform.position + hv);
        }
    }

    // ---------------- Overrides (for MoveStateController) ----------------

    public void ClearPropulsionOverride()
    {
        propulsionOverrideEnabled = false;
        overrideKickRateMax = 0f;
        overrideImpulseMax = 0f;
        overrideIdleBrake = idleBrakeAccel;
        overrideDisableKicks = false;
    }

    public void SetPropulsionOverride(float kickRateMax, float impulseMax, float idleBrake, bool disableKicks)
    {
        propulsionOverrideEnabled = true;
        overrideKickRateMax = kickRateMax;
        overrideImpulseMax = impulseMax;
        overrideIdleBrake = idleBrake;
        overrideDisableKicks = disableKicks;
    }

    // ---------------- Exposed for IK / camera / state ----------------

    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;

    /// <summary>World-space guide point ahead of the character in input direction.</summary>
    public Vector3 GetCarrotWorld() => carrotWorld;

    /// <summary>Desired input direction on XZ (unit length when valid).</summary>
    public Vector3 GetWorldDir() => worldDir;

    /// <summary>Current world velocity (horizontal; y=0).</summary>
    public Vector3 GetVelocity()
    {
        if (rb == null) return Vector3.zero;
        Vector3 v = rb.velocity;
        v.y = 0f;
        return v;
    }

    public float GetSpeed() => GetVelocity().magnitude;
    public float GetTargetSpeed() => targetSpeed;
    public float GetSpeedDerivative() => dvdt;
    public float GetAngularVelocityDeg() => currentOmegaDeg;

    public float GetSwimSpeed() => swimSpeed;
    public float GetSprintSpeed() => sprintSpeed;
}
