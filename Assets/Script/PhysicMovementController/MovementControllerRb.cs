using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rigidbody-driven swimming controller (mouse + dual-ring UI).
///
/// Semantics:
/// - Aim   (inside inner ring): rotate only, targetSpeed = 0.
/// - Swim  (between rings):    targetSpeed = swimSpeed; try to reach + maintain.
/// - Sprint(outside outer):    targetSpeed = sprintSpeed; try to reach + maintain.
///
/// Key behavior:
/// - Acceleration phase: noticeable discrete kicks.
/// - Cruise phase: near-constant speed with periodic maintenance kicks.
/// - If maintaining the requested speed would require a kick rate above the configured max,
///   the controller saturates and the otter is allowed to slow/stop/move backward due to water forces.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)]
public class MovementControllerRB : MonoBehaviour
{
    public enum MoveZone { None, Aim, Swim, Sprint }

    /// <summary>
    /// Fired once per actual propulsion impulse (each rb.AddForce(..., ForceMode.Impulse)).
    /// Use this for strict animation sync (hind limbs, splash VFX, etc.).
    /// </summary>
    public event Action OnKickImpulse;

    /// <summary>
    /// Same as OnKickImpulse, but includes the solved cadence parameters for this impulse.
    /// impulse: the actual impulse used for this AddForce (N*s)
    /// rateHz : desired kick rate at the moment of this impulse (Hz)
    /// </summary>
    public event Action<float, float> OnKickImpulseDetailed;

    [Header("References")]
    [SerializeField] private Camera inputCamera;
    [SerializeField] private DualRingUIControllerRB ui;
    [SerializeField] private Rigidbody rb;

