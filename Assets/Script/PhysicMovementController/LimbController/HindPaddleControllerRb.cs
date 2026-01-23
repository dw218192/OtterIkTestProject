using UnityEngine;

/// <summary>
/// Hind-leg IK target driver synced to MovementControllerRB rhythm:
///   Prepare -> Kick -> Interval
///
/// Design goals:
/// - Hind legs DO NOT own timing.
/// - A full "kick circle" runs across (Prepare + Kick).
/// - Interval returns to rest / holds rest.
/// - Amplitude scales with demand01 (optional).
///
/// This script ONLY moves IK targets. Your rig (IK+FK) drives bones.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(250)]
public class HindLegKickControllerRB : MonoBehaviour
{
    [System.Serializable]
    public class Leg
    {
        public string name = "HindLeg";

        [Header("Rig references")]
        public Transform ikTarget;

        [Tooltip("Used only to compute left/right side sign relative to spine side axis.")]
        public Transform legRootBone;

        [Header("Rest pose")]
        public Vector3 restLocalPos;
        public bool autoUpdateRestInEditMode = true;

        [HideInInspector] public bool restInitialized;
    }

    [Header("Data source")]
    public MovementControllerRB movement; // <-- new movement script

    [Header("Space / Spine")]
    public Transform rootSpace;
    public Transform[] spineChain;

    [Header("Hind legs")]
    public Leg leftLeg = new Leg { name = "LeftHindLeg" };
    public Leg rightLeg = new Leg { name = "RightHindLeg" };

    [Header("Idle gating")]
    [Tooltip("If targetSpeed is 0 (Aim/None) or no intent, legs return to rest.")]
    public bool returnToRestWhenNoPropulsion = true;

    [Header("Ellipse design (world axes derived from spine tangent/side)")]
    public float ellipseForwardRadius = 0.18f;
    public float ellipseSecondaryRadius = 0.10f;

    [Tooltip("Amplitude multiplier when demand01==1.")]
    public float amplitudeMultiplierAtFullDemand = 1.8f;

    [Header("Rest on trajectory")]
    [Range(0f, 360f)]
    public float restAngleDeg = 110f;

    [Header("Ellipse plane tilt (mirrored per leg)")]
    [Range(0f, 90f)]
    public float ellipsePlaneTiltDeg = 65f;

    public bool tiltOutward = true;

    [Header("Inner / Outer tuning (same semantics as your old script)")]
    [Range(0f, 90f)] public float outerTangentAngleOffsetDeg = 45f;
    [Range(0f, 90f)] public float innerTangentAngleOffsetDeg = 15f;

    [Range(0f, 0.5f)] public float outerLateralOffsetMeters = 0.08f;
    [Range(0f, 0.5f)] public float innerLateralOffsetMeters = 0.02f;

    [Header("Turn amount mapping")]
    public float bendAngleForFullTurnDeg = 25f;

    [Header("Vertical shaping")]
    public AnimationCurve verticalArc = AnimationCurve.EaseInOut(0, 0, 1, 1);
    public float verticalArcAmplitude = 0.03f;

    [Header("Smoothing")]
    public float targetPosLerp = 16f;

    [Header("Trajectory direction")]
    public bool invertTrajectoryDirection = false;

    [Header("Debug gizmos")]
    public bool drawGizmos = true;
    public bool drawTrajectoryContour = true;
    [Range(8, 200)] public int gizmoEllipseSegments = 50;

    // ---------------- Movement kick (higher priority) runtime state ----------------
    public bool IsMovementKickActive => _moveKickActive;

    private bool _moveKickActive;
    private float _moveKickT;
    private float _moveKickDur = 0.28f;
    private float _moveKickDemand01 = 1f;

    // ---------------- Idle kick (lower priority, per-leg) runtime state ----------------
    private bool _idleLActive, _idleRActive;
    private float _idleLT, _idleRT;
    private float _idleLDur = 0.28f, _idleRDur = 0.28f;
    private float _idleLStrength = 0.5f, _idleRStrength = 0.5f;

    // ---------------- Unity lifecycle ----------------

    private void Reset()
    {
        rootSpace = transform;
        movement = GetComponentInParent<MovementControllerRB>();
    }

