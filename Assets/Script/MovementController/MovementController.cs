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

    [Header("Speeds")]
    [SerializeField] private float maxSwimSpeed = 5f;
    [SerializeField] private float maxSprintSpeed = 8f;

    [Header("Acceleration / Turning")]
    [SerializeField] private float acceleration = 14f;
    [SerializeField] private float turnRateDeg = 240f;
    [SerializeField] private float sprintTurnRateDeg = 360f;

    [Header("Carrot (Chase Target)")]
    [SerializeField] private float carrotRadiusAim = 0.8f;
    [SerializeField] private float carrotRadiusSwimMax = 2.8f;
    [SerializeField] private float carrotRadiusSprint = 2.8f;
    [SerializeField] private float keepDistance = 1.2f;
    [SerializeField] private float carrotFollow = 18f;
    [SerializeField] private float velocityDirectionLerp = 10f;

    [Header("Input Smoothing")]
    [SerializeField] private float inputSmoothing = 0f;

    [Header("Debug")]
    [SerializeField] private bool debugDraw = false;

    private bool isDragging;
    private Vector2 dirScreen = Vector2.up;
    private float radiusPx;

    private Vector2 dirScreenSm = Vector2.up;
    private float radiusPxSm;

    private MoveZone zone = MoveZone.None;
    private float currentSpeed;
    private Vector3 velocity;

    private Vector3 centerWorld;   // now = otter position (from UI)
    private Vector3 carrotWorld;
    private bool carrotInitialized;

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : transform.position.y;

    private void Start()
    {
        if (inputCamera == null) inputCamera = Camera.main;
        if (ui == null) ui = FindObjectOfType<DualRingUIController>();

        centerWorld = GetCenterWorld();
        carrotWorld = centerWorld;
        carrotInitialized = true;
    }

    private void LateUpdate()
    {
        HandleInput();
        ApplyMovement();

        if (debugDraw) DrawDebug();
    }

    private void HandleInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            currentSpeed = 0f;
            velocity = Vector3.zero;
        }

        if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
            zone = MoveZone.None;
            currentSpeed = 0f;
            velocity = Vector3.zero;
            radiusPx = 0f;
            radiusPxSm = 0f;
            ui?.HideArrow();
            return;
        }

        if (!isDragging)
            return;

        Vector2 center = (ui != null) ? ui.ScreenCenterPx : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 mouse = Input.mousePosition;
        Vector2 delta = mouse - center;

        float rawR = delta.magnitude;
        Vector2 rawDir = rawR > 1e-3f ? (delta / rawR) : Vector2.up;

        if (inputSmoothing > 0f)
        {
            float k = 1f - Mathf.Exp(-inputSmoothing * Time.deltaTime);

            dirScreenSm = Vector2.Lerp(dirScreenSm, rawDir, k);
            if (dirScreenSm.sqrMagnitude > 1e-6f) dirScreenSm.Normalize();

            radiusPxSm = Mathf.Lerp(radiusPxSm, rawR, k);

            dirScreen = dirScreenSm;
            radiusPx = radiusPxSm;
        }
        else
        {
            dirScreen = rawDir;
            radiusPx = rawR;
            dirScreenSm = rawDir;
            radiusPxSm = rawR;
        }

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

        float clampedPx = Mathf.Clamp(radiusPx, 0f, outerPx);

        if (clampedPx <= innerPx) zone = MoveZone.Aim;
        else if (clampedPx < outerPx) zone = MoveZone.Swim;
        else zone = MoveZone.Sprint;

        ui?.UpdateArrowFromScreen(dirScreen, clampedPx);
    }

    private void ApplyMovement()
    {
        if (!isDragging) return;
        if (inputCamera == null) return;

        centerWorld = GetCenterWorld();

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;
        float rClamped = Mathf.Clamp(radiusPx, 0f, outerPx);

        float tBand = Mathf.InverseLerp(innerPx, outerPx, Mathf.Clamp(rClamped, innerPx, outerPx));
        tBand = tBand * tBand * (3f - 2f * tBand);

        Vector3 worldDir = ScreenDirToWorldXZ(dirScreen);
        if (worldDir.sqrMagnitude < 1e-6f) return;

        float desiredCarrotRadius =
            zone == MoveZone.Aim ? carrotRadiusAim :
            zone == MoveZone.Swim ? Mathf.Lerp(carrotRadiusAim, carrotRadiusSwimMax, tBand) :
            carrotRadiusSprint;

        // ===== carrot now anchored to CHARACTER-centered ring =====
        Vector3 desiredCarrot = centerWorld + worldDir * desiredCarrotRadius;
        desiredCarrot.y = PlaneY;

        float dt = Time.deltaTime;
        float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, carrotFollow) * dt);
        carrotWorld = carrotInitialized ? Vector3.Lerp(carrotWorld, desiredCarrot, k) : desiredCarrot;
        carrotInitialized = true;

        // never-catch behavior
        if (zone == MoveZone.Swim || zone == MoveZone.Sprint)
        {
            Vector3 toCarrot = carrotWorld - transform.position;
            toCarrot.y = 0f;
            if (toCarrot.magnitude < keepDistance)
            {
                carrotWorld = transform.position + worldDir * keepDistance;
                carrotWorld.y = PlaneY;
            }
        }

        float targetSpeed = 0f;
        float turnRate = turnRateDeg;

        if (zone == MoveZone.Aim)
        {
            targetSpeed = 0f; // aim only (rotate)
            turnRate = turnRateDeg;
        }
        else if (zone == MoveZone.Swim)
        {
            targetSpeed = maxSwimSpeed * tBand;
            turnRate = turnRateDeg;
        }
        else
        {
            targetSpeed = maxSprintSpeed;
            turnRate = sprintTurnRateDeg;
        }

        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, acceleration * dt);

        Vector3 desiredMoveDir = carrotWorld - transform.position;
        desiredMoveDir.y = 0f;
        desiredMoveDir = desiredMoveDir.sqrMagnitude > 1e-6f ? desiredMoveDir.normalized : worldDir;

        Vector3 desiredVel = desiredMoveDir * currentSpeed;

        if (velocityDirectionLerp > 0f)
        {
            float kv = 1f - Mathf.Exp(-velocityDirectionLerp * dt);
            velocity = Vector3.Lerp(velocity, desiredVel, kv);
        }
        else
        {
            velocity = desiredVel;
        }

        // translate only in swim/sprint
        if (zone == MoveZone.Swim || zone == MoveZone.Sprint)
            transform.position += velocity * dt;

        // rotate toward velocity direction (or desiredMoveDir)
        Vector3 faceDir = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : desiredMoveDir;
        faceDir.y = 0f;

        if (faceDir.sqrMagnitude > 1e-6f)
        {
            Quaternion targetRot = Quaternion.LookRotation(faceDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnRate * dt);
        }
    }

    private Vector3 GetCenterWorld()
    {
        // Center is simply the otter position (ring under the otter)
        Vector3 p = transform.position;
        if (useFixedPlaneY) p.y = fixedPlaneY;
        return p;
    }

    private Vector3 ScreenDirToWorldXZ(Vector2 dir)
    {
        // For top-down cameras: use camera.up as screen-Y axis (not forward).
        Vector3 camRight = inputCamera.transform.right; camRight.y = 0f;
        Vector3 camUp = inputCamera.transform.up; camUp.y = 0f;

        if (camRight.sqrMagnitude < 1e-6f) camRight = Vector3.right;
        if (camUp.sqrMagnitude < 1e-6f) camUp = Vector3.forward;

        camRight.Normalize();
        camUp.Normalize();

        Vector3 w = camRight * dir.x + camUp * dir.y;
        w.y = 0f;
        return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
    }

    private void DrawDebug()
    {
        Debug.DrawRay(centerWorld, Vector3.up * 0.4f, Color.cyan);
        Debug.DrawLine(centerWorld, carrotWorld, Color.yellow);
        Debug.DrawRay(transform.position, velocity, Color.green);
    }

    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;
    public bool IsSprinting() => zone == MoveZone.Sprint;
    public Vector3 GetCarrotWorld() => carrotWorld;
    public Vector3 GetVelocity() => velocity;
}
