using Crest;
using UnityEngine;
using Range = UnityEngine.RangeAttribute;

/// <summary>
/// Rigidbody-driven swimming controller with Crest ocean integration (mouse + dual-ring UI).
///
/// Zones:
/// - Aim   (inside inner ring): rotate only, no propulsion.
/// - Swim  (between rings):    targetSpeed = swimAvgSpeed.
/// - Sprint(outside outer):    targetSpeed = sprintTargetSpeed.
///
/// Propulsion rhythm (no frequency settings):
///   Prepare -> Kick -> Interval -> repeat
///
/// Parameters are defined in time-domain only:
/// - K = Prepare + Kick duration, bounded by [KMin, KMax] per mode
/// - I = Interval duration, bounded by [IMin, IMax] per mode
/// - R = KickTime / PrepareTime (ratio)
///
/// Demand is derived from speed deficit along propulsion direction:
///   demand01 = clamp01( (targetSpeed - vAlong) / demandDvRef )
///
/// Mapping:
/// - Higher demand => shorter K, shorter I, larger impulse.
/// - Lower  demand => longer  K, longer  I, smaller impulse.
///
/// Debug viz:
/// - Shows a "pre-kick" arrow during the N seconds before each kick.
/// - Arrow points toward the otter's rear (opposite facing) and has adjustable length range.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)]
public class CrestMovementControllerRB : MonoBehaviour
{
    public enum MoveZone { None, Aim, Swim, Sprint }
    public enum WaterState { NotInWater, Floating, Diving }

    [Header("References")]
    [SerializeField] private DualRingGuide ui;
    [SerializeField] private Rigidbody rb;

    [Header("Propulsion alignment gate (optional)")]
    [Tooltip("If enabled, only apply kick impulse when root forward is within Max Angle of the target move direction (worldDir).")]
    [SerializeField] private bool enableAlignmentGate = true;
    [Tooltip("Allowed angle (degrees) between root forward and target move direction to allow propulsion.")]
    [Range(0f, 180f)]
    [SerializeField] private float maxAlignmentAngleDeg = 80f;

    [Header("Crest Water")]
    [SerializeField, Tooltip("Transform to sample water height/flow. Falls back to rigidbody position if null.")]
    private Transform _waterProbe;
    [SerializeField] private float _waterSampleWidth = 1f;
    [SerializeField, Range(0f, 2f)] private float _waterCheckTolerance = 0.1f;

    [Header("Buoyancy")]
    [SerializeField, Tooltip("How deep (m) the object sits at rest. Smaller = floats higher.")]
    private float _equilibriumSubmersion = 0.25f;
    [SerializeField] private float _maximumBuoyancyForce = Mathf.Infinity;
    [SerializeField] private float _dragInWaterUp = 9f;
    [SerializeField] private float _buoyancyTorque = 8f;
    [SerializeField] private float _dragInWaterRotational = 0.2f;
    [SerializeField, Range(0f, 60f)] private float _maxRollPitchDeg = 20f;

    [Header("Horizontal Drag")]
    [SerializeField] private float _horizontalDragLinear = 3.0f;
    [SerializeField] private float _horizontalDragQuadratic = 0.5f;

    [Header("Target speeds")]
    [SerializeField] private float swimAvgSpeed = 2.4f;
    [SerializeField] private float sprintTargetSpeed = 4.2f;

    [Header("Rhythm ratio")]
    [Tooltip("R = KickTime / PrepareTime. Example: 0.35 means kick is shorter than prepare.")]
    [SerializeField] private float kickToPrepareRatioR = 0.35f;

    [Header("Swim K bounds (Prepare+Kick)")]
    [SerializeField] private float swimKMin = 0.18f;
    [SerializeField] private float swimKMax = 0.38f;

    [Header("Swim I bounds (Interval)")]
    [SerializeField] private float swimIMin = 0.08f;
    [SerializeField] private float swimIMax = 0.26f;

    [Header("Sprint K bounds (Prepare+Kick)")]
    [SerializeField] private float sprintKMin = 0.12f;
    [SerializeField] private float sprintKMax = 0.28f;

    [Header("Sprint I bounds (Interval)")]
    [SerializeField] private float sprintIMin = 0.06f;
    [SerializeField] private float sprintIMax = 0.18f;

