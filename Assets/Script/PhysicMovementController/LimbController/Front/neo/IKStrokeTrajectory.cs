using UnityEngine;

/// <summary>
/// IKStrokeTrajectory
///
/// Draws default teardrop trajectories around two user-provided rest points.
/// Basis is built from:
/// - shoulder line (Right axis)
/// - stable basis forward (Forward axis, projected to be orthogonal to shoulder line)
///
/// The curve is shifted so that a chosen phase ("restPhase01") is exactly at the rest point.
/// </summary>
[ExecuteAlways]
public class IKStrokeTrajectory : MonoBehaviour
{
    [Header("Input")]
    public DynamicIndirectIk_V2 source;
    public Transform leftRestPoint;
    public Transform rightRestPoint;

    [Header("Default Trajectory Shape (local)")]
    public float strokeAmpRight = 0.25f;   // local X
    public float strokeAmpUp = 0.15f;      // local Y
    public float strokeAmpForward = 0.20f; // local Z
    [Range(0f, 1f)] public float strokeTeardrop = 0.6f;
    [Tooltip("If true, swap local Y/Z contribution of the curve shape.")]
    public bool strokeSwapYZ = false;

    [Header("Rest Vertex")]
    [Tooltip("Phase where curve offset is zero (point sits exactly at rest point).")]
    [Range(0f, 1f)] public float restPhase01 = 0.25f;
    [Tooltip("If true, always pin the TOP vertex (t = pi/2) to rest point.")]
    public bool restPointIsTopVertex = true;
    [Tooltip("Mirror right side in local X for left-right symmetry.")]
    public bool mirrorRightSide = true;

    [Header("Dynamic Outer Plane")]
    public bool enableDynamicOuterPlane = true;
    [Min(0.0001f)] public float lagUnrecoveredDistance = 0.03f;
    [Range(0.1f, 1f)] public float lagRecoverRatio = 0.65f;
    [Tooltip("动态权重进入时长（秒）：idle->dynamic 的过渡时间。")]
    [Min(0.01f)] public float activationInTime = 0.18f;
    [Tooltip("动态权重退出时长（秒）：dynamic->idle 的过渡时间。")]
    [Min(0.01f)] public float activationOutTime = 0.24f;
    [Tooltip("基座旋转最大角速度（外侧动态时，deg/s）。")]
    [Min(1f)] public float activeTurnSpeedDegPerSec = 420f;
    [Tooltip("基座旋转最大角速度（回到默认时，deg/s）。")]
    [Min(1f)] public float returnTurnSpeedDegPerSec = 300f;
    [Tooltip("动态平面目标自身平滑强度（越大越跟随，越小越稳）。")]
    [Min(0f)] public float dynamicTargetSharpness = 20f;
    [Min(1e-6f)] public float dynamicForwardMin = 1e-4f;
    [Tooltip("lag 点相对 idle(肩线平行静止位)的最小偏离。用于动态平面激活的软门限起点。")]
    [Min(0f)] public float lagEvidenceMinDistance = 0.015f;
    [Tooltip("lag 证据软过渡宽度。最终混合权重从 minDistance 到 (minDistance+blendRange) 平滑上升到 1。")]
    [Min(0f)] public float lagEvidenceBlendRange = 0.03f;