    private void OnEnable()
    {
        if (rootSpace == null) rootSpace = transform;
        SyncRestIfNeeded(force: false);

        if (movement != null)
            movement.OnKickCycleStart += HandleMovementKickCycleStart;
    }

    private void OnValidate()
    {
        if (rootSpace == null) rootSpace = transform;
        SyncRestIfNeeded(force: true);
    }

    private void OnDisable()
    {
        if (movement != null)
            movement.OnKickCycleStart -= HandleMovementKickCycleStart;
    }

    private void SyncRestIfNeeded(bool force)
    {
        SyncLegRest(leftLeg, force);
        SyncLegRest(rightLeg, force);
    }

    private void SyncLegRest(Leg leg, bool force)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;

        if (!Application.isPlaying)
        {
            if (!leg.autoUpdateRestInEditMode && leg.restInitialized && !force) return;
            leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
            leg.restInitialized = true;
        }
    }

    private void Start()
    {
        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);
    }

    public bool IsIdleLegActive(bool left) => left ? _idleLActive : _idleRActive;

    public bool TryStartIdleKick(bool left, float cycleDuration, float strength01)
    {
        // Movement has higher priority: do not start idle kick while moving kick is active
        if (_moveKickActive) return false;

        cycleDuration = Mathf.Max(0.05f, cycleDuration);
        strength01 = Mathf.Clamp01(strength01);

        if (left)
        {
            if (_idleLActive) return false;
            _idleLActive = true;
            _idleLT = 0f;
            _idleLDur = cycleDuration;
            _idleLStrength = strength01;
            return true;
        }

        if (_idleRActive) return false;
        _idleRActive = true;
        _idleRT = 0f;
        _idleRDur = cycleDuration;
        _idleRStrength = strength01;
        return true;
    }

    private void CancelAllIdleKicks()
    {
        _idleLActive = _idleRActive = false;
    }

    private void HandleMovementKickCycleStart(MovementControllerRB.KickCycleEvent e)
    {
        _moveKickActive = true;
        _moveKickT = 0f;
        _moveKickDur = Mathf.Max(0.05f, e.cycleDuration);
        _moveKickDemand01 = Mathf.Clamp01(e.demand01);

        // Movement overrides idle instantly
        CancelAllIdleKicks();
    }

    private void ForceInitRest(Leg leg)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        leg.restLocalPos = rootSpace.InverseTransformPoint(leg.ikTarget.position);
        leg.restInitialized = true;
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying)
        {
            SyncRestIfNeeded(force: false);
            return;
        }

        if (rootSpace == null) return;

        float dt = Time.deltaTime;

        // 1) Movement kick (highest priority)
        if (_moveKickActive)
        {
            _moveKickT += dt;
            float phase01 = Mathf.Clamp01(_moveKickT / Mathf.Max(1e-4f, _moveKickDur));

            UpdateLeg(leftLeg, dt, ApplyDirectionFlip(phase01), _moveKickDemand01, 1f);
            UpdateLeg(rightLeg, dt, ApplyDirectionFlip(phase01), _moveKickDemand01, 1f);

            if (_moveKickT >= _moveKickDur) _moveKickActive = false;
            return;
        }

        // 2) Idle kicks (per-leg, lower priority)
        bool anyIdle = _idleLActive || _idleRActive;

        if (anyIdle)
        {
            if (_idleLActive)
            {
                _idleLT += dt;
                float pL = Mathf.Clamp01(_idleLT / Mathf.Max(1e-4f, _idleLDur));
                UpdateLeg(leftLeg, dt, ApplyDirectionFlip(pL), 1f, _idleLStrength);
                if (_idleLT >= _idleLDur) _idleLActive = false;
            }
            else
            {
                ReturnToRest(leftLeg, dt);
            }

            if (_idleRActive)
            {
                _idleRT += dt;
                float pR = Mathf.Clamp01(_idleRT / Mathf.Max(1e-4f, _idleRDur));
                UpdateLeg(rightLeg, dt, ApplyDirectionFlip(pR), 1f, _idleRStrength);
                if (_idleRT >= _idleRDur) _idleRActive = false;
            }
            else
            {
                ReturnToRest(rightLeg, dt);
            }

            return;
        }

        // 3) No kicks: rest
        ReturnToRest(leftLeg, dt);
        ReturnToRest(rightLeg, dt);
    }

    // ---------------- Core motion ----------------

    private void ReturnToRest(Leg leg, float dt)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        leg.ikTarget.position = Vector3.Lerp(
            leg.ikTarget.position,
            restWorld,
            1f - Mathf.Exp(-targetPosLerp * dt)
        );
    }

    private void UpdateLeg(Leg leg, float dt, float phase01, float demand01, float scale01)
    {
        if (leg == null || leg.ikTarget == null || rootSpace == null) return;

        Vector3 localPos = ComputeKickLocalPosition(leg, phase01, demand01, scale01);
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

    // ---------------- Trajectory math (same idea as old script, but time is external) ----------------

    private float PhaseToThetaFromRest(float phase01)
    {
        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        return thetaRest + (phase01 * Mathf.PI * 2f);
    }

    private Vector3 ComputeKickLocalPosition(Leg leg, float phase01, float demand01, float scale01)
    {
        scale01 = Mathf.Clamp01(scale01);

        Vector3 deltaW = ComputeTrajectoryDeltaWorld_FromRestPhase(leg, phase01, demand01) * scale01;
        Vector3 deltaL = rootSpace.InverseTransformDirection(deltaW);

        float y01 = (verticalArc != null) ? verticalArc.Evaluate(Mathf.Clamp01(phase01)) : phase01;
        float ySigned = (y01 - 0.5f) * 2f;
        float yAmp = verticalArcAmplitude * Mathf.Lerp(0.6f, 1f, demand01) * scale01;

        return leg.restLocalPos + deltaL + (Vector3.up * (ySigned * yAmp));
    }

    private Vector3 ComputeTrajectoryDeltaWorld_FromRestPhase(Leg leg, float phase01, float demand01)
    {
        float ampMul = Mathf.Lerp(1f, amplitudeMultiplierAtFullDemand, demand01);
        float aF = ellipseForwardRadius * ampMul;
        float aS = ellipseSecondaryRadius * ampMul;

        GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out float turnSign, out float turnAmount01);

        float legSideSign = GetLegSideSign(leg, sideXZ);

        // If no meaningful turn, suppress inner/outer separation.
        if (Mathf.Abs(turnSign) < 0.5f || turnAmount01 < 0.02f)
            turnAmount01 = 0f;

        bool isInnerLeg = (turnAmount01 > 0f) && (Mathf.Sign(legSideSign) == Mathf.Sign(turnSign));
        bool isOuterLeg = (turnAmount01 > 0f) && !isInnerLeg;

        float biasScale = Mathf.Clamp01(turnAmount01);

        float outerAngle = Mathf.Abs(outerTangentAngleOffsetDeg) * biasScale;
        float innerAngle = Mathf.Abs(innerTangentAngleOffsetDeg) * biasScale;

        float angleSignedDeg = isOuterLeg
            ? (-outerAngle * turnSign)
            : (innerAngle * turnSign);

        Vector3 kickAxisXZ = (Quaternion.AngleAxis(angleSignedDeg, Vector3.up) * tangentXZ).normalized;

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

        DrawLegGizmos(leftLeg);
        DrawLegGizmos(rightLeg);
    }

    private void DrawLegGizmos(Leg leg)
    {
        if (leg == null || leg.ikTarget == null) return;

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(leg.ikTarget.position, 0.02f);

        if (!drawTrajectoryContour) return;

        float previewDemand = 1f;

        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        Vector3 prev = ComputeTrajectoryPointWorld(leg, 0f, previewDemand);

        for (int i = 1; i <= gizmoEllipseSegments; i++)
        {
            float u = i / (float)gizmoEllipseSegments;
            Vector3 p = ComputeTrajectoryPointWorld(leg, u, previewDemand);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }

        Gizmos.DrawLine(prev, ComputeTrajectoryPointWorld(leg, 0f, previewDemand));
    }

    private Vector3 ComputeTrajectoryPointWorld(Leg leg, float phase01, float demand01)
    {
        Vector3 restWorld = rootSpace.TransformPoint(leg.restLocalPos);
        return restWorld + ComputeTrajectoryDeltaWorld_FromRestPhase(leg, ApplyDirectionFlip(phase01), demand01);
    }
#endif
}
