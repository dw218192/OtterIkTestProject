using UnityEngine;

/// <summary>
/// Hind-leg kick controller (no Rigidbody).
///
/// Core ideas:
/// - This script ONLY moves IK target transforms.
/// - Kick trigger uses point acceleration at hip: v_used = v_root + (omega x r) [Scheme B].
/// - Inner/Outer leg direction is defined relative to the SPINE CURVE tangent at hip on XZ:
///     tangentXZ: spine curve tangent (XZ)
///     sideXZ   : right-side normal to tangent on XZ (Vector3.Cross(up, tangent))
///     turnSign : sign of turning direction (recommended from omegaY for stability)
///     legSideSign: which side of sideXZ the leg root is on
///     innerLeg : legSideSign == turnSign
///     outerLeg : legSideSign != turnSign
///
/// Refinements included:
/// - Cooldown starts AFTER kick finishes.
/// - When root has meaningful translation -> shared cooldown (both legs start together).
/// - When rotating in place -> per-leg cooldown (each leg independent).
/// - Keep angular contribution for in-place turns, and smooth usedVel before differentiating.
/// - Direction & amplitude shaping:
///     (1) Inner leg can steer toward spine-curve INNER normal when turning without forward motion.
///     (2) Backward-stroke (negative local Z) is reduced when forward demand is low.
///     (3) Side stroke is boosted with turning demand; inner leg can get extra side boost + extra back reduction.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(250)]
public class HindLegKickControllerNeo : MonoBehaviour
{
    [System.Serializable]
    public class Leg
    {
        public string name = "HindLeg";

        [Header("Rig references")]
        [Tooltip("IK end-effector target. This script ONLY moves this Transform.")]
        public Transform ikTarget;

        [Tooltip("Hind-leg root bone Transform (used only to determine legSideSign).")]
        public Transform legRootBone;

        [Header("Rest pose")]
        [Tooltip("Rest position in rootSpace local coordinates. Auto-initialized from current IK target.")]
        public Vector3 restLocalPos;

        [Tooltip("If true, restLocalPos will be auto-updated in Edit Mode (recommended while tuning).")]
        public bool autoUpdateRestInEditMode = true;

        [Header("Anti-robot delay")]
        [Range(0f, 0.25f)]
        public float randomDelayMax = 0.08f;

        // Runtime
        [HideInInspector] public bool pendingKick;
        [HideInInspector] public float delayTimer;
        [HideInInspector] public bool cachedIsInner;
        [HideInInspector] public float phase01;
        [HideInInspector] public float strength01;
        [HideInInspector] public bool restInitialized;
    }

    [Header("Data source (root translation)")]
    public MovementController movement;

    [Header("Space / Spine")]
    public Transform rootSpace;
    public Transform[] spineChain;

    

    [Header("Spine tangent conventions")]
    [Tooltip("If your spineChain is ordered tail->head (or otherwise produces a tangent pointing backward), enable this to flip the computed tangent (fixes 180° kick direction and inner/outer swap).")]
    public bool invertSpineTangent = false;

    [Tooltip("If inner/outer still feels swapped when using omegaY as turn sign, enable to invert omega-derived turning sign.")]
    public bool invertOmegaTurnSign = false;
[Header("Cooldown mode")]
    [Tooltip("When true, both legs share one cooldown timer while the root has meaningful TRANSLATION. If the root is only rotating in place, legs cool down independently.")]
    public bool shareCooldownWhenTranslating = true;

    [Tooltip("Translation speed threshold (m/s) above which we treat the root as 'translating' and use the shared cooldown.")]
    public float translateSpeedForSharedCooldown = 0.05f;

    [Header("Trigger signal smoothing")]
    [Tooltip("Time constant (seconds) for smoothing usedVel BEFORE taking the derivative. Higher = less noisy accel trigger.")]
    public float usedVelSmoothingTau = 0.12f;

    [Header("Angular motion trigger (Scheme B)")]
    [Tooltip("If true, use v_point = v_root + (omega x r) to trigger kicks (handles root rotation without translation).")]
    public bool useAngularMotionForTrigger = true;

    [Tooltip("If true, only consider yaw (project omega onto world up). Recommended for top-down swimming.")]
    public bool useYawOnlyForAngularMotion = true;

    [Tooltip("Scale for the angular contribution (omega x r). 0 = disabled, 1 = physical scale.")]
    [Range(0f, 2f)]
    public float angularContributionWeight = 1.0f;

    [Tooltip("The point treated as the 'hip' for r = hip - pivot. If null, uses this.transform.")]
    public Transform hipTransform;

    [Tooltip("The rotation pivot used for r. If null, uses rootSpace (or this.transform if rootSpace is null).")]
    public Transform pivotTransform;

