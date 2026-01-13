using UnityEngine;

/// <summary>
/// Hind-leg kick controller (no Rigidbody).
///
/// Key design:
/// - This script ONLY moves IK targets. Bone motion comes from your IK rig.
/// - Kick trigger uses point acceleration at hip: v_used = v_root + (omega x r) [Scheme B].
/// - Inner/Outer leg direction is defined relative to the SPINE CURVE at hip:
///     - tangentXZ: spine curve tangent (XZ).
///     - sideXZ: normal to tangent on XZ (right side of tangent).
///     - turnSign: sign of turning direction (recommended from omegaY for stability).
///     - legSideSign: which side of sideXZ the leg root is on.
///     - innerLeg: legSideSign == turnSign
///     - outerLeg: legSideSign != turnSign
///
/// Inner/Outer can have DIFFERENT magnitudes:
/// - outerTangentAngleOffsetDeg / innerTangentAngleOffsetDeg
/// - outerLateralOffsetMeters   / innerLateralOffsetMeters
///
/// Gizmo:
/// - Preview marker sphere on the trajectory at gizmoPreviewPhase01 (0..1). Phase=0 is REST.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(250)]
public class HindLegKickController : MonoBehaviour
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
        [HideInInspector] public float phase01;
        [HideInInspector] public float strength01;

        [HideInInspector] public bool restInitialized;
    }

    [Header("Data source (root translation)")]
    public MovementController movement;

    [Header("Space / Spine")]
    public Transform rootSpace;
    public Transform[] spineChain;

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
    private float _sinceLastKick = 999f;

    // We also cache omegaY each frame for turnSign usage (when useOmegaYawForTurnSign=true).
    private float _lastOmegaY;

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
        _lastOmegaY = 0f;

        SyncRestIfNeeded(force: false);
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
        if (leg == null) return;
        if (rootSpace == null || leg.ikTarget == null) return;

        if (!Application.isPlaying)
        {
            if (!leg.autoUpdateRestInEditMode && leg.restInitialized && !force) return;
            leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
            leg.restInitialized = true;
        }
    }

    private void Start()
    {
        if (rootSpace == null) rootSpace = transform;
        if (hipTransform == null) hipTransform = transform;

        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);

        _prevRootRot = GetRootRotation();
        _prevUsedVel = Vector3.zero;
        _lastOmegaY = 0f;
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
        _sinceLastKick += dt;

        // Build the velocity used for triggering kicks (Scheme B).
        Vector3 usedVel = ComputeUsedVelocityAndOmegaY(dt, out float omegaY);

        // Cache omegaY for inner/outer SIGN if desired.
        _lastOmegaY = omegaY;

        float usedSpeed = usedVel.magnitude;

        if (usedSpeed < minSpeedToAnimate && !leftLeg.pendingKick && !rightLeg.pendingKick)
        {
            ReturnToRest(leftLeg, dt);
            ReturnToRest(rightLeg, dt);
            _prevUsedVel = usedVel;
            return;
        }

        Vector3 usedAccel = (usedVel - _prevUsedVel) / Mathf.Max(dt, 1e-5f);
        _prevUsedVel = usedVel;

        float accelMag = usedAccel.magnitude;

        if (_sinceLastKick >= minKickInterval && accelMag >= accelTrigger)
        {
            float strength01 = Mathf.Clamp01(Mathf.InverseLerp(accelTrigger, accelForFullStrength, accelMag));
            TriggerKick(strength01);
            _sinceLastKick = 0f;
        }

        UpdateLeg(leftLeg, dt);
        UpdateLeg(rightLeg, dt);
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
        {
            omega = Vector3.Project(omega, Vector3.up);
        }

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
        if (leg == null || leg.ikTarget == null) return;
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
        leg.pendingKick = true;
        leg.phase01 = 0f;
        leg.strength01 = strength01;
        leg.delayTimer = Random.Range(0f, leg.randomDelayMax);
    }

    private void UpdateLeg(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null) return;

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

        if (leg.phase01 >= 1f)
        {
            leg.phase01 = 1f;
            leg.pendingKick = false;
        }

        float phaseUsed = ApplyDirectionFlip(leg.phase01);

        Vector3 localPos = ComputeKickLocalPosition(leg, phaseUsed, leg.strength01);
        Vector3 desiredWorld = rootSpace.TransformPoint(localPos);

        leg.ikTarget.position = Vector3.Lerp(
            leg.ikTarget.position,
            desiredWorld,
            1f - Mathf.Exp(-targetPosLerp * dt)
        );
    }

    private float ApplyDirectionFlip(float phase01)
    {
        phase01 = Mathf.Clamp01(phase01);
        return invertTrajectoryDirection ? (1f - phase01) : phase01;
    }

    // =========================
    // Trajectory (phase starts at REST)
    // =========================
    private float PhaseToThetaFromRest(float phase01)
    {
        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        return thetaRest + (phase01 * Mathf.PI * 2f);
    }

    private Vector3 ComputeKickLocalPosition(Leg leg, float phase01, float strength01)
    {
        Vector3 deltaW = ComputeTrajectoryDeltaWorld_FromRestPhase(leg, phase01, strength01);
        Vector3 deltaL = rootSpace.InverseTransformDirection(deltaW);

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

        // We compute tangent/side and bend amount from spine.
        GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out float spineTurnSign, out float turnAmount01);

        // Turning SIGN can come from omegaY (recommended), while amount still uses spine bend magnitude.
        float turnSign = spineTurnSign;
        if (useOmegaYawForTurnSign)
        {
            turnSign = Mathf.Sign(_lastOmegaY);
            // If omega is almost zero, treat as "no turn".
            if (Mathf.Abs(_lastOmegaY) < 1e-4f) turnSign = 0f;
        }

        float legSideSign = GetLegSideSign(leg, sideXZ);

        // If no meaningful turn, do not classify inner/outer strongly.
        // In that case, we reduce separation by forcing turnAmount01=0.
        if (Mathf.Abs(turnSign) < 0.5f || turnAmount01 < 0.02f)
        {
            turnAmount01 = 0f;
        }

        bool isInnerLeg = (turnAmount01 > 0f) && (Mathf.Sign(legSideSign) == Mathf.Sign(turnSign));
        bool isOuterLeg = (turnAmount01 > 0f) && !isInnerLeg;

        // === Your requested two-value controls ===
        // Angle offset magnitude
        float angleMag = isOuterLeg ? outerTangentAngleOffsetDeg : innerTangentAngleOffsetDeg;
        // Lateral offset magnitude (meters)
        float offsetMag = isOuterLeg ? outerLateralOffsetMeters : innerLateralOffsetMeters;

        // Scale both by turnAmount01 so straight swimming doesn't exaggerate.
        float biasScale = Mathf.Clamp01(turnAmount01);
        angleMag *= biasScale;
        offsetMag *= biasScale;

        // Direction sign MUST be mirrored by leg side:
        // - OUTER leg pushes outward: along +legSideSign
        // - INNER leg pushes inward:  along -legSideSign
        float sideSignForBias = isOuterLeg ? legSideSign : -legSideSign;

        // Kick axis is tangent rotated around world up.
        Quaternion rotBias = Quaternion.AngleAxis(angleMag * sideSignForBias, Vector3.up);
        Vector3 kickAxisXZ = (rotBias * tangentXZ).normalized;

        // Secondary axis is side mirrored by leg side.
        Vector3 secondaryAxisBase = (sideXZ * legSideSign).normalized;

        // Tilt around tangent for "ellipse plane tilt"
        float outwardFlip = tiltOutward ? 1f : -1f;
        float signedTiltDeg = ellipsePlaneTiltDeg * legSideSign * outwardFlip;
        Vector3 secondaryAxisTilted = Quaternion.AngleAxis(signedTiltDeg, tangentXZ) * secondaryAxisBase;

        // Lateral offset applies along the secondary axis; sign must follow sideSignForBias
        float lateralOffsetSigned = offsetMag * sideSignForBias;

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
        Vector3 t0 = rootSpace.forward;
        Vector3 t1 = rootSpace.forward;

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

        if (t0.sqrMagnitude < 1e-6f) t0 = rootSpace.forward;
        if (t1.sqrMagnitude < 1e-6f) t1 = t0;

        Vector3 n0 = t0.normalized;
        Vector3 n1 = t1.normalized;

        tangentXZ = n0;

        sideXZ = Vector3.Cross(Vector3.up, tangentXZ).normalized;
        if (sideXZ.sqrMagnitude < 1e-6f) sideXZ = rootSpace.right;

        // Curvature direction sign from spine itself (may overshoot / be asymmetric)
        float crossY = Vector3.Cross(n0, n1).y;
        turnSign = Mathf.Sign(crossY);

        // Bend amount -> 0..1
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