    [Header("Water surface plane")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Target speeds")]
    [SerializeField] private float swimSpeed = 2.4f;
    [SerializeField] private float sprintSpeed = 4.2f;

    [Header("Acceleration time-scales (if not force-limited)")]
    [Tooltip("Desired time-scale to close speed gap (along intent) during Swim acceleration.")]
    [SerializeField] private float swimAccelTime = 1.1f;

    [Tooltip("Desired time-scale to close speed gap (along intent) during Sprint acceleration.")]
    [SerializeField] private float sprintAccelTime = 0.30f;

    [Header("Kick limits")]
    [SerializeField] private float kickRateMaxSwim = 4.2f;
    [SerializeField] private float kickRateMaxSprint = 6.5f;
    [SerializeField] private float impulseMaxSwim = 2.1f;
    [SerializeField] private float impulseMaxSprint = 3.2f;

    [Header("Kick timing")]
    [Tooltip("Delay between kick event (animation) and when force is applied.")]
    [SerializeField] private float kickForceDelay = 0.06f;

    [Header("Cruise behavior")]
    [Tooltip("When (targetSpeed - vAlongIntent) <= this, we consider ourselves in cruise/maintenance mode.")]
    [SerializeField] private float cruiseEnterDv = 0.20f;

    [Tooltip("Minimum kick rate during cruise (helps avoid tractor-like stop-go).")]
    [SerializeField] private float cruiseKickRateMinSwim = 2.2f;

    [Tooltip("Minimum kick rate during cruise in Sprint.")]
    [SerializeField] private float cruiseKickRateMinSprint = 3.0f;

    [Tooltip("Smallest impulse allowed for a maintenance kick.")]
    [SerializeField] private float cruiseImpulseMin = 0.08f;

    [Tooltip("If speed is above target by more than this, skip kicks and let water slow us down.")]
    [SerializeField] private float overSpeedDeadband = 0.06f;

    [Header("Fast stop")]
    [SerializeField] private float idleBrakeAccel = 12.0f;
    [SerializeField] private float stopSpeedEpsilon = 0.10f;

    [Header("Facing (root yaw via Rigidbody)")]
    [Tooltip("How fast the root yaws toward the intent direction. 0 disables yaw follow.")]
    [SerializeField] private float faceSmoothing = 20f;

    [Header("Facing delay (optional)")]
    [Tooltip("Delay (seconds) applied to facing target direction. 0 = no delay.")]
    [SerializeField] private float faceDelaySeconds = 0.08f;

    [Tooltip("Ring buffer capacity for delayed facing samples. Larger = safer for big delays.")]
    [SerializeField] private int faceDelayBufferSize = 96;

    [Header("Turn slow down (reduce speed while turning)")]
    [Tooltip("Below this angle (deg), do not slow down.")]
    [SerializeField] private float turnSlowdownStartAngle = 15f;

    [Tooltip("At/above this angle (deg), slow down reaches maximum.")]
    [SerializeField] private float turnSlowdownMaxAngle = 90f;

    [Tooltip("Minimum speed multiplier when turning hard (0..1).")]
    [Range(0f, 1f)]
    [SerializeField] private float minTurnSpeedFactor = 0.35f;

    [Tooltip("How quickly the speed multiplier reacts (bigger = snappier).")]
    [SerializeField] private float turnSlowdownSmoothing = 10f;

    [Header("Input smoothing (screen space)")]
    [SerializeField] private float inputSmoothing = 18f;

    [Header("Carrot / guide (for IK)")]
    [SerializeField] private float carrotRadiusAim = 1.5f;
    [SerializeField] private float carrotRadiusSwim = 2.2f;
    [SerializeField] private float carrotRadiusSprint = 3.0f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoVelScale = 1.0f;
    [SerializeField] private float gizmoAccelScale = 0.25f;
    [SerializeField] private float gizmoIntentScale = 1.0f;

    // ---------------- Runtime input ----------------
    private bool isDragging;
    private Vector2 dirScreen;
    private float radiusPx;
    private Vector2 dirScreenSm;
    private float radiusPxSm;

    private MoveZone zone = MoveZone.None;

    // Intent/telemetry
    private Vector3 worldDir = Vector3.forward; // unit on XZ
    private float targetSpeed;
    private Vector3 carrotWorld;
    private Vector3 centerWorld;

    // Kick scheduler
    private float kickTimer;
    private float lastKickRate;
    private float lastKickImpulse;
    private readonly List<PendingKick> pendingKicks = new List<PendingKick>(8);

    private struct PendingKick
    {
        public float timeRemaining;
        public Vector3 dir;
        public float impulse;
    }

    // Accel estimation (horizontal)
    private Vector3 prevVelH;
    private Vector3 accelH;

    // Along-intent deceleration estimator for cruise
    private float prevVProj;
    private float dvProjDtSmoothed;

    // Turn-speed coupling
    private float turnSpeedFactor = 1f;

    // Facing delay buffer
    private float[] faceTimeBuf;
    private Vector3[] faceDirBuf;
    private int faceBufHead;
    private int faceBufCount;

    // Optional propulsion override for Special/Transitions
    private bool propulsionOverride;
    private float overrideTargetSpeed;
    private float overrideKickRateMax;
    private float overrideImpulseMax;

    private float PlaneY => useFixedPlaneY
        ? fixedPlaneY
        : (rb != null ? rb.position.y : transform.position.y);

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
            // Keep on water plane + prevent roll/pitch.
            rb.constraints |= RigidbodyConstraints.FreezePositionY
                           | RigidbodyConstraints.FreezeRotationX
                           | RigidbodyConstraints.FreezeRotationZ;
            // Ensure yaw is NOT frozen.
            rb.constraints &= ~RigidbodyConstraints.FreezeRotationY;
        }

        prevVelH = Vector3.zero;
        accelH = Vector3.zero;
        prevVProj = 0f;
        dvProjDtSmoothed = 0f;

