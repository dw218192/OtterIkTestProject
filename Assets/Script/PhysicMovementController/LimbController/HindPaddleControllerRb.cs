// HindLegKickControllerNeoRB.cs
using UnityEngine;

/// <summary>
/// Hind-leg kick controller (Rigidbody version, event-driven, NO event queue).
///
/// Moving propulsion:
///   - Triggered strictly by MovementControllerRB.OnKickImpulseDetailed(impulse, rateHz).
///   - No local cadence timer; no event queue.
///   - If a kick is already running, we boost strength (max), NOT restart.
///
/// Idle turning:
///   - When not translating, cadence derived from measured yaw speed (deg/s) computed from rotation delta
///     (NOT rb.angularVelocity).
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(250)]
public class HindLegKickControllerNeoRB : MonoBehaviour
{
    [System.Serializable]
    public class Leg
    {
        public string name = "HindLeg";

        [Header("Rig references")]
        public Transform ikTarget;
        public Transform legRootBone;

        [Header("Rest pose")]
        public Vector3 restLocalPos;
        public bool autoUpdateRestInEditMode = true;

        [Header("Anti-robot delay")]
        [Range(0f, 0.25f)]
        public float randomDelayMax = 0.08f;

        // Runtime
        [HideInInspector] public bool pendingKick;
        [HideInInspector] public float delayTimer;
        [HideInInspector] public bool cachedIsInner;
        [HideInInspector] public float phase01;
        [HideInInspector] public float phaseOffset01;
        [HideInInspector] public float strength01;
        [HideInInspector] public bool restInitialized;
    }

    [Header("Data source")]
    public MovementControllerRB movement;
    public Rigidbody rb;

    [Header("Space / Spine")]
    public Transform rootSpace;
    public Transform[] spineChain;

    [Header("Spine tangent conventions")]
    public bool invertSpineTangent = false;
    public bool invertOmegaTurnSign = false;

    [Header("Cooldown mode")]
    public bool shareCooldownWhenTranslating = true;
    public float translateSpeedForSharedCooldown = 0.05f;

    [Header("Idle-turn cadence (measured yaw speed)")]
    public bool useOmegaCadenceWhenIdleTurn = true;

    [Tooltip("Below this abs yaw speed (deg/s), no in-place kick cadence.")]
    public float omegaTriggerDeg = 20f;

    [Tooltip("At/above this abs yaw speed (deg/s), omega-driven cadence and strength reach max.")]
    public float omegaForFullDemandDeg = 180f;

    [Tooltip("Kick rate at omegaTriggerDeg (Hz).")]
    public float omegaKickRateMin = 1.2f;

    [Tooltip("Kick rate at omegaForFullDemandDeg (Hz).")]
    public float omegaKickRateMax = 3.8f;

    [Tooltip("Strength floor during omega-driven cadence.")]
    [Range(0f, 1f)]
    public float omegaStrengthFloor = 0.10f;

    [Header("Yaw speed measurement (Scheme B)")]
    [Tooltip("If true, use rb.rotation to measure yaw; otherwise use rootSpace.rotation.")]
    public bool measureYawFromRigidbodyRotation = true;

    [Tooltip("Exponential smoothing for measured yaw speed (deg/s). Larger = snappier, smaller = smoother.")]
    [Range(0f, 40f)]
    public float yawSpeedSmoothing = 18f;

    [Tooltip("Ignore tiny yaw jitter below this absolute deg/s.")]
    public float yawJitterDeadzoneDeg = 2.5f;

    [Header("Propulsion strength mapping (impulse-driven)")]
    [Range(0f, 1f)] public float propulsionStrengthFloor = 0.15f;
    public float impulseMaxSwim = 2.1f;
    public float impulseMaxSprint = 3.2f;

    [Header("Interrupt / gating")]
    public bool interruptKickWhenRootStopped = true;
    public float stoppedSpeedEpsilon = 0.02f;

    [Tooltip("Consider root stopped when abs(yaw speed) is below this (deg/s).")]
    public float stoppedOmegaDegEpsilon = 6f;