    [Header("Impulse bounds (demand -> impulse)")]
    [SerializeField] private float swimImpulseMin = 0.12f;
    [SerializeField] private float swimImpulseMax = 1.10f;
    [SerializeField] private float sprintImpulseMin = 0.25f;
    [SerializeField] private float sprintImpulseMax = 2.20f;

    [Tooltip(">1 makes low-demand kicks much lighter and high-demand kicks much stronger.")]
    [SerializeField] private float impulseGamma = 1.8f;

    [Header("Demand mapping")]
    [Tooltip("Speed deficit (m/s) considered 'full demand'. dv/dvRef -> demand 0..1.")]
    [SerializeField] private float demandDvRef = 1.0f;

    [Header("Turning delay (propulsion direction inertia)")]
    [Tooltip("Max turn rate of propulsion direction (deg/sec). Smaller = more delayed turns. 0 disables.")]
    [SerializeField] private float maxTurnRateDegPerSec = 180f;

    [Header("Turn rate by speed")]
    [SerializeField] private float turnRateIdle = 180f;
    [SerializeField] private float turnRateSwim = 120f;
    [SerializeField] private float turnRateSpeedStart = 0.15f; // v0

    [Header("Facing (root yaw via Rigidbody)")]
    [SerializeField] private float faceSmoothing = 20f;

    [Header("Release auto align")]
    [Tooltip("After mouse release, keep rotating body toward the last intent direction for a short time.")]
    [SerializeField] private bool enableReleaseAutoAlign = true;
    [Tooltip("Maximum duration of auto-align after release.")]
    [SerializeField] private float releaseAlignDuration = 0.9f;
    [Tooltip("Blend weight (0..1) over releaseAlignDuration. X=normalized time since release, Y=weight applied to turning/alignment intent.")]
    [SerializeField] private AnimationCurve releaseAlignWeightCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
    [Tooltip("Stop auto-align when facing is within this angle (deg) of the release direction.")]
    [Range(0.1f, 30f)]
    [SerializeField] private float releaseAlignStopAngleDeg = 2f;
    [Tooltip("Optional smoothing for release auto-align. <= 0 uses faceSmoothing.")]
    [SerializeField] private float releaseAlignSmoothing = 16f;
    [Tooltip("Optional max yaw turn rate during release auto-align (deg/sec). 0 disables and uses smoothing only.")]
    [SerializeField] private float releaseAlignMaxTurnRateDegPerSec = 180f;

    [Header("Fast stop")]
    [SerializeField] private float idleBrakeAccel = 12f;
    [SerializeField] private float stopSpeedEpsilon = 0.10f;

    [Header("Carrot / guide (for IK & UI)")]
    [SerializeField] private float carrotRadiusAim = 1.2f;
    [SerializeField] private float carrotRadiusSwim = 2.0f;
    [SerializeField] private float carrotRadiusSprint = 2.8f;

    [Header("Input smoothing")]
    [SerializeField] private float inputSmoothing = 18f;

    [Header("Pre-kick visualization")]
    [Tooltip("Show preview during the N seconds BEFORE the kick impulse.")]
    [SerializeField] private float preKickGizmoLead = 0.20f;

    [Tooltip("Side offset so preview doesn't overlap other arrow visuals.")]
    [SerializeField] private float kickVizSideOffset = 0.35f;

    [Tooltip("Raise preview slightly above plane.")]
    [SerializeField] private float kickVizUpOffset = 0.06f;

    [Tooltip("Preview arrow length range (meters). Length scales with demand.")]
    [SerializeField] private float kickVizLenMin = 0.6f;
    [SerializeField] private float kickVizLenMax = 1.8f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;

    // ---------------- Runtime input ----------------
    private bool isDragging;
    private Vector3 inputDirWorld;   // smoothed XZ direction from player to mouse hit
    private float inputDistWorld;    // smoothed XZ distance from player to mouse hit
    private MoveZone zone = MoveZone.None;

    // Intent
    private Vector3 worldDir = Vector3.forward; // desired on XZ
    private Vector3 steerDir = Vector3.forward; // delayed direction used for propulsion/facing
    private float targetSpeed;
    private Vector3 carrotWorld;

    // Rhythm phase machine
    private enum Phase { Prepare, Kick, Interval }
    private Phase phase = Phase.Interval;
    private float phaseRemain = 0f;

