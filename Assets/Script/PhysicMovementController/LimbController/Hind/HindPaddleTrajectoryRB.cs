using UnityEngine;

/// <summary>
/// HindPaddleTrajectoryRB (optimized / simplified)
///
/// Key design:
/// - The trajectory coordinate system is defined purely from the given hip joint (rootSpace).
/// - If the hip joint flips 180° for backstroke, the trajectory naturally flips with it (no parameter changes needed).
///
/// Features:
/// - Minimal required params: restAngleDeg, forwardRadius, sideRadius, clockwise, tiltOutward, trajectoryTiltDeg.
/// - Two legs for stable left/right sign.
/// - Turn-aware kick direction: outer kicks outward, inner kicks inward (turnKickYawDeg).
/// - Turn lateral offset relative to kick direction (turnLateralOffsetMeters).
/// - trajectoryTiltDeg is mirrored per leg (not identical), so both legs tilt "down" or "up" together visually.
///   Implementation: signedTilt = trajectoryTiltDeg * legSideSign (left=-1, right=+1).
///
/// IMPORTANT CHANGE:
/// - Inner/Outer leg classification uses SpineCurveInnerOuterWorldUp (WORLD UP), verified by standalone test.
///   This avoids sign/axis convention issues that caused flipped inner/outer previously.
///
/// API compatibility:
/// - EvaluateKickLocal(...)
/// - EvaluateTrajectoryPointWorld(...)
/// - GetTurnContext(out up, out forward, out side, out turnSign, out turnAmount01)
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class HindPaddleTrajectoryRB : MonoBehaviour
{
    [System.Serializable]
    public class LegBinding
    {
        public Transform legRootBone;
    }

    public enum AxisChoice { X, Y, Z, NegX, NegY, NegZ }

    [Header("Hip / Root (REQUIRED)")]
    [Tooltip("Hip joint transform. The trajectory axes are derived from this transform's local axes.\n" +
             "If this transform flips for backstroke, trajectory flips automatically.")]
    public Transform rootSpace;

    [Header("Axes (on hip/rootSpace)")]
    [Tooltip("Which local axis of hip/rootSpace defines Up.")]
    public AxisChoice upAxis = AxisChoice.Y;

    [Tooltip("Which local axis of hip/rootSpace defines Forward.")]
    public AxisChoice forwardAxis = AxisChoice.Z;

    [Header("Leg inputs (REQUIRED)")]
    public LegBinding leftLeg = new LegBinding();
    public LegBinding rightLeg = new LegBinding();

    [Header("Spine chain (for turn inner/outer)")]
    [Tooltip("Provide spine nodes in order. First = front, last = back.\n" +
             "Mid node is chosen automatically for bend estimation.")]
    public Transform[] spineChain;

    [Header("Ellipse (REQUIRED)")]
    [Tooltip("Forward/back radius along kick direction.")]
    public float forwardRadius = 0.18f;

    [Tooltip("Side radius along the leg side axis (after tilt).")]
    public float sideRadius = 0.10f;

    [Tooltip("Rest/Target point angle on ellipse in degrees.\n" +
             "0° = +kickDir, 90° = +sideDir (per leg).")]
    [Range(0f, 360f)]
    public float restAngleDeg = 110f;

    [Header("Phase direction (REQUIRED)")]
    [Tooltip("Phase increases clockwise when looking from +Up axis.")]
    public bool clockwise = true;

    [Header("Outward bias (REQUIRED)")]
    [Tooltip("ON: left goes more left, right goes more right (outward from hip centerline).")]
    public bool tiltOutward = true;

    [Header("Trajectory tilt (mirrored per leg)")]
    [Tooltip("Tilts the ellipse side axis around kickDir.\n" +
             "This is mirrored per leg so both sides visually tilt together (not one up / one down).")]
    [Range(-90f, 90f)]
    public float trajectoryTiltDeg = -55f;

    [Header("Turn model (optional but recommended)")]
    [Tooltip("Outer leg yaws outward and inner leg yaws inward by this degrees * turnAmount01.")]
    [Range(0f, 90f)]
    public float turnKickYawDeg = 25f;

    [Tooltip("INNER leg yaw multiplier when turning. 1=default, >1 stronger, <1 weaker.")]
    [Range(0f, 3f)] public float innerYawAmplifier = 1.0f;

    [Tooltip("OUTER leg yaw multiplier when turning. 1=default, >1 stronger, <1 weaker.")]
    [Range(0f, 3f)] public float outerYawAmplifier = 1.0f;

    [Tooltip("Spine bend angle (deg) that maps to turnAmount01 = 1.")]
    public float bendAngleForFullTurnDeg = 25f;

    [Tooltip("Lateral offset (meters) relative to kick direction during turns.\n" +
             "Outer offsets outward, inner offsets inward, scaled by turnAmount01.")]
    public float turnLateralOffsetMeters = 0.04f;

    [Header("Vertical shaping (optional)")]
    public AnimationCurve verticalArc = AnimationCurve.EaseInOut(0, 0, 1, 1);
    public float verticalArcAmplitude = 0.03f;

    [Header("Amplitude mapping (optional)")]
    [Tooltip("Multiplier when demand01==1. Keep at 1 if you want demand to be handled only by Driver scale.")]
    public float amplitudeMultiplierAtFullDemand = 1.0f;

    [Header("Misc (optional)")]
    public bool invertTrajectoryDirection = false;

    private void Reset()
    {
        if (rootSpace == null) rootSpace = transform;
    }

    private void OnValidate()
    {
        if (rootSpace == null) rootSpace = transform;
    }

    // ============================================================
    // Public API (compatible with Driver/Visualizer)
    // ============================================================

    public Vector3 EvaluateKickLocal(
        LegBinding binding,
        Vector3 restLocalPos,
        float phase01,
        float demand01,
        float scale01)
    {
        if (rootSpace == null) return restLocalPos;

        phase01 = Mathf.Clamp01(phase01);
        if (invertTrajectoryDirection) phase01 = 1f - phase01;

        demand01 = Mathf.Clamp01(demand01);
        scale01 = Mathf.Clamp01(scale01);

        Vector3 deltaW = ComputeDeltaWorld_FromRest(binding, phase01, demand01) * scale01;
        Vector3 deltaL = rootSpace.InverseTransformDirection(deltaW);

        // Vertical shaping remains shared (idle/move)
        float y01 = verticalArc != null ? Mathf.Clamp01(verticalArc.Evaluate(phase01)) : phase01;
        float ySigned = (y01 - 0.5f) * 2f;
        float yAmp = verticalArcAmplitude * scale01;

        return restLocalPos + deltaL + Vector3.up * (ySigned * yAmp);
    }

    public Vector3 EvaluateTrajectoryPointWorld(
        LegBinding binding,
        Vector3 restLocalPos,
        float phase01,
        float demand01)
    {
        if (rootSpace == null) return transform.position;

        phase01 = Mathf.Clamp01(phase01);
        if (invertTrajectoryDirection) phase01 = 1f - phase01;

        Vector3 restW = rootSpace.TransformPoint(restLocalPos);
        return restW + ComputeDeltaWorld_FromRest(binding, phase01, Mathf.Clamp01(demand01));
    }

    public void GetTurnContext(
        out Vector3 up,
        out Vector3 forward,
        out Vector3 side,
        out float turnSign,
        out float turnAmount01)
    {
        BuildBaseFrame(out up, out forward, out side);
        ComputeTurnFromSpine(up, out turnSign, out turnAmount01);
    }

    // ============================================================
    // Core math
    // ============================================================

    private Vector3 ComputeDeltaWorld_FromRest(LegBinding binding, float phase01, float demand01)
    {
        BuildBaseFrame(out Vector3 up, out Vector3 baseForward, out Vector3 baseSide);

        float legSideSign = GetLegSideSign(binding, up, baseSide); // left=-1, right=+1

        // Horizontal outward direction (purely left/right), affected by tiltOutward.
        float outwardSign = tiltOutward ? legSideSign : -legSideSign;

        // Turn info from spine curve (kept: used for yaw direction/magnitude)
        ComputeTurnFromSpine(up, out float turnSign, out float turn01);
        bool hasTurnYaw = turn01 > 0.02f && Mathf.Abs(turnSign) > 0.5f;

        // Inner/Outer (WORLD UP, verified by standalone test):
        // Use SpineCurveInnerOuterWorldUp to avoid sign/axis conventions.
        bool isInner = false;
        bool isOuter = false;

        bool hasTurn = false;
        if (hasTurnYaw &&
            spineChain != null && spineChain.Length >= 3 &&
            leftLeg != null && leftLeg.legRootBone != null &&
            rightLeg != null && rightLeg.legRootBone != null)
        {
            float planeY = rootSpace != null ? rootSpace.position.y : transform.position.y;
            var res = SpineCurveInnerOuterWorldUp.Evaluate(
                spineChain,
                leftLeg.legRootBone,
                rightLeg.legRootBone,
                planeY
            );

            hasTurn = res.hasTurn;

            bool bindingIsLeft = binding != null && binding.legRootBone == leftLeg.legRootBone;
            bool bindingIsRight = binding != null && binding.legRootBone == rightLeg.legRootBone;

            if (hasTurn)
            {
                if (bindingIsLeft) isInner = res.leftIsInner;
                else if (bindingIsRight) isInner = !res.leftIsInner;
                else
                {
                    // Unknown binding: fallback to old sign logic
                    isInner = (Mathf.Sign(outwardSign) == Mathf.Sign(turnSign));
                }

                isOuter = !isInner;
            }
        }

        if (!hasTurn)
        {
            // Fallback: keep behavior reasonable if spine is near-straight or refs are missing.
            hasTurn = hasTurnYaw;
            isInner = hasTurn && (Mathf.Sign(outwardSign) == Mathf.Sign(turnSign));
            isOuter = hasTurn && !isInner;
        }

        // Kick direction: base forward + turn yaw (outer outward, inner inward)
        Vector3 kickDir = baseForward;
        if (hasTurn && turnKickYawDeg > 0.001f)
        {
            float amp = isOuter ? outerYawAmplifier : innerYawAmplifier;
            float yaw = turnKickYawDeg * turn01 * Mathf.Max(0f, amp);

            float signedYaw = isOuter ? (-yaw * turnSign) : (+yaw * turnSign);
            kickDir = Quaternion.AngleAxis(signedYaw, up) * baseForward;
            kickDir.Normalize();
        }

        // Side axis relative to kickDir (offsets relative to kick direction, not world)
        Vector3 sideOfKick = Vector3.Cross(up, kickDir).normalized;

        // Per-leg side direction (outward) in kick frame
        Vector3 legSideDir = sideOfKick * outwardSign;

        // Mirrored tilt per leg (prevents one-up/one-down)
        float signedTiltDeg = trajectoryTiltDeg * legSideSign;
        if (Mathf.Abs(signedTiltDeg) > 1e-4f)
        {
            legSideDir = Quaternion.AngleAxis(signedTiltDeg, kickDir) * legSideDir;
            if (legSideDir.sqrMagnitude > 1e-8f) legSideDir.Normalize();
        }

        // Turn lateral offset (relative to kick direction)
        float lateral = 0f;
        if (hasTurn && Mathf.Abs(turnLateralOffsetMeters) > 1e-6f)
        {
            // Outer offsets outward, inner offsets inward
            float s = isOuter ? +1f : -1f;
            lateral = s * turnLateralOffsetMeters * turn01;
        }

        // Demand amplitude
        float ampMul = Mathf.Lerp(1f, amplitudeMultiplierAtFullDemand, demand01);
        float aF = forwardRadius * ampMul;
        float aS = sideRadius * ampMul;

        float thetaRest = Mathf.Deg2Rad * restAngleDeg;
        float dir = clockwise ? -1f : +1f;
        float theta = thetaRest + dir * (phase01 * Mathf.PI * 2f);

        Vector3 E(float ang)
        {
            float c = Mathf.Cos(ang);
            float s = Mathf.Sin(ang);
            return kickDir * (c * aF) + legSideDir * (s * aS + lateral);
        }

        return E(theta) - E(thetaRest);
    }

    private void BuildBaseFrame(out Vector3 up, out Vector3 forward, out Vector3 side)
    {
        Transform t = rootSpace != null ? rootSpace : transform;

        up = GetAxisWorld(t, upAxis).normalized;

        // Project forward onto plane defined by up
        Vector3 f = GetAxisWorld(t, forwardAxis);
        f = Vector3.ProjectOnPlane(f, up);
        if (f.sqrMagnitude < 1e-8f)
        {
            Vector3 rf = t.forward;
            f = Vector3.ProjectOnPlane(rf, up);
        }
        forward = f.normalized;

        side = Vector3.Cross(up, forward).normalized;
        if (side.sqrMagnitude < 1e-8f)
        {
            Vector3 rs = t.right;
            side = Vector3.ProjectOnPlane(rs, up).normalized;
        }
    }

    private static Vector3 GetAxisWorld(Transform t, AxisChoice a)
    {
        switch (a)
        {
            case AxisChoice.X:    return t.right;
            case AxisChoice.Y:    return t.up;
            case AxisChoice.Z:    return t.forward;
            case AxisChoice.NegX: return -t.right;
            case AxisChoice.NegY: return -t.up;
            case AxisChoice.NegZ: return -t.forward;
            default:              return t.up;
        }
    }

    private float GetLegSideSign(LegBinding binding, Vector3 up, Vector3 baseSide)
    {
        if (binding == null || binding.legRootBone == null || rootSpace == null || baseSide.sqrMagnitude < 1e-8f)
            return +1f;

        Vector3 toLeg = binding.legRootBone.position - rootSpace.position;
        toLeg = Vector3.ProjectOnPlane(toLeg, up);

        return Vector3.Dot(toLeg, baseSide) >= 0f ? +1f : -1f;
    }

    /// <summary>
    /// Turn from spine curvature in hip-defined plane:
    /// - Use first/mid/last nodes.
    /// - Signed angle between (mid-first) and (last-mid) around Up gives turn direction.
    /// - Magnitude mapped by bendAngleForFullTurnDeg.
    /// </summary>
    private void ComputeTurnFromSpine(Vector3 up, out float turnSign, out float turnAmount01)
    {
        turnSign = 0f;
        turnAmount01 = 0f;

        if (spineChain == null || spineChain.Length < 3)
            return;

        Transform a = spineChain[0];
        Transform b = spineChain[spineChain.Length / 2];
        Transform c = spineChain[spineChain.Length - 1];

        if (a == null || b == null || c == null)
            return;

        Vector3 v0 = b.position - a.position;
        Vector3 v1 = c.position - b.position;

        v0 = Vector3.ProjectOnPlane(v0, up);
        v1 = Vector3.ProjectOnPlane(v1, up);

        if (v0.sqrMagnitude < 1e-8f || v1.sqrMagnitude < 1e-8f)
            return;

        v0.Normalize();
        v1.Normalize();

        float signed = Vector3.SignedAngle(v0, v1, up);
        float mag = Mathf.Abs(signed);

        if (mag < 0.5f)
            return;

        turnSign = Mathf.Sign(signed);

        float full = Mathf.Max(1e-3f, bendAngleForFullTurnDeg);
        turnAmount01 = Mathf.Clamp01(mag / full);
    }
}