        InitFacingDelayBuffer();
    }

    private void Update()
    {
        HandleInput();
        UpdateIntentAndCarrot();
    }

    private void FixedUpdate()
    {
        ProcessPendingKicks();
        UpdateFacingRigidbody();
        UpdateTurnSpeedFactor();
        ApplyPulsePropulsion();
        ApplyIdleBrakeAndPlaneLock();
        UpdateAccelerationTelemetry();
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

            // Prevent old samples from influencing the next drag.
            ClearFacingDelayBuffer();
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
        else zone = MoveZone.Sprint;

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

        // Push facing samples every frame so FixedUpdate can query delayed direction.
        PushFacingDelaySample(isDragging ? worldDir : transform.forward);

        // Discrete target speed by zone (no ramp).
        if (!isDragging) targetSpeed = 0f;
        else
        {
            targetSpeed = zone switch
            {
                MoveZone.Aim => 0f,
                MoveZone.Swim => swimSpeed,
                MoveZone.Sprint => sprintSpeed,
                _ => 0f
            };
        }

        float carrotR = zone switch
        {
            MoveZone.Aim => carrotRadiusAim,
            MoveZone.Swim => carrotRadiusSwim,
            MoveZone.Sprint => carrotRadiusSprint,
            _ => carrotRadiusAim
        };

        carrotWorld = centerWorld + worldDir * carrotR;
        carrotWorld.y = PlaneY;
    }

    private Vector3 GetCenterWorld()
    {
        Vector3 pos = (rb != null) ? rb.position : transform.position;
        pos.y = PlaneY;
        return pos;
    }

    // ---------------- Facing (root yaw) ----------------

    private void UpdateFacingRigidbody()
    {
        if (rb == null) return;
        if (faceSmoothing <= 0f) return;
        if (!isDragging) return;

        Vector3 dir = GetDelayedFacingDir(worldDir);
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
        float k = 1f - Mathf.Exp(-faceSmoothing * Time.fixedDeltaTime);
        Quaternion next = Quaternion.Slerp(rb.rotation, target, k);
        rb.MoveRotation(next);
    }

    // ---------------- Turn-speed coupling ----------------

    private void UpdateTurnSpeedFactor()
    {
        float dt = Mathf.Max(1e-5f, Time.fixedDeltaTime);

        // No intent (or Aim) => recover to 1.
        if (!isDragging || targetSpeed <= 1e-4f)
        {
            float k0 = 1f - Mathf.Exp(-Mathf.Max(0f, turnSlowdownSmoothing) * dt);
            turnSpeedFactor = Mathf.Lerp(turnSpeedFactor, 1f, k0);
            return;
        }

        Vector3 flatForward = transform.forward;
        flatForward.y = 0f;
        Vector3 flatDir = worldDir;
        flatDir.y = 0f;

        if (flatForward.sqrMagnitude < 1e-6f || flatDir.sqrMagnitude < 1e-6f)
        {
            float kBad = 1f - Mathf.Exp(-Mathf.Max(0f, turnSlowdownSmoothing) * dt);
            turnSpeedFactor = Mathf.Lerp(turnSpeedFactor, 1f, kBad);
            return;
        }

        flatForward.Normalize();
        flatDir.Normalize();

        float angle = Vector3.Angle(flatForward, flatDir);
        float t = Mathf.InverseLerp(
            Mathf.Max(0f, turnSlowdownStartAngle),
            Mathf.Max(turnSlowdownStartAngle + 0.001f, turnSlowdownMaxAngle),
            angle
        );

        float targetFactor = Mathf.Lerp(1f, Mathf.Clamp01(minTurnSpeedFactor), t);
        float k = 1f - Mathf.Exp(-Mathf.Max(0f, turnSlowdownSmoothing) * dt);
        turnSpeedFactor = Mathf.Lerp(turnSpeedFactor, targetFactor, k);
    }

    // ---------------- Facing delay helpers ----------------

    private void InitFacingDelayBuffer()
    {
        int n = Mathf.Max(8, faceDelayBufferSize);
        faceTimeBuf = new float[n];
        faceDirBuf = new Vector3[n];
        faceBufHead = 0;
        faceBufCount = 0;
    }

    private void ClearFacingDelayBuffer()
    {
        faceBufHead = 0;
        faceBufCount = 0;
    }

    private void PushFacingDelaySample(Vector3 dir)
    {
        if (faceTimeBuf == null || faceDirBuf == null || faceTimeBuf.Length == 0)
            InitFacingDelayBuffer();

        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
        dir.Normalize();

        faceTimeBuf[faceBufHead] = Time.time;
        faceDirBuf[faceBufHead] = dir;

        faceBufHead = (faceBufHead + 1) % faceTimeBuf.Length;
        faceBufCount = Mathf.Min(faceBufCount + 1, faceTimeBuf.Length);
    }

    private Vector3 GetDelayedFacingDir(Vector3 fallback)
    {
        float delay = Mathf.Max(0f, faceDelaySeconds);
        if (delay <= 1e-5f || faceBufCount <= 0) return fallback;

        float targetTime = Time.time - delay;

        // Search backward from newest sample.
        int newestIndex = (faceBufHead - 1 + faceTimeBuf.Length) % faceTimeBuf.Length;
        int idx = newestIndex;

        for (int i = 0; i < faceBufCount; i++)
        {
            float t = faceTimeBuf[idx];
            if (t <= targetTime)
                return faceDirBuf[idx];

            idx = (idx - 1 + faceTimeBuf.Length) % faceTimeBuf.Length;
        }

        // Not enough history yet -> return oldest we have (max available delay).
        int oldestIndex = (faceBufHead - faceBufCount + faceTimeBuf.Length) % faceTimeBuf.Length;
        return faceDirBuf[oldestIndex];
    }

    // ---------------- Propulsion ----------------

    private void ApplyPulsePropulsion()
    {
        if (rb == null) return;

        // Estimate along-intent acceleration/deceleration for cruise.
        float dt = Mathf.Max(1e-5f, Time.fixedDeltaTime);
        float vProj = Vector3.Dot(GetVelocity(), worldDir);
        float dvProjDt = (vProj - prevVProj) / dt;
        prevVProj = vProj;
        // Smooth a bit so we don't chase noise.
        float smoothK = 1f - Mathf.Exp(-10f * dt);
        dvProjDtSmoothed = Mathf.Lerp(dvProjDtSmoothed, dvProjDt, smoothK);

        // No intent or Aim => no kicks.
        float desiredTarget = propulsionOverride ? overrideTargetSpeed : targetSpeed;

        // Reduce requested speed while turning (more turn = more slow down).
        // This keeps "high speed + instant sharp yaw" from feeling unnatural.
        desiredTarget *= Mathf.Clamp01(turnSpeedFactor);
        if (!isDragging || desiredTarget <= 1e-4f)
        {
            lastKickRate = 0f;
            lastKickImpulse = 0f;
            // let timer decay toward 0 so next kick doesn't happen instantly
            kickTimer = Mathf.Max(0f, kickTimer - dt);
            return;
        }

        bool inSprint = (zone == MoveZone.Sprint);

        float rateMax = propulsionOverride ? overrideKickRateMax : (inSprint ? kickRateMaxSprint : kickRateMaxSwim);
        float impMax = propulsionOverride ? overrideImpulseMax : (inSprint ? impulseMaxSprint : impulseMaxSwim);
        rateMax = Mathf.Max(0f, rateMax);
        impMax = Mathf.Max(0f, impMax);
        if (rateMax <= 1e-4f || impMax <= 1e-4f)
        {
            lastKickRate = 0f;
            lastKickImpulse = 0f;
            kickTimer = Mathf.Max(0f, kickTimer - dt);
            return;
        }

        float dv = desiredTarget - vProj;

        // If we're above target by a bit, just coast (let water slow us down).
        if (dv < -Mathf.Max(0f, overSpeedDeadband))
        {
            lastKickRate = 0f;
            lastKickImpulse = 0f;
            kickTimer = Mathf.Max(0f, kickTimer - dt);
            return;
        }

        float mass = Mathf.Max(0.01f, rb.mass);

        // Decide whether we're in cruise.
        bool inCruise = dv <= Mathf.Max(0.02f, cruiseEnterDv);

        float desiredRate;
        float desiredImpulse;

        if (!inCruise)
        {
            // ACCEL PHASE: close the gap toward target over the configured accel time-scale.
            float accelTime = inSprint ? sprintAccelTime : swimAccelTime;
            accelTime = Mathf.Max(0.05f, accelTime);

            float aReq = Mathf.Max(0f, dv) / accelTime;      // m/s^2
            float impulsePerSecondReq = mass * aReq;         // N*s per s

            // Prefer fairly frequent kicks in accel (still "kick-y" but not tractor).
            float accelRateMin = inSprint ? Mathf.Min(3.0f, rateMax) : Mathf.Min(2.4f, rateMax);

            // Choose rate based on how hard we need to work.
            // If we need a lot of impulse/s, push toward rateMax.
            float work01 = Mathf.Clamp01(impulsePerSecondReq / Mathf.Max(1e-6f, rateMax * impMax));
            desiredRate = Mathf.Lerp(accelRateMin, rateMax, Mathf.Sqrt(work01));
            desiredRate = Mathf.Clamp(desiredRate, accelRateMin, rateMax);

            // Choose impulse to meet impulse/s budget at that rate.
            desiredImpulse = impulsePerSecondReq / Mathf.Max(1e-3f, desiredRate);
            desiredImpulse = Mathf.Clamp(desiredImpulse, cruiseImpulseMin, impMax);

            // Saturation: if even at max rate and max impulse we can't meet demand, we just saturate.
            if (impulsePerSecondReq > rateMax * impMax)
            {
                desiredRate = rateMax;
                desiredImpulse = impMax;
            }
        }
        else
        {
            // CRUISE PHASE: estimate how much impulse/s is needed to cancel the observed deceleration
            // along the intent direction (drag + current).
            // If dvProjDtSmoothed is negative, we are losing speed along intent.
            float aNeed = Mathf.Max(0f, -dvProjDtSmoothed);
            float impulsePerSecondNeed = mass * aNeed;

            float rateMin = inSprint ? cruiseKickRateMinSprint : cruiseKickRateMinSwim;
            rateMin = Mathf.Clamp(rateMin, 0.1f, rateMax);

            if (impulsePerSecondNeed <= 1e-4f)
            {
                // No observed decel: we can coast.
                // Keep a gentle periodic kick only if we're slightly below target.
                if (dv > 0.02f)
                {
                    desiredRate = rateMin;
                    desiredImpulse = Mathf.Clamp((mass * dv) * 0.15f, cruiseImpulseMin, impMax);
                }
                else
                {
                    desiredRate = 0f;
                    desiredImpulse = 0f;
                }
            }
            else
            {
                // Prefer to maintain at least rateMin for smoothness.
                float impulseAtRateMin = impulsePerSecondNeed / Mathf.Max(1e-3f, rateMin);

                if (impulseAtRateMin <= impMax)
                {
                    desiredRate = rateMin;
                    desiredImpulse = Mathf.Clamp(impulseAtRateMin, cruiseImpulseMin, impMax);
                }
                else
                {
                    // Need more than impMax at rateMin => raise rate until impulse <= impMax.
                    float rateNeededAtImpMax = impulsePerSecondNeed / Mathf.Max(1e-3f, impMax);
                    if (rateNeededAtImpMax <= rateMax)
                    {
                        desiredRate = Mathf.Max(rateMin, rateNeededAtImpMax);
                        desiredImpulse = impMax;
                    }
                    else
                    {
                        // Even at max rate and max impulse we can't maintain. Saturate and allow slowdown.
                        desiredRate = rateMax;
                        desiredImpulse = impMax;
                    }
                }

                // If we are still below target a bit, add a tiny proportional term.
                if (dv > 0.02f)
                {
                    float boost = Mathf.Clamp((mass * dv) * 0.08f, 0f, impMax - desiredImpulse);
                    desiredImpulse = Mathf.Clamp(desiredImpulse + boost, cruiseImpulseMin, impMax);
                }
            }
        }

        lastKickRate = desiredRate;
        lastKickImpulse = desiredImpulse;

        // Schedule kicks.
        if (desiredRate <= 1e-4f || desiredImpulse <= 1e-5f)
        {
            kickTimer = Mathf.Max(0f, kickTimer - dt);
            return;
        }

        kickTimer -= dt;
        int guard = 0;
        float interval = 1f / Mathf.Max(0.05f, desiredRate);

        while (kickTimer <= 0f && guard < 3)
        {
            // Fire animation immediately but defer the physical force for better sync.
            OnKickImpulse?.Invoke();
            OnKickImpulseDetailed?.Invoke(desiredImpulse, desiredRate);

            pendingKicks.Add(new PendingKick
            {
                timeRemaining = Mathf.Max(0f, kickForceDelay),
                dir = worldDir,
                impulse = desiredImpulse
            });

            // slight timing jitter keeps it from feeling robotic
            // Explicitly use Unity's RNG to avoid ambiguity with System.Random
            float jitter = UnityEngine.Random.Range(0.92f, 1.08f);
            kickTimer += interval * jitter;
            guard++;
        }
    }

    private void ProcessPendingKicks()
    {
        if (pendingKicks.Count == 0 || rb == null) return;

        float dt = Mathf.Max(1e-5f, Time.fixedDeltaTime);
        for (int i = pendingKicks.Count - 1; i >= 0; i--)
        {
            PendingKick pk = pendingKicks[i];
            pk.timeRemaining -= dt;
            if (pk.timeRemaining <= 0f)
            {
                rb.AddForce(pk.dir * pk.impulse, ForceMode.Impulse);
                pendingKicks.RemoveAt(i);
            }
            else
            {
                pendingKicks[i] = pk;
            }
        }
    }

    private void ApplyIdleBrakeAndPlaneLock()
    {
        if (rb == null) return;

        // Lock plane Y.
        if (useFixedPlaneY)
        {
            Vector3 vel = rb.velocity;
            if (Mathf.Abs(vel.y) > 1e-4f) vel.y = 0f;
            rb.velocity = vel;

            Vector3 pos = rb.position;
            if (Mathf.Abs(pos.y - fixedPlaneY) > 1e-4f)
            {
                pos.y = fixedPlaneY;
                rb.MovePosition(pos);
            }
        }

        // Fast stop when no intent.
        bool hasIntent = isDragging && targetSpeed > 1e-4f;
        if (hasIntent) return;

        Vector3 v = GetVelocity();
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

    private void UpdateAccelerationTelemetry()
    {
        if (rb == null)
        {
            accelH = Vector3.zero;
            prevVelH = Vector3.zero;
            return;
        }

        Vector3 v = rb.velocity;
        v.y = 0f;

        float dt = Mathf.Max(1e-5f, Time.fixedDeltaTime);
        accelH = (v - prevVelH) / dt;
        prevVelH = v;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        Vector3 origin = Application.isPlaying ? GetCenterWorld() : transform.position;
        origin.y = Application.isPlaying ? PlaneY : origin.y;

        // Intent
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 1f);
        Gizmos.DrawLine(origin, origin + worldDir * gizmoIntentScale);

        // Carrot
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(origin, carrotWorld);
        Gizmos.DrawSphere(carrotWorld, 0.06f);

        if (rb != null)
        {
            // Velocity
            Gizmos.color = Color.green;
            Vector3 hv = rb.velocity; hv.y = 0f;
            Gizmos.DrawLine(origin, origin + hv * gizmoVelScale);

            // Acceleration
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 1f);
            Gizmos.DrawLine(origin, origin + accelH * gizmoAccelScale);
        }
    }

    // ---------------- Public API (used by other scripts) ----------------

    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;

    public Vector3 GetCarrotWorld() => carrotWorld;
    public Vector3 GetWorldDir() => worldDir;

    public Vector3 GetVelocity()
    {
        if (rb == null) return Vector3.zero;
        Vector3 v = rb.velocity;
        v.y = 0f;
        return v;
    }

    public Vector3 GetAcceleration()
    {
        Vector3 a = accelH;
        a.y = 0f;
        return a;
    }

    public float GetSpeed() => GetVelocity().magnitude;

    public float GetSpeedAlongIntent()
    {
        Vector3 v = GetVelocity();
        return Vector3.Dot(v, worldDir);
    }

    public float GetTargetSpeed() => targetSpeed;
    public float GetSwimSpeed() => swimSpeed;
    public float GetSprintSpeed() => sprintSpeed;

    // For debugging / telemetry
    public float GetLastKickRate() => lastKickRate;
    public float GetLastKickImpulse() => lastKickImpulse;

    /// <summary>
    /// Approximate along-intent deceleration (m/s^2) observed at runtime. Positive means slowing down.
    /// </summary>
    public float GetObservedDecelAlongIntent() => Mathf.Max(0f, -dvProjDtSmoothed);

    // ---------------- Compatibility hooks for state controller / specials ----------------

    /// <summary>
    /// Optional override for transitions/special states.
    /// - targetSpeedOverride: if >= 0 uses that instead of computed targetSpeed.
    /// - kickRateMaxOverride / impulseMaxOverride: if > 0 clamp maxima.
    /// </summary>
    public void SetPropulsionOverride(float targetSpeedOverride, float kickRateMaxOverride, float impulseMaxOverride)
    {
        propulsionOverride = true;
        overrideTargetSpeed = Mathf.Max(0f, targetSpeedOverride);
        overrideKickRateMax = Mathf.Max(0f, kickRateMaxOverride);
        overrideImpulseMax = Mathf.Max(0f, impulseMaxOverride);
    }

    public void ClearPropulsionOverride()
    {
        propulsionOverride = false;
        overrideTargetSpeed = 0f;
        overrideKickRateMax = 0f;
        overrideImpulseMax = 0f;
    }
}