    // Solved for current cycle (telemetry)
    private float demand01;
    private float K;            // prepare+kick
    private float I;            // interval
    private float prepareTime;
    private float kickTime;
    private float impulse;

    // For pre-kick preview
    private float nextKickTime;

    // Release auto-align runtime
    private bool releaseAlignActive;
    private float releaseAlignTimer;
    private Vector3 releaseAlignDir = Vector3.forward;
    private Vector3 releaseAlignSteerDir = Vector3.forward;

    // Debug/compat getters
    private float lastKickRate;     // derived cadence (Hz), not a setting
    private float lastKickImpulse;

    // Crest runtime state
    private WaterState _waterState = WaterState.NotInWater;
    private readonly SampleHeightHelper _sampleHeightHelper = new SampleHeightHelper();
    private readonly SampleFlowHelper _sampleFlowHelper = new SampleFlowHelper();
    private float _submersionHeight;
    private Vector3 _waterSurfaceVel;
    private Vector3 _waterNormal = Vector3.up;
    private float _sampledWaterHeight;
    private bool _hasSampledWater;

    private float PlaneY => _hasSampledWater
        ? _sampledWaterHeight
        : (rb != null ? rb.position.y : transform.position.y);

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.useGravity = true;
            rb.constraints = RigidbodyConstraints.None;
        }

        worldDir = transform.forward; worldDir.y = 0f;
        if (worldDir.sqrMagnitude > 1e-6f) worldDir.Normalize();
        steerDir = worldDir.sqrMagnitude > 1e-6f ? worldDir : Vector3.forward;

        // Start in a short interval so we don't instantly kick on play.
        phase = Phase.Interval;
        phaseRemain = 0.10f;
        nextKickTime = Time.time + phaseRemain;
    }

    private void Update()
    {
        HandleInput();
        UpdateIntentAndCarrot();
    }

    private void FixedUpdate()
    {
        UpdateWaterState();
        if (_waterState == WaterState.Floating)
            ApplyBuoyancyForce();

        UpdateSteerDir(Time.fixedDeltaTime);
        UpdateReleaseAutoAlign(Time.fixedDeltaTime);
        UpdateFacing();
        StepRhythm(Time.fixedDeltaTime);
        ApplyIdleBrake();
    }

    // ---------------- Crest Water ----------------

    private void UpdateWaterState()
    {
        if (OceanRenderer.Instance == null)
        {
            _waterState = WaterState.NotInWater;
            _submersionHeight = 0f;
            _waterSurfaceVel = Vector3.zero;
            _hasSampledWater = false;
            return;
        }

        Vector3 probePos = _waterProbe != null ? _waterProbe.position : rb.position;

        _sampleHeightHelper.Init(probePos, _waterSampleWidth, true);
        _sampleHeightHelper.Sample(out Vector3 disp, out var normal, out var waterSurfaceVel);

        _sampledWaterHeight = disp.y + OceanRenderer.Instance.SeaLevel;
        _submersionHeight = _sampledWaterHeight - probePos.y;
        _hasSampledWater = true;

        _waterState = _submersionHeight <= -_waterCheckTolerance ? WaterState.NotInWater : WaterState.Floating;
        _waterNormal = normal;

        _sampleFlowHelper.Init(probePos, _waterSampleWidth);
        _sampleFlowHelper.Sample(out var surfaceFlow);
        _waterSurfaceVel = waterSurfaceVel + new Vector3(surfaceFlow.x, 0f, surfaceFlow.y);
    }

    private void ApplyBuoyancyForce()
    {
        // Buoyancy: cubic submersion.  coeff derived so that at _equilibriumSubmersion the
        // upward acceleration exactly equals gravity magnitude → object rests at that depth.
        float h0 = Mathf.Max(0.01f, _equilibriumSubmersion);
        float coeff = Physics.gravity.magnitude / (h0 * h0 * h0);
        Vector3 buoyancy = coeff * _submersionHeight * _submersionHeight * _submersionHeight * -Physics.gravity.normalized;
        if (_maximumBuoyancyForce < Mathf.Infinity)
        {
            buoyancy = Vector3.ClampMagnitude(buoyancy, _maximumBuoyancyForce);
        }
        rb.AddForce(buoyancy, ForceMode.Acceleration);

        // Vertical drag relative to water surface velocity
        var velocityRelativeToWater = rb.velocity - _waterSurfaceVel;
        float verticalDrag = _dragInWaterUp * Vector3.Dot(Vector3.up, -velocityRelativeToWater);
        rb.AddForce(verticalDrag * Vector3.up, ForceMode.Acceleration);

        // Torque alignment to water surface normal (clamped to max roll/pitch)
        Vector3 limitedNormal = _waterNormal;
        float normalAngle = Vector3.Angle(Vector3.up, _waterNormal);
        if (normalAngle > _maxRollPitchDeg && normalAngle > 0f)
        {
            Vector3 axis = Vector3.Cross(Vector3.up, _waterNormal).normalized;
            limitedNormal = Quaternion.AngleAxis(_maxRollPitchDeg, axis) * Vector3.up;
        }

        Vector3 torqueWidth = Vector3.Cross(transform.up, limitedNormal);
        rb.AddTorque(torqueWidth * _buoyancyTorque, ForceMode.Acceleration);
        rb.AddTorque(-_dragInWaterRotational * rb.angularVelocity, ForceMode.Acceleration);

        // Horizontal drag (linear + quadratic)
        ApplyHorizontalDrag();
    }

    private void ApplyHorizontalDrag()
    {
        Vector3 velXZ = rb.velocity;
        velXZ.y = 0f;
        float speed = velXZ.magnitude;
        if (speed < 1e-4f) return;

        Vector3 dragDir = -velXZ / speed;
        float dragMag = _horizontalDragLinear * speed + _horizontalDragQuadratic * speed * speed;
        rb.AddForce(dragDir * dragMag, ForceMode.Acceleration);
    }

    // ---------------- Input ----------------

    private void HandleInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            ComputeMouseWorld(out inputDirWorld, out inputDistWorld);
        }
        else if (Input.GetMouseButtonUp(0))
        {
            bool hadDrag = isDragging;
            isDragging = false;
            zone = MoveZone.None;
            targetSpeed = 0f;
            inputDistWorld = 0f;

            ui?.HideArrow();

            if (hadDrag) BeginReleaseAutoAlign();
        }

        if (!isDragging) return;

        ComputeMouseWorld(out Vector3 rawDir, out float rawDist);

        float dt = Time.deltaTime;
        float k = inputSmoothing <= 0f ? 1f : (1f - Mathf.Exp(-inputSmoothing * dt));

        // Smooth direction via Vector3.Slerp, distance via Lerp
        if (rawDir.sqrMagnitude > 1e-6f && inputDirWorld.sqrMagnitude > 1e-6f)
            inputDirWorld = Vector3.Slerp(inputDirWorld, rawDir, k);
        else
            inputDirWorld = rawDir;
        inputDistWorld = Mathf.Lerp(inputDistWorld, rawDist, k);

        float innerR = (ui != null) ? ui.InnerRadius : 1.2f;
        float outerR = (ui != null) ? ui.OuterRadius : 2.0f;

        if (inputDistWorld <= innerR) zone = MoveZone.Aim;
        else if (inputDistWorld < outerR) zone = MoveZone.Swim;
        else zone = MoveZone.Sprint;

        ui?.SetArrow(inputDirWorld, inputDistWorld);
    }

    private void ComputeMouseWorld(out Vector3 dirXZ, out float dist)
    {
        dirXZ = Vector3.forward;
        dist = 0f;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Screen-space delta: otter screen position → mouse screen position.
        // This is independent of rb.position changes (kicks/buoyancy), avoiding
        // the feedback loop that world-space delta would create.
        Vector3 playerPos = rb != null ? rb.position : transform.position;
        Vector2 otterScreen = cam.WorldToScreenPoint(playerPos);
        Vector2 mouseDelta = (Vector2)Input.mousePosition - otterScreen;
        float pxRadius = mouseDelta.magnitude;
        if (pxRadius < 1e-4f) return;
        Vector2 screenDir = mouseDelta / pxRadius;

        // Convert screen direction to world XZ via camera axes
        Vector3 camRight = cam.transform.right; camRight.y = 0f;
        Vector3 camUp    = cam.transform.up;    camUp.y = 0f;
        if (camRight.sqrMagnitude < 1e-6f) camRight = Vector3.right;
        if (camUp.sqrMagnitude    < 1e-6f) camUp    = Vector3.forward;
        camRight.Normalize();
        camUp.Normalize();

        dirXZ = camRight * screenDir.x + camUp * screenDir.y;
        dirXZ.y = 0f;
        if (dirXZ.sqrMagnitude < 1e-6f) { dirXZ = Vector3.forward; return; }
        dirXZ.Normalize();

        // Convert pixel radius to world distance using camera scale
        float pxPerUnit;
        if (cam.orthographic)
        {
            pxPerUnit = Screen.height / (2f * Mathf.Max(0.01f, cam.orthographicSize));
        }
        else
        {
            float d = Mathf.Max(0.01f, Vector3.Distance(cam.transform.position, playerPos));
            float halfTan = Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f);
            pxPerUnit = Screen.height / (2f * d * halfTan);
        }

        dist = pxRadius / Mathf.Max(1f, pxPerUnit);
    }

    // ---------------- Intent / carrot ----------------

    private void UpdateIntentAndCarrot()
    {
        if (isDragging)
        {
            worldDir = inputDirWorld;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude > 1e-6f) worldDir.Normalize();
            else worldDir = Vector3.forward;
        }

        if (!isDragging) targetSpeed = 0f;
        else
        {
            targetSpeed = zone switch
            {
                MoveZone.Aim => 0f,
                MoveZone.Swim => swimAvgSpeed,
                MoveZone.Sprint => sprintTargetSpeed,
                _ => 0f
            };
        }

        float carrotR = zone switch
        {
            MoveZone.Aim => carrotRadiusAim,
            MoveZone.Swim => carrotRadiusSwim,
            MoveZone.Sprint => carrotRadiusSprint,
            _ => carrotRadiusSwim
        };

        Vector3 center = (rb != null) ? rb.position : transform.position;
        center.y = PlaneY;
        carrotWorld = center + worldDir * carrotR;
        carrotWorld.y = PlaneY;
    }

    // ---------------- Turning delay ----------------

    private void UpdateSteerDir(float dt)
    {
        // No propulsion intent => keep steerDir stable (don't snap around)
        if (!isDragging) return;

        Vector3 desired = worldDir;
        desired.y = 0f;
        if (desired.sqrMagnitude < 1e-6f) return;
        desired.Normalize();

        float speed = GetSpeed(); // current otter's rigidbody speed

        float t = Mathf.InverseLerp(
            turnRateSpeedStart,
            swimAvgSpeed,   // reference swim speed
            speed
        );
        t = Mathf.Clamp01(t);
        t = t * t * (3f - 2f * t); // smoothstep

        float dynamicTurnRate =
            Mathf.Lerp(turnRateIdle, turnRateSwim, t);

        float maxRad = Mathf.Deg2Rad * dynamicTurnRate * Mathf.Max(1e-5f, dt);
        steerDir = Vector3.RotateTowards(steerDir, desired, maxRad, 0f);
        steerDir.y = 0f;
        if (steerDir.sqrMagnitude > 1e-6f) steerDir.Normalize();
    }

    // ---------------- Facing (yaw only) ----------------

    private void UpdateFacing()
    {
        if (rb == null) return;

        Vector3 dir = steerDir;
        float smoothing = faceSmoothing;

        if (!isDragging)
        {
            if (!releaseAlignActive) return;
            float w = GetAimBlend01();
            if (w <= 1e-4f) return;

            // Ease-out: as w decays, rotation response becomes gentler (less "rigid snap").
            dir = releaseAlignSteerDir;
            smoothing = releaseAlignSmoothing > 0f ? releaseAlignSmoothing : faceSmoothing;
            smoothing *= w;
        }

        if (smoothing <= 0f) return;

        // Dragging: face steerDir. Released: face releaseAlignDir for a short window.
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        // Yaw-only: decompose current rotation, smooth only yaw, preserve pitch/roll from buoyancy torque
        Quaternion targetYaw = Quaternion.Euler(0f, Quaternion.LookRotation(dir, Vector3.up).eulerAngles.y, 0f);
        Quaternion currentYaw = Quaternion.Euler(0f, rb.rotation.eulerAngles.y, 0f);

        float k = 1f - Mathf.Exp(-smoothing * Time.fixedDeltaTime);
        Quaternion newYaw = Quaternion.Slerp(currentYaw, targetYaw, k);

        // Preserve pitch/roll from physics (buoyancy torque)
        Quaternion tilt = Quaternion.Inverse(currentYaw) * rb.rotation;
        rb.MoveRotation(newYaw * tilt);
    }

    private void BeginReleaseAutoAlign()
    {
        if (!enableReleaseAutoAlign)
        {
            releaseAlignActive = false;
            return;
        }

        Vector3 dir = worldDir;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f)
        {
            dir = steerDir;
            dir.y = 0f;
        }
        if (dir.sqrMagnitude < 1e-6f)
        {
            dir = transform.forward;
            dir.y = 0f;
        }
        if (dir.sqrMagnitude < 1e-6f)
        {
            releaseAlignActive = false;
            return;
        }

        releaseAlignDir = dir.normalized;
        // Start from current facing to avoid an instantaneous target snap.
        releaseAlignSteerDir = transform.forward;
        releaseAlignSteerDir.y = 0f;
        if (releaseAlignSteerDir.sqrMagnitude < 1e-6f) releaseAlignSteerDir = releaseAlignDir;
        releaseAlignSteerDir.Normalize();
        releaseAlignTimer = 0f;
        releaseAlignActive = true;
    }

    private void UpdateReleaseAutoAlign(float dt)
    {
        if (!releaseAlignActive) return;
        if (isDragging)
        {
            releaseAlignActive = false;
            return;
        }

        releaseAlignTimer += Mathf.Max(0f, dt);
        float maxDuration = Mathf.Max(0.05f, releaseAlignDuration);
        if (releaseAlignTimer >= maxDuration)
        {
            releaseAlignActive = false;
            return;
        }

        // Soft steer toward release direction with optional turn-rate cap (gives "inertia" feel).
        float turnRate = Mathf.Max(0f, releaseAlignMaxTurnRateDegPerSec);
        if (turnRate > 1e-3f)
        {
            float maxRad = Mathf.Deg2Rad * turnRate * Mathf.Max(1e-5f, dt);
            releaseAlignSteerDir = Vector3.RotateTowards(releaseAlignSteerDir, releaseAlignDir, maxRad, 0f);
            releaseAlignSteerDir.y = 0f;
            if (releaseAlignSteerDir.sqrMagnitude > 1e-6f) releaseAlignSteerDir.Normalize();
        }

        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        float angle = Vector3.Angle(fwd, releaseAlignDir);
        if (angle <= Mathf.Clamp(releaseAlignStopAngleDeg, 0.1f, 45f))
            releaseAlignActive = false;
    }

    /// <summary>
    /// 0..1 blend for "aim intent" used by other systems (spine/neck).
    /// Dragging => 1. After release => decays to 0 according to curve over releaseAlignDuration.
    /// </summary>
    public float GetAimBlend01()
    {
        if (isDragging) return 1f;
        if (!releaseAlignActive) return 0f;

        float dur = Mathf.Max(0.001f, releaseAlignDuration);
        float t01 = Mathf.Clamp01(releaseAlignTimer / dur);
        float w = releaseAlignWeightCurve != null ? releaseAlignWeightCurve.Evaluate(t01) : (1f - t01);
        return Mathf.Clamp01(w);
    }

    /// <summary>
    /// During release auto-align, this is the softly-steered direction toward the last intent.
    /// Otherwise returns current steerDir (drag) or worldDir (idle).
    /// </summary>
    public Vector3 GetAimWorldDir()
    {
        if (isDragging) return steerDir;
        if (releaseAlignActive) return releaseAlignSteerDir;
        return worldDir;
    }

    // ---------------- Rhythm + Propulsion (no frequency settings) ----------------

    private void StepRhythm(float dt)
    {
        if (rb == null) return;

        // No intent or Aim => no kicks; let drag/brake handle it.
        if (!isDragging || targetSpeed <= 1e-4f)
        {
            lastKickRate = 0f;
            lastKickImpulse = 0f;
            phaseRemain = Mathf.Max(0f, phaseRemain - dt);
            return;
        }

        // Optional: gate propulsion based on root forward vs target movement direction (worldDir)
        bool allowPropulsion = true;
        if (enableAlignmentGate)
            allowPropulsion = IsForwardAlignedWithWorldDir(maxAlignmentAngleDeg);

        // Demand from speed deficit along steerDir
        float vAlong = Vector3.Dot(GetVelocity(), steerDir);
        float dv = targetSpeed - vAlong;
        if (dv < 0f) dv = 0f;

        demand01 = Mathf.Clamp01(dv / Mathf.Max(0.01f, demandDvRef));

        bool sprint = (zone == MoveZone.Sprint);

        float Kmin = Mathf.Max(0.02f, sprint ? sprintKMin : swimKMin);
        float Kmax = Mathf.Max(Kmin, sprint ? sprintKMax : swimKMax);

        float Imin = Mathf.Max(0.01f, sprint ? sprintIMin : swimIMin);
        float Imax = Mathf.Max(Imin, sprint ? sprintIMax : swimIMax);

        // Higher demand => shorter K and I (more frequent and faster kicks)
        K = Mathf.Lerp(Kmax, Kmin, demand01);
        I = Mathf.Lerp(Imax, Imin, demand01);

        // Split K into prepare/kick by ratio R
        float R = Mathf.Max(0.01f, kickToPrepareRatioR);
        prepareTime = K / (1f + R);
        kickTime = K - prepareTime; // == K*R/(1+R)

        // Impulse grows with demand (and indirectly with shorter K/I)
        float impMin = sprint ? sprintImpulseMin : swimImpulseMin;
        float impMax = sprint ? sprintImpulseMax : swimImpulseMax;
        float g = Mathf.Max(0.1f, impulseGamma);
        impulse = Mathf.Lerp(impMin, impMax, Mathf.Pow(demand01, g));

        // Derived cadence for telemetry only (NOT a setting)
        float T = Mathf.Max(1e-3f, K + I);
        float cadenceHz = 1f / T;

        // Phase machine
        phaseRemain -= dt;
        int guard = 0;
        while (phaseRemain <= 0f && guard++ < 4)
        {
            switch (phase)
            {
                case Phase.Prepare:
                    phase = Phase.Kick;
                    phaseRemain += Mathf.Max(0.01f, kickTime);

                    // Apply impulse at the START of Kick phase
                    if (allowPropulsion)
                    {
                        rb.AddForce(steerDir * impulse, ForceMode.Impulse);
                        lastKickImpulse = impulse;
                    }
                    else
                    {
                        // Still advance rhythm, but no propulsion this kick.
                        lastKickImpulse = 0f;
                    }

                    lastKickRate = cadenceHz;

                    // Predict next kick moment (after kick + interval + next prepare)
                    nextKickTime = Time.time + kickTime + I + prepareTime;
                    break;

                case Phase.Kick:
                    phase = Phase.Interval;
                    phaseRemain += Mathf.Max(0.01f, I);

                    // Next kick after interval + prepare
                    nextKickTime = Time.time + I + prepareTime;
                    break;

                case Phase.Interval:
                default:
                    phase = Phase.Prepare;
                    phaseRemain += Mathf.Max(0.01f, prepareTime);

                    // >>> Your desired moment: Interval -> Prepare (cycle start)
                    EmitKickCycleStart();

                    // Next kick when prepare ends
                    nextKickTime = Time.time + prepareTime;
                    break;
            }
        }

        // Keep nextKickTime valid even mid-phase
        if (nextKickTime < Time.time)
        {
            float toKick =
                phase == Phase.Prepare ? Mathf.Max(0f, phaseRemain) :
                phase == Phase.Kick ? 0f :
                Mathf.Max(0f, phaseRemain + prepareTime);

            nextKickTime = Time.time + toKick;
        }
    }

    // ---------------- Idle brake (no plane lock) ----------------

    private void ApplyIdleBrake()
    {
        if (rb == null) return;

        bool hasIntent = isDragging && targetSpeed > 1e-4f;
        if (hasIntent) return;

        Vector3 v = rb.velocity; v.y = 0f;
        float spd = v.magnitude;

        if (spd < stopSpeedEpsilon)
        {
            rb.velocity = new Vector3(0f, rb.velocity.y, 0f);
            return;
        }

        float brake = Mathf.Max(0f, idleBrakeAccel);
        if (brake <= 1e-3f) return;

        Vector3 a = (-v / Mathf.Max(1e-4f, spd)) * brake;
        rb.AddForce(a, ForceMode.Acceleration);
    }

    // ---------------- Gizmos (kick preview points to REAR) ----------------

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (!Application.isPlaying) return;

        // Only show when we have propulsion intent
        if (!isDragging || targetSpeed <= 1e-4f) return;

        float lead = Mathf.Max(0f, preKickGizmoLead);
        if (lead <= 1e-4f) return;

        float t = Time.time;
        if (t < nextKickTime - lead || t > nextKickTime) return;

        Vector3 origin = (rb != null ? rb.position : transform.position);
        origin.y = PlaneY;

        // Rear direction uses actual facing (transform.forward) for intuitive "push water backward" preview
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = steerDir;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 rearDir = -fwd;

        // Offset sideways to avoid overlapping your UI arrow
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        if (right.sqrMagnitude > 1e-6f) right.Normalize();

        Vector3 o = origin + Vector3.up * kickVizUpOffset + right * kickVizSideOffset;

        // Pulse as we approach kick moment (more visible)
        float phase01 = Mathf.InverseLerp(nextKickTime - lead, nextKickTime, t);
        float alpha = Mathf.Lerp(0.20f, 1.0f, phase01 * phase01);

        // Length scales with demand (developer-set range)
        float lenMin = Mathf.Max(0.05f, kickVizLenMin);
        float lenMax = Mathf.Max(lenMin, kickVizLenMax);
        float len = Mathf.Lerp(lenMin, lenMax, Mathf.Clamp01(demand01));

        Gizmos.color = new Color(1f, 0.25f, 0.15f, alpha);
        Gizmos.DrawLine(o, o + rearDir * len);
        Gizmos.DrawSphere(o + rearDir * len, 0.10f);
        Gizmos.DrawWireSphere(o, 0.10f);
    }

    // ---------------- Kick cycle event ----------------

    public struct KickCycleEvent
    {
        // Total time for one leg cycle motion: Prepare + Kick
        public float cycleDuration;

        // Next interval (optional, for anticipation / blending)
        public float intervalDuration;

        // Optional scaling so legs can modulate amplitude
        public float demand01;
    }

    public event System.Action<KickCycleEvent> OnKickCycleStart;

    private void EmitKickCycleStart()
    {
        if (OnKickCycleStart == null) return;

        OnKickCycleStart.Invoke(new KickCycleEvent
        {
            cycleDuration    = Mathf.Max(1e-4f, prepareTime + kickTime),
            intervalDuration = Mathf.Max(0f, I),
            demand01         = Mathf.Clamp01(demand01),
        });
    }

    // ---------------- Public API (compat) ----------------

    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;
    public WaterState GetWaterState() => _waterState;

    public Vector3 GetCarrotWorld() => carrotWorld;
    public Vector3 GetWorldDir() => worldDir;
    public Vector3 GetSteerDir() => steerDir;

    public Vector3 GetVelocity()
    {
        if (rb == null) return Vector3.zero;
        Vector3 v = rb.velocity;
        v.y = 0f;
        return v;
    }

    public float GetSpeed() => GetVelocity().magnitude;

    public float GetSpeedAlongIntent()
    {
        Vector3 v = GetVelocity();
        return Vector3.Dot(v, steerDir);
    }

    public float GetTargetSpeed() => targetSpeed;
    public float GetSwimSpeed() => swimAvgSpeed;
    public float GetSprintSpeed() => sprintTargetSpeed;

    // Derived telemetry (NOT settings)
    public float GetLastKickRate() => lastKickRate;
    public float GetLastKickImpulse() => lastKickImpulse;

    // Optional debug getters
    public float GetDemand01() => demand01;
    public float GetK() => K;
    public float GetI() => I;
    public float GetPrepareTime() => prepareTime;
    public float GetKickTime() => kickTime;

    // ---------------- Alignment gate helper ----------------

    private bool IsForwardAlignedWithWorldDir(float maxAngleDeg)
    {
        Vector3 fwd = transform.forward; fwd.y = 0f;
        Vector3 dir = worldDir;         dir.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f || dir.sqrMagnitude < 1e-6f) return true;
        fwd.Normalize();
        dir.Normalize();
        float ang = Vector3.Angle(fwd, dir);
        return ang <= Mathf.Clamp(maxAngleDeg, 0f, 180f);
    }
}