    [Header("Gizmos")]
    public bool drawTrajectory = true;
    [Range(8, 256)] public int segments = 64;
    public bool drawBothSides = true;
    public Color leftColor = new Color(0.2f, 0.85f, 1f, 1f);
    public Color rightColor = new Color(0.15f, 0.7f, 1f, 1f);
    public Color outerCurveColor = new Color(1f, 0.55f, 0.15f, 1f);
    public Color innerCurveColor = new Color(0.2f, 0.85f, 1f, 1f);
    public bool drawBasisAxes = true;
    public float basisAxisLength = 0.35f;
    public bool drawOuterCurveBasisAxes = true;
    public float outerBasisAxisLength = 0.3f;
    public bool drawLagEvidenceGizmos = false;
    public float lagEvidenceSphereRadius = 0.02f;
    public bool drawPlaneLimitForwardDebug = true;
    [Tooltip("RawForward 调试射线长度。")]
    [Min(0.01f)] public float rawForwardVizLength = 0.3f;
    [Tooltip("轨迹上方向(Up)调试射线长度。")]
    [Min(0.01f)] public float upDirectionVizLength = 0.3f;
    public Color planeRawForwardColor = new Color(1f, 0.3f, 0.3f, 1f);
    public Color planeClampedForwardColor = new Color(0.2f, 1f, 0.4f, 1f);
    public Color upDirectionColor = new Color(0.9f, 0.35f, 1f, 1f);

    private float RestPhaseRad => Mathf.Repeat(restPhase01, 1f) * Mathf.PI * 2f;
    private float TopVertexRad => 0.5f * Mathf.PI;
    private Quaternion leftBasis = Quaternion.identity;
    private Quaternion rightBasis = Quaternion.identity;
    private bool leftLagActive;
    private bool rightLagActive;
    private bool leftHasPrevDynamic;
    private bool rightHasPrevDynamic;
    private Quaternion leftDynamicBasis = Quaternion.identity;
    private Quaternion rightDynamicBasis = Quaternion.identity;
    private float leftActivation01;
    private float rightActivation01;
    private float leftActivationVel;
    private float rightActivationVel;
    private Vector3 prevLeftF = Vector3.forward;
    private Vector3 prevLeftN = Vector3.up;
    private Vector3 prevRightF = Vector3.forward;
    private Vector3 prevRightN = Vector3.up;
    private Vector3 debugLeftRawForward = Vector3.forward;
    private Vector3 debugLeftClampedForward = Vector3.forward;
    private Vector3 debugRightRawForward = Vector3.forward;
    private Vector3 debugRightClampedForward = Vector3.forward;
    private Vector3 debugLeftUp = Vector3.up;
    private Vector3 debugRightUp = Vector3.up;
    private bool debugLeftWasClamped;
    private bool debugRightWasClamped;

    private bool TryGetDefaultBasis(out Quaternion basis, out Vector3 center)
    {
        basis = Quaternion.identity;
        center = Vector3.zero;
        if (!source || !source.leftShoulder || !source.rightShoulder) return false;

        center = 0.5f * (source.leftShoulder.position + source.rightShoulder.position);

        Vector3 r = source.rightShoulder.position - source.leftShoulder.position;
        if (r.sqrMagnitude < 1e-10f) r = source.StableBasis * Vector3.right;
        if (r.sqrMagnitude < 1e-10f) r = Vector3.right;
        r.Normalize();

        Vector3 stableF = source.StableBasis * Vector3.forward;
        if (stableF.sqrMagnitude < 1e-10f) stableF = transform.forward;
        if (stableF.sqrMagnitude < 1e-10f) stableF = Vector3.forward;
        stableF.Normalize();

        // project forward onto plane orthogonal to shoulder line
        Vector3 f = stableF - Vector3.Dot(stableF, r) * r;
        if (f.sqrMagnitude < 1e-10f)
        {
            Vector3 stableU = source.StableBasis * Vector3.up;
            if (stableU.sqrMagnitude < 1e-10f) stableU = Vector3.up;
            f = Vector3.Cross(stableU, r);
        }
        if (f.sqrMagnitude < 1e-10f) f = Vector3.Cross(Vector3.up, r);
        if (f.sqrMagnitude < 1e-10f) f = Vector3.forward;
        f.Normalize();

        Vector3 n = Vector3.Cross(r, f);
        if (n.sqrMagnitude < 1e-10f) n = source.StableBasis * Vector3.up;
        if (n.sqrMagnitude < 1e-10f) n = Vector3.up;
        n.Normalize();

        // Keep the same handedness as dynamic basis construction to avoid switch-time full spins.
        Vector3 fFinal = Vector3.Cross(r, n);
        if (fFinal.sqrMagnitude < 1e-10f) fFinal = f;
        fFinal.Normalize();

        basis = Quaternion.LookRotation(fFinal, n);
        return true;
    }

