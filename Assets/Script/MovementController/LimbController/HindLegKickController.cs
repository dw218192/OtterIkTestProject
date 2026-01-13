using UnityEngine;

[ExecuteAlways]
[DefaultExecutionOrder(250)]
public class HindLegKickController : MonoBehaviour
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
        [HideInInspector] public float phase01;
        [HideInInspector] public float strength01;

        [HideInInspector] public bool restInitialized;
    }

    [Header("Data source")]
    public MovementController movement;

    [Header("Space / Spine")]
    public Transform rootSpace;
    public Transform[] spineChain;

    [Header("Hind legs")]
    public Leg leftLeg = new Leg { name = "LeftHindLeg" };
    public Leg rightLeg = new Leg { name = "RightHindLeg" };

    [Header("Idle gating")]
    public float minSpeedToAnimate = 0.05f;

    [Header("Kick trigger (acceleration)")]
    public float accelTrigger = 0.8f;
    public float accelForFullStrength = 4.0f;
    public float minKickInterval = 0.25f;

    [Header("Kick timing")]
    public float baseCycleTime = 0.55f;
    public float minCycleTime = 0.25f;
    [Range(0.05f, 0.8f)]
    public float curlPortion = 0.35f;

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

    public bool tiltOutward = true;

    [Header("Direction from spine bending (outer/inner bias)")]
    [Range(0f, 90f)]
    public float maxTangentSideAngleOffsetDeg = 25f;
    public float maxLateralOffsetMeters = 0.05f;
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

    [Tooltip("Enable preview marker in Edit Mode.")]
    public bool gizmoPreviewEnabled = true;

    [Tooltip("Edit-mode preview phase (0..1). Phase=0 is guaranteed to be at REST (current IK rest point).")]
    [Range(0f, 1f)]
    public float gizmoPreviewPhase01 = 0f;

    public bool drawPreviewMarkerOnTrajectory = true;

    [Range(0.005f, 0.08f)]
    public float previewMarkerRadius = 0.025f;

    public Color previewMarkerColor = new Color(1f, 0.55f, 0f, 1f);

    public bool drawRootAxes = true;
    public float gizmoAxisScale = 0.25f;

    private Vector3 _prevVel;
    private float _sinceLastKick = 999f;

    private void Reset()
    {
        rootSpace = transform;
        movement = GetComponentInParent<MovementController>();
    }

    private void OnEnable()
    {
        if (rootSpace == null) rootSpace = transform;
        SyncRestIfNeeded(force: false);
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

    private void Start()
    {
        if (rootSpace == null) rootSpace = transform;
        ForceInitRest(leftLeg);
        ForceInitRest(rightLeg);
        _prevVel = movement ? movement.GetVelocity() : Vector3.zero;
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

        if (rootSpace == null || movement == null) return;

        float dt = Time.deltaTime;
        _sinceLastKick += dt;

        Vector3 vel = movement.GetVelocity();
        float speed = vel.magnitude;

        if (speed < minSpeedToAnimate && !leftLeg.pendingKick && !rightLeg.pendingKick)
        {
            ReturnToRest(leftLeg, dt);
            ReturnToRest(rightLeg, dt);
            _prevVel = vel;
            return;
        }

        Vector3 accel = (vel - _prevVel) / Mathf.Max(dt, 1e-5f);
        _prevVel = vel;

        float accelMag = accel.magnitude;
        if (_sinceLastKick >= minKickInterval && accelMag >= accelTrigger)
        {
            float strength01 = Mathf.Clamp01(Mathf.InverseLerp(accelTrigger, accelForFullStrength, accelMag));
            TriggerKick(strength01);
            _sinceLastKick = 0f;
        }

        UpdateLeg(leftLeg, dt);
        UpdateLeg(rightLeg, dt);
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

        leg.ikTarget.position = Vector3.Lerp(leg.ikTarget.position, desiredWorld, 1f - Mathf.Exp(-targetPosLerp * dt));
    }

    private float ApplyDirectionFlip(float phase01)
    {
        phase01 = Mathf.Clamp01(phase01);
        return invertTrajectoryDirection ? (1f - phase01) : phase01;
    }

    // =========================
    // Key fix: phase -> theta
    // =========================
    // Preview / contour should start from REST:
    // phase=0  => theta = thetaRest
    // phase=1  => theta = thetaRest + 2π  (one full loop)
    private float PhaseToThetaFromRest(float phase01)
    {
        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        return thetaRest + (phase01 * Mathf.PI * 2f);
    }

    private Vector3 ComputeKickLocalPosition(Leg leg, float phase01, float strength01)
    {
        Vector3 deltaW = ComputeTrajectoryDeltaWorld_FromRestPhase(leg, phase01, strength01);
        Vector3 deltaL = rootSpace.InverseTransformDirection(deltaW);

        float y01 = verticalArc != null ? verticalArc.Evaluate(Mathf.Clamp01(phase01)) : phase01;
        float ySigned = (y01 - 0.5f) * 2f;
        float yAmp = verticalArcAmplitude * Mathf.Lerp(0.6f, 1f, strength01);
        Vector3 upDeltaL = Vector3.up * (ySigned * yAmp);

        return leg.restLocalPos + deltaL + upDeltaL;
    }

    // IMPORTANT:
    // This variant uses the "phase starts at rest" theta mapping.
    // If you want runtime kick to still use your old curl/kick mapping, keep that there,
    // BUT for preview you should use this mapping.
    // Here I apply it everywhere for consistency.
    private Vector3 ComputeTrajectoryDeltaWorld_FromRestPhase(Leg leg, float phase01, float strength01)
    {
        float ampMul = Mathf.Lerp(1f, amplitudeMultiplierAtFullStrength, strength01);
        float aF = ellipseForwardRadius * ampMul;
        float aS = ellipseSecondaryRadius * ampMul;

        GetSpineTangentAndBend(out Vector3 tangentXZ, out Vector3 sideXZ, out float turnSign, out float turnAmount01);

        float legSideSign = GetLegSideSign(leg, sideXZ);

        bool outerIsRightSide = (turnSign < 0f);
        bool legIsRightSide = (legSideSign > 0f);
        bool isOuterLeg = (turnAmount01 > 0.001f) && (legIsRightSide == outerIsRightSide);

        float biasScale = turnAmount01;
        float outerInnerSign = isOuterLeg ? +1f : -1f;

        float angleOffsetDeg = maxTangentSideAngleOffsetDeg * biasScale;
        Quaternion rotBias = Quaternion.AngleAxis(angleOffsetDeg * outerInnerSign, Vector3.up);
        Vector3 kickAxisXZ = (rotBias * tangentXZ).normalized;

        Vector3 secondaryAxisBase = (sideXZ * legSideSign).normalized;

        float outwardFlip = tiltOutward ? 1f : -1f;
        float signedTiltDeg = ellipsePlaneTiltDeg * legSideSign * outwardFlip;
        Vector3 secondaryAxisTilted = Quaternion.AngleAxis(signedTiltDeg, tangentXZ) * secondaryAxisBase;

        float lateralOffset = maxLateralOffsetMeters * biasScale;

        Vector3 E(float angRad)
        {
            float f = Mathf.Cos(angRad) * aF;
            float s = Mathf.Sin(angRad) * aS;
            return kickAxisXZ * f + secondaryAxisTilted * (s + lateralOffset);
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

        t0.y = 0f; t1.y = 0f;

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

    private void OnDrawGizmosSelected()
    {
#if UNITY_EDITOR
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
#endif
    }

#if UNITY_EDITOR
    private void DrawLegGizmos(Leg leg)
    {
        if (leg == null || leg.ikTarget == null) return;

        // Always show actual IK target
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

        // Preview marker: phase=0 is guaranteed to be at REST now.
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