    [Range(1f, 12f)] public float stopReturnLerpBoost = 4f;

    [Tooltip("If hip yaw direction flips while a kick is running, allow interrupt and restart.")]
    public bool interruptOnTurnDirectionFlip = true;

    [Tooltip("Ignore propulsion events when considered idle (prevents 'ghost kicks').")]
    public bool ignorePropulsionEventsWhenIdle = true;

    public float minSpeedToConsiderMoving = 0.05f;

    [Header("Delay re-check")]
    public bool recheckBeforeKickAfterDelay = true;

    [Header("Kick timing")]
    public float baseCycleTime = 0.55f;
    public float minCycleTime = 0.25f;

    [Header("Desync noise")]
    public bool enablePhaseJitter = true;
    [Range(0f, 0.15f)] public float phaseJitterMax01 = 0.04f;

    [Header("Ellipse design")]
    public float ellipseForwardRadius = 0.18f;
    public float ellipseSecondaryRadius = 0.10f;
    public float amplitudeMultiplierAtFullStrength = 1.8f;

    [Header("Rest on trajectory")]
    [Range(0f, 360f)]
    public float restAngleDeg = 110f;

    [Header("Ellipse plane tilt")]
    [Range(0f, 90f)]
    public float ellipsePlaneTiltDeg = 65f;
    public bool tiltOutward = true;

    [Header("Inner / Outer tuning")]
    [Range(0f, 90f)] public float outerTangentAngleOffsetDeg = 45f;
    [Range(0f, 90f)] public float innerTangentAngleOffsetDeg = 15f;
    [Range(0f, 0.5f)] public float outerLateralOffsetMeters = 0.08f;
    [Range(0f, 0.5f)] public float innerLateralOffsetMeters = 0.02f;

    [Header("Demand-based amplitude shaping")]
    [Range(0f, 1f)] public float innerNormalSteerStrength01 = 0.75f;

    [Tooltip("Measured yaw speed deadzone for demand mapping (deg/s).")]
    public float omegaYawDeadzoneDeg = 8f;

    [Tooltip("Measured yaw speed magnitude for full turning demand (deg/s).")]
    public float omegaYawForFullDemandDeg = 120f;

    public float forwardSpeedForFullBackStroke = 0.35f;
    [Range(0f, 1f)] public float minBackStrokeScaleNoForward = 0.18f;

    [Range(0f, 1f)] public float sideScaleAtNoTurn = 0.25f;
    [Range(0f, 2f)] public float sideScaleAtFullTurn = 1.0f;

    [Range(0.5f, 2f)] public float innerSideBoost = 1.25f;
    [Range(0f, 1f)] public float innerBackStrokeMultiplier = 0.65f;

    public float bendAngleForFullTurnDeg = 25f;

    [Header("Vertical arc")]
    public AnimationCurve verticalArc = AnimationCurve.EaseInOut(0, 0, 1, 1);
    public float verticalArcAmplitude = 0.03f;

    [Header("Smoothing")]
    public float targetPosLerp = 16f;

    [Header("Trajectory direction")]
    public bool invertTrajectoryDirection = false;

    [Header("Visualization")]
    public bool drawGizmos = true;
    public bool drawTrajectoryContour = true;
    [Range(8, 200)] public int gizmoEllipseSegments = 50;

    public bool gizmoPreviewEnabled = true;
    [Range(0f, 1f)] public float gizmoPreviewPhase01 = 0f;
    public bool drawPreviewMarkerOnTrajectory = true;
    [Range(0.005f, 0.08f)] public float previewMarkerRadius = 0.025f;
    public Color previewMarkerColor = new Color(1f, 0.55f, 0f, 1f);
    public bool drawRootAxes = true;
    public float gizmoAxisScale = 0.25f;

    [Header("Hind legs")]
    public Leg leftLeg = new Leg { name = "LeftHindLeg" };
    public Leg rightLeg = new Leg { name = "RightHindLeg" };

    // Cooldown timers
    private float _sinceLastKickShared = 999f;
    private float _sinceLastKickLeft = 999f;
    private float _sinceLastKickRight = 999f;
    private bool _sharedKickInProgress;