    private static float ExpAlpha(float sharpness, float dt)
    {
        return 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, dt));
    }

    private void SetDebugForwards(bool isLeft, Vector3 rawForward, Vector3 constrainedForward, bool wasClamped)
    {
        if (isLeft)
        {
            debugLeftRawForward = rawForward;
            debugLeftClampedForward = constrainedForward;
            debugLeftWasClamped = wasClamped;
        }
        else
        {
            debugRightRawForward = rawForward;
            debugRightClampedForward = constrainedForward;
            debugRightWasClamped = wasClamped;
        }
    }

    private void SetDebugUp(bool isLeft, Vector3 up)
    {
        if (isLeft) debugLeftUp = up;
        else debugRightUp = up;
    }

    private Vector3 GetRestWorld(bool isLeft)
    {
        Transform rp = isLeft ? leftRestPoint : rightRestPoint;
        if (rp) return rp.position;
        if (!source) return transform.position;
        Transform sh = isLeft ? source.leftShoulder : source.rightShoulder;
        return sh ? sh.position : source.StableCenter;
    }

    private Vector3 GetShoulderRightAxis(Quaternion fallbackBasis)
    {
        if (source && source.leftShoulder && source.rightShoulder)
        {
            Vector3 r = source.rightShoulder.position - source.leftShoulder.position;
            if (r.sqrMagnitude > 1e-10f) return r.normalized;
        }
        Vector3 rf = fallbackBasis * Vector3.right;
        if (rf.sqrMagnitude < 1e-10f) rf = Vector3.right;
        return rf.normalized;
    }

    private bool TryGetPerpendicularForwardFromLag(bool isLeft, Vector3 shoulderAxis, out Vector3 forwardOut)
    {
        forwardOut = Vector3.zero;
        if (!source || !source.leftShoulder || !source.rightShoulder) return false;

        Vector3 lag = isLeft ? source.WorldLeftPoint : source.WorldRightPoint;
        Vector3 lineCenter = 0.5f * (source.leftShoulder.position + source.rightShoulder.position);
        Vector3 closestOnLine = lineCenter + Vector3.Dot(lag - lineCenter, shoulderAxis) * shoulderAxis;

        // Force a fixed sign: from lag point toward the shoulder-line perpendicular foot.
        Vector3 f = closestOnLine - lag;
        f = f - Vector3.Dot(f, shoulderAxis) * shoulderAxis;
        if (f.sqrMagnitude < dynamicForwardMin * dynamicForwardMin) return false;
        forwardOut = f.normalized;
        return true;
    }

    private bool IsLagUnrecovered(bool isLeft)
    {
        if (!source) return false;
        Vector3 lag = isLeft ? source.WorldLeftPoint : source.WorldRightPoint;
        Vector3 target = GetRestWorld(isLeft);
        float d2 = (lag - target).sqrMagnitude;
        float enter = Mathf.Max(1e-6f, lagUnrecoveredDistance);
        float exit = Mathf.Max(1e-6f, enter * Mathf.Clamp01(lagRecoverRatio));
        float enter2 = enter * enter;
        float exit2 = exit * exit;

        if (isLeft)
        {
            if (!leftLagActive) leftLagActive = d2 > enter2;
            else leftLagActive = d2 > exit2;
            return leftLagActive;
        }

        if (!rightLagActive) rightLagActive = d2 > enter2;
        else rightLagActive = d2 > exit2;
        return rightLagActive;
    }

    private Vector3 GetIdleLagPoint(bool isLeft)
    {
        if (!source) return Vector3.zero;
        Quaternion basis = source.StableBasis;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        Transform shoulder = isLeft ? source.leftShoulder : source.rightShoulder;
        if (!shoulder) return source.StableCenter;
        return shoulder.position + side * source.armOffset;
    }

    private float GetLagEvidence01(bool isLeft)
    {
        if (!source) return 0f;
        Vector3 lag = isLeft ? source.WorldLeftPoint : source.WorldRightPoint;
        Vector3 idle = GetIdleLagPoint(isLeft);
        float d = Vector3.Distance(lag, idle);
        float minD = Mathf.Max(0f, lagEvidenceMinDistance);
        float maxD = minD + Mathf.Max(0f, lagEvidenceBlendRange);
        if (maxD <= minD + 1e-6f)
            return d >= minD ? 1f : 0f;

        float t = Mathf.InverseLerp(minD, maxD, d);
        // smoothstep for softer transition near the threshold.
        return t * t * (3f - 2f * t);
    }

    private bool TryBuildDynamicBasisForSide(
        bool isLeft,
        Quaternion defaultBasis,
        bool hasPrev,
        Vector3 prevF,
        Vector3 prevN,
        out Vector3 fOut,
        out Vector3 nOut)
    {
        fOut = Vector3.zero;
        nOut = Vector3.zero;
        if (!source || !source.leftShoulder || !source.rightShoulder) return false;

        Vector3 r = source.rightShoulder.position - source.leftShoulder.position;
        if (r.sqrMagnitude < 1e-10f) r = defaultBasis * Vector3.right;
        if (r.sqrMagnitude < 1e-10f) r = Vector3.right;
        r.Normalize();

        if (!TryGetPerpendicularForwardFromLag(isLeft, r, out Vector3 f))
            return false;

        //if (hasPrev && Vector3.Dot(f, prevF) < 0f) f = -f;
        Vector3 debugRawForward = f;

        Vector3 n = Vector3.Cross(r, f);
        if (n.sqrMagnitude < 1e-8f) n = defaultBasis * Vector3.up;
        if (n.sqrMagnitude < 1e-8f) n = Vector3.up;
        n.Normalize();

        Vector3 fFinal = Vector3.Cross(r, n);
        if (fFinal.sqrMagnitude < 1e-8f) fFinal = f;
        fFinal.Normalize();

        SetDebugForwards(isLeft, debugRawForward, fFinal, false);
        SetDebugUp(isLeft, n);

        Vector3 stableUp = defaultBasis * Vector3.up;
        if (hasPrev)
        {
            float keep = Vector3.Dot(fFinal, prevF) + 0.35f * Vector3.Dot(n, prevN) + 0.35f * Vector3.Dot(n, stableUp);
            float flip = Vector3.Dot(-fFinal, prevF) + 0.35f * Vector3.Dot(-n, prevN) + 0.35f * Vector3.Dot(-n, stableUp);
            if (flip > keep)
            {
                fFinal = -fFinal;
                n = -n;
            }
        }
        else if (Vector3.Dot(n, stableUp) < 0f)
        {
            fFinal = -fFinal;
            n = -n;
        }

        fOut = fFinal;
        nOut = n;
        return true;
    }

    private void UpdateSideBases(float dt)
    {
        if (!TryGetDefaultBasis(out Quaternion defaultBasis, out _)) return;
        if (leftBasis == Quaternion.identity) leftBasis = defaultBasis;
        if (rightBasis == Quaternion.identity) rightBasis = defaultBasis;
        if (leftDynamicBasis == Quaternion.identity) leftDynamicBasis = defaultBasis;
        if (rightDynamicBasis == Quaternion.identity) rightDynamicBasis = defaultBasis;

        bool leftOuterActive = enableDynamicOuterPlane && source && source.isLeftOuter && IsLagUnrecovered(true);
        bool rightOuterActive = enableDynamicOuterPlane && source && source.isRightOuter && IsLagUnrecovered(false);

        float leftEvidence = leftOuterActive ? GetLagEvidence01(true) : 0f;
        float rightEvidence = rightOuterActive ? GetLagEvidence01(false) : 0f;

        float leftTargetActivation = leftOuterActive ? leftEvidence : 0f;
        float rightTargetActivation = rightOuterActive ? rightEvidence : 0f;

        float leftSmoothTime = leftTargetActivation > leftActivation01 ? activationInTime : activationOutTime;
        float rightSmoothTime = rightTargetActivation > rightActivation01 ? activationInTime : activationOutTime;
        leftActivation01 = Mathf.SmoothDamp(leftActivation01, leftTargetActivation, ref leftActivationVel, leftSmoothTime, Mathf.Infinity, dt);
        rightActivation01 = Mathf.SmoothDamp(rightActivation01, rightTargetActivation, ref rightActivationVel, rightSmoothTime, Mathf.Infinity, dt);
        leftActivation01 = Mathf.Clamp01(leftActivation01);
        rightActivation01 = Mathf.Clamp01(rightActivation01);

        if (leftOuterActive &&
            TryBuildDynamicBasisForSide(true, defaultBasis, leftHasPrevDynamic, prevLeftF, prevLeftN, out Vector3 lf, out Vector3 ln))
        {
            Quaternion dynamicTarget = Quaternion.LookRotation(lf, ln);
            leftDynamicBasis = Quaternion.Slerp(leftDynamicBasis, dynamicTarget, ExpAlpha(dynamicTargetSharpness, dt));
            prevLeftF = lf;
            prevLeftN = ln;
            leftHasPrevDynamic = leftActivation01 > 0.001f;
        }
        else if (leftOuterActive)
        {
            // Keep last dynamic target; activation weight will decide how much to use it.
            leftHasPrevDynamic = false;
        }
        else
        {
            leftHasPrevDynamic = false;
        }
        Quaternion leftTargetBasis = Quaternion.Slerp(defaultBasis, leftDynamicBasis, leftActivation01);
        float leftTurnSpeed = Mathf.Lerp(returnTurnSpeedDegPerSec, activeTurnSpeedDegPerSec, leftActivation01);
        leftBasis = Quaternion.RotateTowards(leftBasis, leftTargetBasis, Mathf.Max(1f, leftTurnSpeed) * dt);

        if (rightOuterActive &&
            TryBuildDynamicBasisForSide(false, defaultBasis, rightHasPrevDynamic, prevRightF, prevRightN, out Vector3 rf, out Vector3 rn))
        {
            Quaternion dynamicTarget = Quaternion.LookRotation(rf, rn);
            rightDynamicBasis = Quaternion.Slerp(rightDynamicBasis, dynamicTarget, ExpAlpha(dynamicTargetSharpness, dt));
            prevRightF = rf;
            prevRightN = rn;
            rightHasPrevDynamic = rightActivation01 > 0.001f;
        }
        else if (rightOuterActive)
        {
            // Keep last dynamic target; activation weight will decide how much to use it.
            rightHasPrevDynamic = false;
        }
        else
        {
            rightHasPrevDynamic = false;
        }
        Quaternion rightTargetBasis = Quaternion.Slerp(defaultBasis, rightDynamicBasis, rightActivation01);
        float rightTurnSpeed = Mathf.Lerp(returnTurnSpeedDegPerSec, activeTurnSpeedDegPerSec, rightActivation01);
        rightBasis = Quaternion.RotateTowards(rightBasis, rightTargetBasis, Mathf.Max(1f, rightTurnSpeed) * dt);
    }

    private void LateUpdate()
    {
        float dt = Application.isPlaying ? Time.deltaTime : (1f / 60f);
        if (dt < 1e-6f) dt = 1f / 60f;
        UpdateSideBases(dt);
    }

    private Vector3 EvalStrokeLocalRaw(float tRad)
    {
        float c = Mathf.Cos(tRad);
        float s = Mathf.Sin(tRad);

        float x = strokeAmpRight * c;
        float shape = 1f - strokeTeardrop * (c + 1f) * 0.5f;
        float y = strokeAmpUp * s * Mathf.Max(0.15f, shape);
        float z = strokeAmpForward * (0.65f * Mathf.Max(0f, c) - 0.35f * Mathf.Max(0f, -c));

        return strokeSwapYZ ? new Vector3(x, z, y) : new Vector3(x, y, z);
    }

    private Vector3 EvalStrokeLocalRested(float tRad)
    {
        float restRad = restPointIsTopVertex ? TopVertexRad : RestPhaseRad;
        Vector3 restOffset = EvalStrokeLocalRaw(restRad);
        return EvalStrokeLocalRaw(tRad) - restOffset;
    }

    private bool ShouldMirrorLocalX(Vector3 origin, Quaternion basis)
    {
        // Same mirror logic as DynamicIndirectIK_Neo: choose flip by current world side.
        Vector3 r = (source && source.rightShoulder && source.leftShoulder)
            ? (source.rightShoulder.position - source.leftShoulder.position)
            : (basis * Vector3.right);
        if (r.sqrMagnitude < 1e-10f) r = Vector3.right;
        r.Normalize();

        Vector3 basisRight = basis * Vector3.right;
        if (basisRight.sqrMagnitude < 1e-10f) basisRight = r;
        basisRight.Normalize();

        bool basisRightReversed = Vector3.Dot(basisRight, r) < 0f;
        Vector3 center = source ? source.StableCenter : transform.position;
        bool worldLeftSide = Vector3.Dot(origin - center, r) < 0f;

        // Preserve legacy option: if mirrorRightSide is disabled, never mirror.
        if (!mirrorRightSide) return false;
        return worldLeftSide ? basisRightReversed : !basisRightReversed;
    }

    private Vector3 EvalStrokeLocalRestedMirrored(Vector3 origin, Quaternion basis, float tRad)
    {
        Vector3 local = EvalStrokeLocalRested(tRad);
        if (ShouldMirrorLocalX(origin, basis)) local.x *= -1f;
        return local;
    }

    public Vector3 EvaluateWorldPoint(bool isLeft, float t01)
    {
        if (leftBasis == Quaternion.identity || rightBasis == Quaternion.identity)
        {
            if (!TryGetDefaultBasis(out Quaternion defaultBasis, out _)) return Vector3.zero;
            leftBasis = defaultBasis;
            rightBasis = defaultBasis;
        }
        Transform rp = isLeft ? leftRestPoint : rightRestPoint;
        if (!rp) return Vector3.zero;

        float tRad = Mathf.Repeat(t01, 1f) * Mathf.PI * 2f;
        Quaternion basis = isLeft ? leftBasis : rightBasis;
        return rp.position + basis * EvalStrokeLocalRestedMirrored(rp.position, basis, tRad);
    }

    private void DrawCurve(bool isLeft, Quaternion basis, Color col)
    {
        Transform rp = isLeft ? leftRestPoint : rightRestPoint;
        if (!rp || segments < 8) return;

        Gizmos.color = col;
        float step = (Mathf.PI * 2f) / segments;
        float startT = restPointIsTopVertex ? TopVertexRad : RestPhaseRad;
        Vector3 prev = rp.position + basis * EvalStrokeLocalRestedMirrored(rp.position, basis, startT);
        for (int i = 1; i <= segments; i++)
        {
            float t = startT + i * step;
            Vector3 p = rp.position + basis * EvalStrokeLocalRestedMirrored(rp.position, basis, t);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    private void OnDrawGizmos()
    {
        if (!drawTrajectory) return;
        if (!TryGetDefaultBasis(out Quaternion defaultBasis, out Vector3 center)) return;
        if (!Application.isPlaying)
        {
            leftBasis = defaultBasis;
            rightBasis = defaultBasis;
        }

        if (drawBasisAxes)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawRay(center, defaultBasis * Vector3.right * basisAxisLength);
            Gizmos.color = Color.green;
            Gizmos.DrawRay(center, defaultBasis * Vector3.up * basisAxisLength);
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(center, defaultBasis * Vector3.forward * basisAxisLength);
        }

        bool leftOuter = source && source.isLeftOuter;
        bool rightOuter = source && source.isRightOuter;
        Color colL = leftOuter ? outerCurveColor : innerCurveColor;
        Color colR = rightOuter ? outerCurveColor : innerCurveColor;

        // Fallback to user colors only when no explicit outer/inner state is active.
        if (!leftOuter && !rightOuter)
        {
            colL = leftColor;
            colR = rightColor;
        }

        DrawCurve(true, leftBasis, colL);
        if (drawBothSides) DrawCurve(false, rightBasis, colR);

        if (drawOuterCurveBasisAxes && source)
        {
            bool hasOuter = source.isLeftOuter || source.isRightOuter;
            if (hasOuter)
            {
                bool outerIsLeft = source.isLeftOuter;
                Quaternion ob = outerIsLeft ? leftBasis : rightBasis;
                Vector3 o = outerIsLeft ? GetRestWorld(true) : GetRestWorld(false);
                Gizmos.color = Color.red;
                Gizmos.DrawRay(o, ob * Vector3.right * outerBasisAxisLength);
                Gizmos.color = Color.green;
                Gizmos.DrawRay(o, ob * Vector3.up * outerBasisAxisLength);
                Gizmos.color = Color.blue;
                Gizmos.DrawRay(o, ob * Vector3.forward * outerBasisAxisLength);
            }
        }

        if (drawPlaneLimitForwardDebug)
        {
            Vector3 lO = GetRestWorld(true);
            Vector3 rO = GetRestWorld(false);
            float rawLen = Mathf.Max(0.01f, rawForwardVizLength);
            float clampedLen = Mathf.Max(0.01f, outerBasisAxisLength);
            float upLen = Mathf.Max(0.01f, upDirectionVizLength);

            Gizmos.color = planeRawForwardColor;
            if (debugLeftRawForward.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(lO, debugLeftRawForward.normalized * rawLen);
            if (debugRightRawForward.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(rO, debugRightRawForward.normalized * rawLen);

            Gizmos.color = planeClampedForwardColor;
            if (debugLeftClampedForward.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(lO, debugLeftClampedForward.normalized * clampedLen);
            if (debugRightClampedForward.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(rO, debugRightClampedForward.normalized * clampedLen);

            Gizmos.color = upDirectionColor;
            if (debugLeftUp.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(lO, debugLeftUp.normalized * upLen);
            if (debugRightUp.sqrMagnitude > 1e-10f)
                Gizmos.DrawRay(rO, debugRightUp.normalized * upLen);

            if (debugLeftWasClamped)
            {
                Gizmos.color = new Color(1f, 0.25f, 0.25f, 0.9f);
                Gizmos.DrawSphere(lO, lagEvidenceSphereRadius * 1.2f);
            }
            if (debugRightWasClamped)
            {
                Gizmos.color = new Color(1f, 0.25f, 0.25f, 0.9f);
                Gizmos.DrawSphere(rO, lagEvidenceSphereRadius * 1.2f);
            }
        }

        if (drawLagEvidenceGizmos && source)
        {
            Vector3 idleL = GetIdleLagPoint(true);
            Vector3 idleR = GetIdleLagPoint(false);
            Vector3 lagL = source.WorldLeftPoint;
            Vector3 lagR = source.WorldRightPoint;

            bool enoughL = GetLagEvidence01(true) > 0.5f;
            bool enoughR = GetLagEvidence01(false) > 0.5f;

            Gizmos.color = enoughL ? new Color(1f, 0.7f, 0.15f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f);
            Gizmos.DrawSphere(idleL, lagEvidenceSphereRadius);
            Gizmos.DrawLine(idleL, lagL);

            Gizmos.color = enoughR ? new Color(1f, 0.7f, 0.15f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f);
            Gizmos.DrawSphere(idleR, lagEvidenceSphereRadius);
            Gizmos.DrawLine(idleR, lagR);
        }
    }
}