    [Header("Turn sign source")]
    [Tooltip(
        "CW/CCW stability fix:\n" +
        "- If true, turning SIGN comes from omegaY (root yaw angular velocity). This is stable and symmetric.\n" +
        "- Turning AMOUNT still comes from spine bend (turnAmount01).\n" +
        "If false, turning SIGN comes from spine curvature cross (can be asymmetric due to overshoot/chain order)."
    )]
    public bool useOmegaYawForTurnSign = true;

    [Header("Hind legs")]
    public Leg leftLeg = new Leg { name = "LeftHindLeg" };
    public Leg rightLeg = new Leg { name = "RightHindLeg" };

    [Header("Idle gating")]
    [Tooltip("If speed of the USED point velocity is below this and no kick is running, legs return to rest.")]
    public float minSpeedToAnimate = 0.05f;

    [Header("Kick trigger (acceleration)")]
    public float accelTrigger = 0.8f;
    public float accelForFullStrength = 4.0f;

    [Tooltip("Minimum interval (seconds) between kicks. Applied to shared cooldown OR per-leg cooldown depending on mode.")]
    public float minKickInterval = 0.25f;

    [Header("Kick timing")]
    public float baseCycleTime = 0.55f;
    public float minCycleTime = 0.25f;

    [Header("Ellipse design")]
    public float ellipseForwardRadius = 0.18f;
    public float ellipseSecondaryRadius = 0.10f;
    public float amplitudeMultiplierAtFullStrength = 1.8f;

    [Header("Rest on trajectory")]
    [Range(0f, 360f)]
    public float restAngleDeg = 110f;

    [Header("Ellipse plane tilt (mirrored per leg)")]
    [Range(0f, 90f)]
    public float ellipsePlaneTiltDeg = 65f;

    [Tooltip("If true, both legs cant OUTWARD (away from body midline). If false, they cant inward.")]
    public bool tiltOutward = true;

    [Header("Inner / Outer direction tuning (you requested TWO values each)")]
    [Tooltip("How much OUTER leg kick direction can rotate away from tangent (degrees).")]
    [Range(0f, 90f)]
    public float outerTangentAngleOffsetDeg = 45f;

    [Tooltip("How much INNER leg kick direction can rotate toward the inside (degrees).")]
    [Range(0f, 90f)]
    public float innerTangentAngleOffsetDeg = 15f;

    [Tooltip("Lateral offset along the leg secondary axis for OUTER leg (meters).")]
    [Range(0f, 0.5f)]
    public float outerLateralOffsetMeters = 0.08f;

    [Tooltip("Lateral offset along the leg secondary axis for INNER leg (meters).")]
    [Range(0f, 0.5f)]
    public float innerLateralOffsetMeters = 0.02f;

    [Header("Kick direction & demand-based amplitude")]
    [Tooltip("Inner leg can steer toward the spine-curve INNER normal when turning without forward motion.")]
    [Range(0f, 1f)]
    public float innerNormalSteerStrength01 = 0.75f;

    [Tooltip("OmegaY (rad/s) deadzone for turning detection / demand mapping.")]
    public float omegaYawDeadzone = 0.15f;

    [Tooltip("OmegaY magnitude (rad/s) for full turning demand.")]
    public float omegaYawForFullDemand = 2.4f;

    [Tooltip("Forward speed (m/s along rootSpace forward) for full backward-stroke amplitude.")]
    public float forwardSpeedForFullBackStroke = 0.35f;

    [Tooltip("Minimum scale applied to backward (negative Z) stroke when there is no forward demand.")]
    [Range(0f, 1f)]
    public float minBackStrokeScaleNoForward = 0.18f;

    [Tooltip("Side stroke scale when not turning.")]
    [Range(0f, 1f)]
    public float sideScaleAtNoTurn = 0.25f;

    [Tooltip("Side stroke scale at full turning demand.")]
    [Range(0f, 2f)]
    public float sideScaleAtFullTurn = 1.0f;

    [Tooltip("Extra multiplier for side stroke on INNER leg during turning.")]
    [Range(0.5f, 2f)]
    public float innerSideBoost = 1.25f;

    [Tooltip("Extra multiplier applied to backward stroke on INNER leg during turning (usually < 1).")]
    [Range(0f, 1f)]
    public float innerBackStrokeMultiplier = 0.65f;

    [Header("Turn amount mapping (strength of inner/outer separation)")]
    [Tooltip("Bend angle (degrees) at which turnAmount01 reaches 1. Smaller = stronger response.")]
    public float bendAngleForFullTurnDeg = 25f;

    [Header("Vertical arc (extra shaping)")]
    public AnimationCurve verticalArc = AnimationCurve.EaseInOut(0, 0, 1, 1);
    public float verticalArcAmplitude = 0.03f;

    [Header("Smoothing")]
    public float targetPosLerp = 16f;

    [Header("Trajectory direction")]
    public bool invertTrajectoryDirection = false;