    // Idle-turn cadence scheduler
    private float _omegaCadenceTimer;

    // Telemetry
    private Vector3 _usedVelLocal;

    // Scheme B: measured yaw speed (deg/s)
    private float _yawSpeedDegPerSec;      // smoothed
    private float _prevYawDeg;             // last yaw sample
    private bool _yawInit;

    private float _prevYawSpeedDegPerSec; // for direction flip detection
    private float _turnDemand01;           // 0..1

    private void Reset()
    {
        rootSpace = transform;
        movement = GetComponentInParent<MovementControllerRB>();
        rb = GetComponentInParent<Rigidbody>();
    }

    private void OnEnable()
    {
        if (rootSpace == null) rootSpace = transform;
        if (movement == null) movement = GetComponentInParent<MovementControllerRB>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();

        SubscribeMovementEvents();

        _sinceLastKickShared = 999f;
        _sinceLastKickLeft = 999f;
        _sinceLastKickRight = 999f;
        _sharedKickInProgress = false;

        _omegaCadenceTimer = 0f;

        _yawSpeedDegPerSec = 0f;
        _prevYawDeg = 0f;
        _yawInit = false;
        _prevYawSpeedDegPerSec = 0f;
        _turnDemand01 = 0f;

        SyncRestIfNeeded(force: false);
    }

    private void OnDisable()
    {
        UnsubscribeMovementEvents();
    }

    private void Start()
    {
        if (rootSpace == null) rootSpace = transform;
        if (movement == null) movement = GetComponentInParent<MovementControllerRB>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();

        SubscribeMovementEvents();

        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);

        _sinceLastKickShared = 999f;
        _sinceLastKickLeft = 999f;
        _sinceLastKickRight = 999f;
        _sharedKickInProgress = false;