    [Header("Visualization")]
    public bool drawGizmos = true;
    public bool drawTrajectoryContour = true;

    [Range(8, 200)]
    public int gizmoEllipseSegments = 50;

    public bool gizmoPreviewEnabled = true;

    [Tooltip("Edit-mode preview phase (0..1). Phase=0 is at REST (target rest point).")]
    [Range(0f, 1f)]
    public float gizmoPreviewPhase01 = 0f;

    public bool drawPreviewMarkerOnTrajectory = true;

    [Range(0.005f, 0.08f)]
    public float previewMarkerRadius = 0.025f;

    public Color previewMarkerColor = new Color(1f, 0.55f, 0f, 1f);

    public bool drawRootAxes = true;
    public float gizmoAxisScale = 0.25f;

    // Runtime caches
    private Quaternion _prevRootRot;
    private Vector3 _prevUsedVel;

    // Cooldown timers (seconds since last kick FINISHED)
    private float _sinceLastKickShared = 999f;
    private float _sinceLastKickLeft = 999f;
    private float _sinceLastKickRight = 999f;
    private bool _sharedKickInProgress;

    // Smoothed trigger signal
    private Vector3 _smoothedUsedVel;

    // Cached for turnSign usage.
    private float _lastOmegaY;

    // Cached for demand mapping
    private Vector3 _usedVelLocal;
    private float _turnDemand01;

    private void Reset()
    {
        rootSpace = transform;
        hipTransform = transform;
        pivotTransform = null;
        movement = GetComponentInParent<MovementController>();
    }

    private void OnEnable()
    {
        if (rootSpace == null) rootSpace = transform;
        if (hipTransform == null) hipTransform = transform;

        _prevRootRot = GetRootRotation();
        _prevUsedVel = Vector3.zero;
        _smoothedUsedVel = Vector3.zero;
        _lastOmegaY = 0f;
        _usedVelLocal = Vector3.zero;
        _turnDemand01 = 0f;

        _sinceLastKickShared = 999f;
        _sinceLastKickLeft = 999f;
        _sinceLastKickRight = 999f;
        _sharedKickInProgress = false;

        SyncRestIfNeeded(force: false);
    }

    private void Start()
    {
        if (rootSpace == null) rootSpace = transform;
        if (hipTransform == null) hipTransform = transform;

        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);

        _prevRootRot = GetRootRotation();
        _prevUsedVel = Vector3.zero;
        _smoothedUsedVel = Vector3.zero;
        _lastOmegaY = 0f;
        _usedVelLocal = Vector3.zero;
        _turnDemand01 = 0f;