        _omegaCadenceTimer = 0f;
    }

    private void SubscribeMovementEvents()
    {
        if (movement == null) return;
        movement.OnKickImpulseDetailed -= OnMovementKickImpulseDetailed;
        movement.OnKickImpulseDetailed += OnMovementKickImpulseDetailed;
    }

    private void UnsubscribeMovementEvents()
    {
        if (movement == null) return;
        movement.OnKickImpulseDetailed -= OnMovementKickImpulseDetailed;
    }

    /// <summary>
    /// Strict propulsion trigger. NO queue.
    /// </summary>
    private void OnMovementKickImpulseDetailed(float impulse, float rateHz)
    {
        if (!Application.isPlaying) return;

        // Optional idle gate: if we're basically stopped (and not dragging), ignore propulsion events
        if (ignorePropulsionEventsWhenIdle)
        {
            Vector3 v = GetRootVelocity();
            if (v.magnitude < minSpeedToConsiderMoving && (movement == null || !movement.IsDragging()))
                return;
        }

        bool translating = GetRootVelocity().magnitude >= translateSpeedForSharedCooldown;
        bool useSharedCooldown = shareCooldownWhenTranslating && translating;

        if (interruptKickWhenRootStopped && IsRootStopped())
            return;

        float strength01 = ComputePropulsionStrength01(impulse);

        if (interruptOnTurnDirectionFlip && IsTurnDirectionFlipped())
        {
            if (leftLeg.pendingKick || rightLeg.pendingKick)
            {
                CancelLeg(leftLeg);
                CancelLeg(rightLeg);
                _sharedKickInProgress = false;
            }
        }

        TriggerOrBoost(strength01, useSharedCooldown);
    }

    private bool IsTurnDirectionFlipped()
    {
        float a = _prevYawSpeedDegPerSec;
        float b = _yawSpeedDegPerSec;

        if (Mathf.Abs(a) < yawJitterDeadzoneDeg || Mathf.Abs(b) < yawJitterDeadzoneDeg) return false;
        return Mathf.Sign(a) != Mathf.Sign(b);
    }

    private bool IsRootStopped()
    {
        Vector3 v = GetRootVelocity();
        float wDeg = Mathf.Abs(_yawSpeedDegPerSec);
        return (v.magnitude <= stoppedSpeedEpsilon) && (wDeg <= stoppedOmegaDegEpsilon);
    }

    private void OnValidate()
    {
        if (rootSpace == null) rootSpace = transform;
        SyncRestIfNeeded(force: true);
    }

    private void SyncRestIfNeeded(bool force)
    {
        SyncLegRest(leftLeg, force);
        SyncLegRest(rightLeg, force);
    }

    private void SyncLegRest(Leg leg, bool force)
    {
        if (leg == null || rootSpace == null || leg.ikTarget == null) return;

        if (!Application.isPlaying)
        {
            if (!leg.autoUpdateRestInEditMode && leg.restInitialized && !force) return;
            leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
            leg.restInitialized = true;
        }
    }

    private void ForceInitRest(Leg leg)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
        leg.restInitialized = true;
    }

    private void Update()
    {
        if (!Application.isPlaying)
        {
            SyncRestIfNeeded(force: false);
            return;
        }

        if (rootSpace == null) return;

        float dt = Time.deltaTime;

        // --- Telemetry ---
        Vector3 vRoot = GetRootVelocity();
        float speed = vRoot.magnitude;

        _prevYawSpeedDegPerSec = _yawSpeedDegPerSec;
        UpdateMeasuredYawSpeed(dt); // <--- Scheme B fix

        _usedVelLocal = rootSpace.InverseTransformDirection(vRoot);

        // turning demand (0..1) based on measured yaw speed
        float absYawDeg = Mathf.Abs(_yawSpeedDegPerSec);
        _turnDemand01 = (absYawDeg <= omegaYawDeadzoneDeg)
            ? 0f
            : Mathf.Clamp01((absYawDeg - omegaYawDeadzoneDeg) / Mathf.Max(omegaYawForFullDemandDeg - omegaYawDeadzoneDeg, 1e-4f));

        // Hard stop interrupt in Update loop (for non-event situations)
        if (interruptKickWhenRootStopped && (movement == null || !movement.IsDragging()))
        {
            if (IsRootStopped())
            {
                CancelLeg(leftLeg);
                CancelLeg(rightLeg);
                _sharedKickInProgress = false;

                ReturnToRestFast(leftLeg, dt, stopReturnLerpBoost);
                ReturnToRestFast(rightLeg, dt, stopReturnLerpBoost);
                return;
            }
        }

        bool translating = speed >= translateSpeedForSharedCooldown;
        bool useSharedCooldown = shareCooldownWhenTranslating && translating;

        // Cooldowns
        if (useSharedCooldown)
        {
            if (!_sharedKickInProgress) _sinceLastKickShared += dt;
        }
        else
        {
            if (!leftLeg.pendingKick) _sinceLastKickLeft += dt;
            if (!rightLeg.pendingKick) _sinceLastKickRight += dt;
        }

        // Idle-turn cadence (only when not translating)
        if (!translating && useOmegaCadenceWhenIdleTurn)
        {
            float wDeg = Mathf.Abs(_yawSpeedDegPerSec);
            if (wDeg >= omegaTriggerDeg)
            {
                float t = Mathf.InverseLerp(omegaTriggerDeg, Mathf.Max(omegaTriggerDeg + 0.01f, omegaForFullDemandDeg), wDeg);
                float rate = Mathf.Lerp(omegaKickRateMin, omegaKickRateMax, Mathf.Sqrt(Mathf.Clamp01(t)));
                float strength01 = Mathf.Clamp01(Mathf.Lerp(omegaStrengthFloor, 1f, t));
                TickOmegaCadenceAndTrigger(rate, strength01, useSharedCooldown, dt);
            }
        }

        UpdateLeg(leftLeg, dt);
        UpdateLeg(rightLeg, dt);

        if (useSharedCooldown && _sharedKickInProgress && !leftLeg.pendingKick && !rightLeg.pendingKick)
        {
            _sinceLastKickShared = 0f;
            _sharedKickInProgress = false;
        }
    }

    /// <summary>
    /// Scheme B: compute yaw speed from rotation delta (deg/s), then smooth.
    /// Works whether rotation is driven by physics, MoveRotation, or direct transform changes.
    /// </summary>
    private void UpdateMeasuredYawSpeed(float dt)
    {
        dt = Mathf.Max(dt, 1e-4f);

        float yawNow = GetYawDegrees();
        if (!_yawInit)
        {
            _prevYawDeg = yawNow;
            _yawInit = true;
            _yawSpeedDegPerSec = 0f;
            return;
        }

        float deltaYaw = Mathf.DeltaAngle(_prevYawDeg, yawNow); // signed deg
        _prevYawDeg = yawNow;

        float raw = deltaYaw / dt; // deg/s

        if (Mathf.Abs(raw) < yawJitterDeadzoneDeg)
            raw = 0f;

        // exp smoothing
        float a = 1f - Mathf.Exp(-yawSpeedSmoothing * dt);
        _yawSpeedDegPerSec = Mathf.Lerp(_yawSpeedDegPerSec, raw, a);
    }

    private float GetYawDegrees()
    {
        Quaternion q;
        if (measureYawFromRigidbodyRotation && rb != null)
            q = rb.rotation;
        else
            q = (rootSpace != null ? rootSpace.rotation : transform.rotation);

        // yaw around world up (Y). Equivalent to eulerAngles.y for typical use.
        return q.eulerAngles.y;
    }

    private void TickOmegaCadenceAndTrigger(float desiredRateHz, float strength01, bool useSharedCooldown, float dt)
    {
        desiredRateHz = Mathf.Max(0f, desiredRateHz);
        if (desiredRateHz <= 1e-4f) return;

        float interval = 1f / Mathf.Max(0.05f, desiredRateHz);
        _omegaCadenceTimer -= dt;

        float jitter = Random.Range(0.92f, 1.08f);

        if (_omegaCadenceTimer <= 0f)
        {
            TriggerOrBoost(strength01, useSharedCooldown);
            _omegaCadenceTimer += interval * jitter;
        }
    }

    private void TriggerOrBoost(float strength01, bool useSharedCooldown)
    {
        if (useSharedCooldown)
        {
            if (!_sharedKickInProgress && !leftLeg.pendingKick && !rightLeg.pendingKick)
            {
                TriggerKick(strength01);
                _sharedKickInProgress = true;
            }
            else
            {
                BoostStrength(leftLeg, strength01);
                BoostStrength(rightLeg, strength01);
            }
        }
        else
        {
            if (!leftLeg.pendingKick) ArmLeg(leftLeg, strength01);
            else BoostStrength(leftLeg, strength01);

            if (!rightLeg.pendingKick) ArmLeg(rightLeg, strength01);
            else BoostStrength(rightLeg, strength01);
        }
    }

    private float ComputePropulsionStrength01(float impulse)
    {
        if (movement == null) return Mathf.Max(propulsionStrengthFloor, 0f);

        float maxI = impulseMaxSwim;
        if (movement.GetZone() == MovementControllerRB.MoveZone.Sprint) maxI = impulseMaxSprint;

        maxI = Mathf.Max(1e-4f, maxI);
        float s = Mathf.Clamp01(impulse / maxI);
        return Mathf.Max(propulsionStrengthFloor, s);
    }

    private Vector3 GetRootVelocity()
    {
        if (movement != null) return movement.GetVelocity();
        if (rb != null)
        {
            Vector3 v = rb.velocity;
            v.y = 0f;
            return v;
        }
        return Vector3.zero;
    }

    private void CancelLeg(Leg leg)
    {
        if (leg == null) return;
        leg.pendingKick = false;
        leg.delayTimer = 0f;
        leg.phase01 = 0f;
        leg.strength01 = 0f;
    }

    private void ReturnToRest(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        leg.ikTarget.position = Vector3.Lerp(leg.ikTarget.position, restWorld, 1f - Mathf.Exp(-targetPosLerp * dt));
    }

    private void ReturnToRestFast(Leg leg, float dt, float boost)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        float k = targetPosLerp * Mathf.Max(1f, boost);
        leg.ikTarget.position = Vector3.Lerp(leg.ikTarget.position, restWorld, 1f - Mathf.Exp(-k * dt));
    }

    private void TriggerKick(float strength01)
    {
        ArmLeg(leftLeg, strength01);
        ArmLeg(rightLeg, strength01);
    }

    private void ArmLeg(Leg leg, float strength01)
    {
        if (leg == null || leg.ikTarget == null) return;
        if (leg.pendingKick) return;

        leg.pendingKick = true;
        leg.phase01 = 0f;
        leg.strength01 = Mathf.Clamp01(strength01);
        leg.delayTimer = Random.Range(0f, leg.randomDelayMax);
        leg.phaseOffset01 = (enablePhaseJitter && phaseJitterMax01 > 0f)
            ? Random.Range(-phaseJitterMax01, phaseJitterMax01)
            : 0f;
    }

    private void BoostStrength(Leg leg, float strength01)
    {
        if (leg == null) return;
        leg.strength01 = Mathf.Max(leg.strength01, Mathf.Clamp01(strength01));
    }

    private void UpdateLeg(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;

        if (!leg.pendingKick)
        {
            ReturnToRest(leg, dt);
            return;
        }

        if (leg.delayTimer > 0f)
        {
            leg.delayTimer -= dt;
            ReturnToRest(leg, dt);

            if (recheckBeforeKickAfterDelay && leg.delayTimer <= 0f)
            {
                if (interruptKickWhenRootStopped && IsRootStopped())
                {
                    CancelLeg(leg);
                    ReturnToRestFast(leg, dt, stopReturnLerpBoost);
                    return;
                }
            }
            return;
        }

        float cycleTime = Mathf.Lerp(baseCycleTime, minCycleTime, leg.strength01);
        leg.phase01 += dt / Mathf.Max(cycleTime, 1e-4f);

        bool finishedThisFrame = false;
        if (leg.phase01 >= 1f)
        {
            leg.phase01 = 1f;
            leg.pendingKick = false;
            finishedThisFrame = true;
        }

        float phaseUsed = ApplyDirectionFlip(Mathf.Clamp01(leg.phase01 + leg.phaseOffset01));
        Vector3 localPos = ComputeKickLocalPosition(leg, phaseUsed, leg.strength01);
        Vector3 desiredWorld = rootSpace.TransformPoint(localPos);

        leg.ikTarget.position = Vector3.Lerp(
            leg.ikTarget.position,
            desiredWorld,
            1f - Mathf.Exp(-targetPosLerp * dt)
        );

        if (finishedThisFrame)
        {
            if (ReferenceEquals(leg, leftLeg)) _sinceLastKickLeft = 0f;
            else if (ReferenceEquals(leg, rightLeg)) _sinceLastKickRight = 0f;
        }
    }

    private float ApplyDirectionFlip(float phase01)
    {
        phase01 = Mathf.Clamp01(phase01);
        return invertTrajectoryDirection ? (1f - phase01) : phase01;
    }

    private float PhaseToThetaFromRest(float phase01)
    {
        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        return thetaRest + (phase01 * Mathf.PI * 2f);
    }

    private Vector3 ComputeKickLocalPosition(Leg leg, float phase01, float strength01)
    {
        Vector3 deltaW = ComputeTrajectoryDeltaWorld_FromRestPhase(leg, phase01, strength01);
        Vector3 deltaL = rootSpace.InverseTransformDirection(deltaW);

        float forwardSpeed = _usedVelLocal.z;
        float forwardDemand01 = Mathf.Clamp01(forwardSpeed / Mathf.Max(forwardSpeedForFullBackStroke, 1e-4f));
        float backStrokeScale = Mathf.Lerp(minBackStrokeScaleNoForward, 1f, forwardDemand01);

        float sideScale = Mathf.Lerp(sideScaleAtNoTurn, sideScaleAtFullTurn, _turnDemand01);

        if (leg.cachedIsInner && _turnDemand01 > 0f)
        {
            sideScale *= innerSideBoost;
            backStrokeScale *= innerBackStrokeMultiplier;
        }

        deltaL.x *= sideScale;
        if (deltaL.z < 0f) deltaL.z *= backStrokeScale;

        float y01 = (verticalArc != null) ? verticalArc.Evaluate(Mathf.Clamp01(phase01)) : phase01;
        float ySigned = (y01 - 0.5f) * 2f;
        float yAmp = verticalArcAmplitude * Mathf.Lerp(0.6f, 1f, strength01);

        return leg.restLocalPos + deltaL + (Vector3.up * (ySigned * yAmp));
    }

    private Vector3 ComputeTrajectoryDeltaWorld_FromRestPhase(Leg leg, float phase01, float strength01)
    {
        float ampMul = Mathf.Lerp(1f, amplitudeMultiplierAtFullStrength, strength01);
        float aF = ellipseForwardRadius * ampMul;
        float aS = ellipseSecondaryRadius * ampMul;

        GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out float spineTurnSign, out float turnAmount01);

        // Use measured yaw speed sign as turn sign (Scheme B)
        float turnSign = Mathf.Sign(_yawSpeedDegPerSec);
        if (invertOmegaTurnSign) turnSign *= -1f;
        if (Mathf.Abs(_yawSpeedDegPerSec) < yawJitterDeadzoneDeg) turnSign = 0f;

        float legSideSign = GetLegSideSign(leg, sideXZ);

        if (Mathf.Abs(turnSign) < 0.5f || turnAmount01 < 0.02f)
            turnAmount01 = 0f;

        bool isInnerLeg = (turnAmount01 > 0f) && (Mathf.Sign(legSideSign) == Mathf.Sign(turnSign));
        leg.cachedIsInner = isInnerLeg;
        bool isOuterLeg = (turnAmount01 > 0f) && !isInnerLeg;

        float biasScale = Mathf.Clamp01(turnAmount01);

        float outerAngle = Mathf.Abs(outerTangentAngleOffsetDeg) * biasScale;
        float innerAngle = Mathf.Abs(innerTangentAngleOffsetDeg) * biasScale;

        float angleSignedDeg = isOuterLeg ? (-outerAngle * turnSign) : (innerAngle * turnSign);
        Quaternion rotBias = Quaternion.AngleAxis(angleSignedDeg, Vector3.up);
        Vector3 kickAxisXZ = (rotBias * tangentXZ).normalized;

        if (isInnerLeg && _turnDemand01 > 0f && innerNormalSteerStrength01 > 1e-3f)
        {
            Vector3 innerNormal = (-turnSign * sideXZ).normalized;

            float forwardDemand01 = Mathf.Clamp01(_usedVelLocal.z / Mathf.Max(forwardSpeedForFullBackStroke, 1e-4f));
            float steerBlend = Mathf.Clamp01(_turnDemand01 * (1f - forwardDemand01) * innerNormalSteerStrength01);

            kickAxisXZ = Vector3.Slerp(kickAxisXZ, innerNormal, steerBlend).normalized;
        }

        Vector3 secondaryAxisBase = (sideXZ * legSideSign).normalized;
        float outwardFlip = tiltOutward ? 1f : -1f;
        float signedTiltDeg = ellipsePlaneTiltDeg * legSideSign * outwardFlip;
        Vector3 secondaryAxisTilted = Quaternion.AngleAxis(signedTiltDeg, tangentXZ) * secondaryAxisBase;

        float outerOffset = Mathf.Abs(outerLateralOffsetMeters) * biasScale;
        float innerOffset = Mathf.Abs(innerLateralOffsetMeters) * biasScale;
        float lateralOffsetSigned = isOuterLeg ? (-outerOffset * turnSign) : (innerOffset * turnSign);

        Vector3 E(float angRad)
        {
            float f = Mathf.Cos(angRad) * aF;
            float s = Mathf.Sin(angRad) * aS;
            return kickAxisXZ * f + secondaryAxisTilted * (s + lateralOffsetSigned);
        }

        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        float theta = PhaseToThetaFromRest(phase01);

        return E(theta) - E(thetaRest);
    }

    private Vector3 ComputeTrajectoryPointWorld(Leg leg, float phase01, float strength01)
    {
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        return restWorld + ComputeTrajectoryDeltaWorld_FromRestPhase(leg, phase01, strength01);
    }

    private float GetLegSideSign(Leg leg, Vector3 sideXZ)
    {
        float sign = +1f;
        if (leg != null && leg.legRootBone != null && rootSpace != null)
        {
            Vector3 toLeg = leg.legRootBone.position - rootSpace.position;
            toLeg.y = 0f;
            sign = (Vector3.Dot(toLeg, sideXZ) >= 0f) ? +1f : -1f;
        }
        return sign;
    }

    private void GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out float turnSign, out float turnAmount01)
    {
        Vector3 t0 = rootSpace != null ? rootSpace.forward : Vector3.forward;
        Vector3 t1 = t0;

        if (spineChain != null && spineChain.Length >= 3 &&
            spineChain[0] != null && spineChain[1] != null && spineChain[2] != null)
        {
            t0 = spineChain[1].position - spineChain[0].position;
            t1 = spineChain[2].position - spineChain[1].position;
        }
        else if (spineChain != null && spineChain.Length >= 2 &&
                 spineChain[0] != null && spineChain[1] != null)
        {
            t0 = spineChain[1].position - spineChain[0].position;
            t1 = t0;
        }

        t0.y = 0f;
        t1.y = 0f;

        if (t0.sqrMagnitude < 1e-6f) t0 = (rootSpace != null ? rootSpace.forward : Vector3.forward);
        if (t1.sqrMagnitude < 1e-6f) t1 = t0;

        Vector3 n0 = t0.normalized;
        Vector3 n1 = t1.normalized;

        tangentXZ = n0;
        if (invertSpineTangent) tangentXZ = -tangentXZ;

        sideXZ = Vector3.Cross(Vector3.up, tangentXZ).normalized;
        if (sideXZ.sqrMagnitude < 1e-6f)
            sideXZ = (rootSpace != null ? rootSpace.right : Vector3.right);

        float crossY = Vector3.Cross(n0, n1).y;
        turnSign = Mathf.Sign(crossY);

        float bendAngle = Vector3.Angle(n0, n1);
        turnAmount01 = Mathf.Clamp01(bendAngle / Mathf.Max(1e-3f, bendAngleForFullTurnDeg));

        if (turnAmount01 < 0.02f)
        {
            turnSign = 0f;
            turnAmount01 = 0f;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || rootSpace == null) return;

        if (!Application.isPlaying)
            SyncRestIfNeeded(force: false);

        if (drawRootAxes)
        {
            GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out _, out _);
            Vector3 origin = rootSpace.position;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, origin + tangentXZ * gizmoAxisScale);

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(origin, origin + sideXZ * gizmoAxisScale);
        }

        DrawLegGizmos(leftLeg);
        DrawLegGizmos(rightLeg);
    }

    private void DrawLegGizmos(Leg leg)
    {
        if (leg == null || leg.ikTarget == null) return;

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(leg.ikTarget.position, 0.02f);

        float previewStrength = 1f;

        if (drawTrajectoryContour)
        {
            Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
            Vector3 prev = ComputeTrajectoryPointWorld(leg, 0f, previewStrength);

            for (int i = 1; i <= gizmoEllipseSegments; i++)
            {
                float u = i / (float)gizmoEllipseSegments;
                Vector3 p = ComputeTrajectoryPointWorld(leg, u, previewStrength);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }

            Gizmos.DrawLine(prev, ComputeTrajectoryPointWorld(leg, 0f, previewStrength));
        }

        if (!Application.isPlaying && gizmoPreviewEnabled && drawPreviewMarkerOnTrajectory)
        {
            float phase = ApplyDirectionFlip(gizmoPreviewPhase01);
            Vector3 markerPos = ComputeTrajectoryPointWorld(leg, phase, previewStrength);

            Gizmos.color = previewMarkerColor;
            Gizmos.DrawSphere(markerPos, previewMarkerRadius);
        }
    }
#endif
}