        _sinceLastKickShared = 999f;
        _sinceLastKickLeft = 999f;
        _sinceLastKickRight = 999f;
        _sharedKickInProgress = false;
    }

    private void OnValidate()
    {
        if (rootSpace == null) rootSpace = transform;
        if (hipTransform == null) hipTransform = transform;
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

        // Translating -> shared cooldown; rotating in place -> independent cooldown.
        bool useSharedCooldown = shareCooldownWhenTranslating &&
                                 movement != null &&
                                 movement.GetVelocity().sqrMagnitude >= (translateSpeedForSharedCooldown * translateSpeedForSharedCooldown);

        // Tick cooldown timers (cooldown starts AFTER kick finishes).
        if (useSharedCooldown)
        {
            if (!_sharedKickInProgress) _sinceLastKickShared += dt;
        }
        else
        {
            if (!leftLeg.pendingKick) _sinceLastKickLeft += dt;
            if (!rightLeg.pendingKick) _sinceLastKickRight += dt;
        }

        // Build used velocity for triggering kicks (Scheme B).
        Vector3 usedVelRaw = ComputeUsedVelocityAndOmegaY(dt, out float omegaY);
        _lastOmegaY = omegaY;

        // Smooth usedVel before taking derivative (reduces accel spikes from omega/dt noise).
        if (usedVelSmoothingTau <= 1e-5f)
        {
            _smoothedUsedVel = usedVelRaw;
        }
        else
        {
            float a = 1f - Mathf.Exp(-dt / usedVelSmoothingTau);
            _smoothedUsedVel = Vector3.Lerp(_smoothedUsedVel, usedVelRaw, a);
        }

        // Demand mapping in root local space
        _usedVelLocal = rootSpace.InverseTransformDirection(_smoothedUsedVel);
        float omegaAbs = Mathf.Abs(_lastOmegaY);
        _turnDemand01 = (omegaAbs <= omegaYawDeadzone)
            ? 0f
            : Mathf.Clamp01((omegaAbs - omegaYawDeadzone) / Mathf.Max(omegaYawForFullDemand - omegaYawDeadzone, 1e-4f));

        float usedSpeed = _smoothedUsedVel.magnitude;

        if (usedSpeed < minSpeedToAnimate && !leftLeg.pendingKick && !rightLeg.pendingKick)
        {
            ReturnToRest(leftLeg, dt);
            ReturnToRest(rightLeg, dt);
            _prevUsedVel = _smoothedUsedVel;
            return;
        }

        Vector3 usedAccel = (_smoothedUsedVel - _prevUsedVel) / Mathf.Max(dt, 1e-5f);
        _prevUsedVel = _smoothedUsedVel;

        float accelMag = usedAccel.magnitude;

        if (accelMag >= accelTrigger)
        {
            float strength01 = Mathf.Clamp01(Mathf.InverseLerp(accelTrigger, accelForFullStrength, accelMag));

            if (useSharedCooldown)
            {
                if (!_sharedKickInProgress && _sinceLastKickShared >= minKickInterval)
                {
                    TriggerKick(strength01);
                    _sharedKickInProgress = true; // cooldown restarts when BOTH legs finish
                }
            }
            else
            {
                if (!leftLeg.pendingKick && _sinceLastKickLeft >= minKickInterval)
                    ArmLeg(leftLeg, strength01);

                if (!rightLeg.pendingKick && _sinceLastKickRight >= minKickInterval)
                    ArmLeg(rightLeg, strength01);
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

    private Vector3 ComputeUsedVelocityAndOmegaY(float dt, out float omegaY)
    {
        omegaY = 0f;

        Vector3 vRoot = Vector3.zero;
        if (movement != null)
            vRoot = movement.GetVelocity();

        if (!useAngularMotionForTrigger || angularContributionWeight <= 0f)
            return vRoot;

        Transform pivot = (pivotTransform != null) ? pivotTransform : (rootSpace != null ? rootSpace : transform);
        Transform hip = (hipTransform != null) ? hipTransform : transform;

        Quaternion rootRotNow = GetRootRotation();
        Quaternion rootRotPrev = _prevRootRot;
        _prevRootRot = rootRotNow;

        Quaternion delta = rootRotNow * Quaternion.Inverse(rootRotPrev);

        delta.ToAngleAxis(out float angleDeg, out Vector3 axisWorld);
        if (float.IsNaN(axisWorld.x) || axisWorld.sqrMagnitude < 1e-8f)
            return vRoot;

        if (angleDeg > 180f) angleDeg -= 360f;

        float angleRad = angleDeg * Mathf.Deg2Rad;
        Vector3 omega = axisWorld.normalized * (angleRad / Mathf.Max(dt, 1e-5f)); // rad/s

        if (useYawOnlyForAngularMotion)
            omega = Vector3.Project(omega, Vector3.up);

        omegaY = omega.y;

        Vector3 r = hip.position - pivot.position;
        Vector3 vAngular = Vector3.Cross(omega, r);

        return vRoot + vAngular * angularContributionWeight;
    }

    private Quaternion GetRootRotation()
    {
        return (rootSpace != null) ? rootSpace.rotation : transform.rotation;
    }

    private void ReturnToRest(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        leg.ikTarget.position = Vector3.Lerp(leg.ikTarget.position, restWorld, 1f - Mathf.Exp(-targetPosLerp * dt));
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
        leg.strength01 = strength01;
        leg.delayTimer = Random.Range(0f, leg.randomDelayMax);
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

        float phaseUsed = ApplyDirectionFlip(leg.phase01);

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

        float forwardSpeed = _usedVelLocal.z; // +Z forward in rootSpace local
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

        float turnSign = spineTurnSign;
        if (useOmegaYawForTurnSign)
        {
            turnSign = Mathf.Sign(_lastOmegaY);
            if (invertOmegaTurnSign) turnSign *= -1f;
            if (Mathf.Abs(_lastOmegaY) < 1e-4f) turnSign = 0f;
        }

        float legSideSign = GetLegSideSign(leg, sideXZ);

        if (Mathf.Abs(turnSign) < 0.5f || turnAmount01 < 0.02f)
            turnAmount01 = 0f;

        bool isInnerLeg = (turnAmount01 > 0f) && (Mathf.Sign(legSideSign) == Mathf.Sign(turnSign));
        leg.cachedIsInner = isInnerLeg;
        bool isOuterLeg = (turnAmount01 > 0f) && !isInnerLeg;

        float biasScale = Mathf.Clamp01(turnAmount01);

        float outerAngle = Mathf.Abs(outerTangentAngleOffsetDeg) * biasScale;
        float innerAngle = Mathf.Abs(innerTangentAngleOffsetDeg) * biasScale;

        float angleSignedDeg = isOuterLeg
            ? (-outerAngle * turnSign)
            : (innerAngle * turnSign);

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
        float lateralOffsetSigned = isOuterLeg
            ? (-outerOffset * turnSign)
            : (innerOffset * turnSign);

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
