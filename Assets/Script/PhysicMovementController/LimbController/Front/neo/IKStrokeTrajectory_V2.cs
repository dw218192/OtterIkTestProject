using UnityEngine;

/// <summary>
/// IKStrokeTrajectory_V2
///
/// Core idea:
/// 1) Keep front estimation from lag->shoulder-line perpendicular.
/// 2) Use shoulder->perpendicular-foot as per-side horizontal extension axis.
/// 3) Expose up-direction mode:
///    - true: keep natural per-side cross result (left/right up can be opposite)
///    - false: flip right-side up for mirrored drawing symmetry.
/// </summary>
[ExecuteAlways]
public class IKStrokeTrajectory_V2 : MonoBehaviour
{
    [Header("Input")]
    public DynamicIndirectIk_V2 source;
    public Transform leftRestPoint;
    public Transform rightRestPoint;

    [Header("Trajectory Shape (local frame)")]
    public float strokeAmpHorizontal = 0.25f;
    public float strokeAmpUp = 0.15f;
    public float strokeAmpForward = 0.20f;
    [Range(0f, 1f)] public float strokeTeardrop = 0.6f;
    [Tooltip("Reserved for future out-of-plane thickness. Current V2 keeps trajectory in horizontal-forward plane.")]
    public float outOfPlaneThickness = 0f;

    [Header("Rest Phase")]
    [Tooltip("Phase pinned to rest point. Adjust manually to choose which side is the tip (commonly around 0.5).")]
    [Range(0f, 1f)] public float restPhase01 = 0.25f;

    [Header("Dynamic Activation")]
    public bool enableDynamicOuterPlane = true;
    [Min(0.0001f)] public float lagUnrecoveredDistance = 0.03f;
    [Range(0.1f, 1f)] public float lagRecoverRatio = 0.65f;
    [Min(0.01f)] public float activationInTime = 0.18f;
    [Min(0.01f)] public float activationOutTime = 0.24f;
    [Min(1f)] public float activeTurnSpeedDegPerSec = 420f;
    [Min(1f)] public float returnTurnSpeedDegPerSec = 300f;
    [Min(0f)] public float dynamicTargetSharpness = 20f;
    [Min(1e-6f)] public float dynamicForwardMin = 1e-4f;
    [Min(0f)] public float lagEvidenceMinDistance = 0.015f;
    [Min(0f)] public float lagEvidenceBlendRange = 0.03f;
    [Tooltip("仅影响Gizmos轨迹绘制：true时左侧曲线本地Y偏移取反(镜像上下)，不改变front/right/up求解。")]
    public bool mirrorCurveUpByFlippingLeft = false;

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
    public bool drawFrameDebug = true;
    public bool drawIdleDynamicSolveDebug = true;
    public float frontVizLength = 0.3f;
    public float upVizLength = 0.3f;
    public float horizontalVizLength = 0.3f;
    public Color frontDebugColor = new Color(1f, 0.25f, 0.25f, 1f);
    public Color upDebugColor = new Color(0.9f, 0.35f, 1f, 1f);
    public Color horizontalDebugColor = new Color(0.25f, 1f, 0.55f, 1f);
    public Color idleFrontDebugColor = new Color(0.2f, 0.75f, 1f, 1f);
    public Color idleHorizontalDebugColor = new Color(0.2f, 1f, 0.8f, 1f);
    public Color dynamicFrontDebugColor = new Color(1f, 0.35f, 0.2f, 1f);
    public Color dynamicHorizontalDebugColor = new Color(1f, 0.85f, 0.2f, 1f);
    public bool drawPerpendicularFoot = true;
    public float footSphereRadius = 0.015f;
    [Header("Debug Logs")]
    public bool logIdleFrontDiagnostics = false;
    [Range(1, 120)] public int idleFrontLogEveryNFrames = 20;

    private float RestPhaseRad => Mathf.Repeat(restPhase01, 1f) * Mathf.PI * 2f;

    private Quaternion leftBasis = Quaternion.identity;
    private Quaternion rightBasis = Quaternion.identity;
    private Quaternion leftDynamicBasis = Quaternion.identity;
    private Quaternion rightDynamicBasis = Quaternion.identity;
    private bool leftLagActive;
    private bool rightLagActive;
    private bool leftHasPrevDynamic;
    private bool rightHasPrevDynamic;
    private float leftActivation01;
    private float rightActivation01;
    private float leftActivationVel;
    private float rightActivationVel;
    private Vector3 prevLeftF = Vector3.forward;
    private Vector3 prevRightF = Vector3.forward;

    private Vector3 debugLeftFront = Vector3.forward;
    private Vector3 debugRightFront = Vector3.forward;
    private Vector3 debugLeftUp = Vector3.up;
    private Vector3 debugRightUp = Vector3.up;
    private Vector3 debugLeftHorizontal = Vector3.right;
    private Vector3 debugRightHorizontal = Vector3.right;
    private Vector3 debugLeftFoot;
    private Vector3 debugRightFoot;
    private Vector3 debugIdleLeftFront = Vector3.forward;
    private Vector3 debugIdleRightFront = Vector3.forward;
    private Vector3 debugIdleLeftHorizontal = Vector3.right;
    private Vector3 debugIdleRightHorizontal = Vector3.right;
    private Vector3 debugDynamicLeftFront = Vector3.forward;
    private Vector3 debugDynamicRightFront = Vector3.forward;
    private Vector3 debugDynamicLeftHorizontal = Vector3.right;
    private Vector3 debugDynamicRightHorizontal = Vector3.right;
    private bool hasDynamicLeftSolveDebug;
    private bool hasDynamicRightSolveDebug;
    private Vector3 lastLoggedIdleLeftFront = Vector3.forward;
    private Vector3 lastLoggedIdleRightFront = Vector3.forward;

    // Runtime solved frame vectors for external controllers (e.g. IKStrokeController consistency gate).
    public Vector3 LeftSolvedFront => debugLeftFront;
    public Vector3 RightSolvedFront => debugRightFront;
    public Vector3 LeftSolvedUp => debugLeftUp;
    public Vector3 RightSolvedUp => debugRightUp;

    private static float ExpAlpha(float sharpness, float dt)
    {
        return 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, dt));
    }

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

        // StableBasis forward in current rig points to tail; flip to use body-front as trajectory forward reference.
        Vector3 stableF = -(source.StableBasis * Vector3.forward);
        if (stableF.sqrMagnitude < 1e-10f) stableF = transform.forward;
        if (stableF.sqrMagnitude < 1e-10f) stableF = Vector3.forward;
        stableF.Normalize();

        Vector3 f = stableF - Vector3.Dot(stableF, r) * r;
        if (f.sqrMagnitude < 1e-10f) f = Vector3.Cross(Vector3.up, r);
        if (f.sqrMagnitude < 1e-10f) f = Vector3.forward;
        f.Normalize();

        Vector3 u = Vector3.Cross(r, f);
        if (u.sqrMagnitude < 1e-10f) u = source.StableBasis * Vector3.up;
        if (u.sqrMagnitude < 1e-10f) u = Vector3.up;
        u.Normalize();

        f = Vector3.Cross(r, u).normalized;
        basis = Quaternion.LookRotation(f, u);
        return true;
    }

    private bool TryGetIdleBasisForSide(bool isLeft, Quaternion defaultBasis, out Quaternion basis)
    {
        basis = defaultBasis;
        if (!source || !source.leftShoulder || !source.rightShoulder) return false;

        Vector3 leftShoulderPos = source.leftShoulder.position;
        Vector3 rightShoulderPos = source.rightShoulder.position;
        Vector3 center = 0.5f * (leftShoulderPos + rightShoulderPos);
        Vector3 shoulderPos = isLeft ? leftShoulderPos : rightShoulderPos;

        // Per request: idle horizontal comes from shoulder-line center to the side shoulder.
        Vector3 horizontal = shoulderPos - center;
        if (horizontal.sqrMagnitude < 1e-10f)
        {
            Vector3 rFallback = defaultBasis * Vector3.right;
            if (rFallback.sqrMagnitude < 1e-10f) rFallback = Vector3.right;
            horizontal = isLeft ? -rFallback : rFallback;
        }
        horizontal.Normalize();

        Vector3 front = defaultBasis * Vector3.forward;
        front = front - Vector3.Dot(front, horizontal) * horizontal;
        if (front.sqrMagnitude < 1e-10f) front = Vector3.Cross(Vector3.up, horizontal);
        if (front.sqrMagnitude < 1e-10f) front = Vector3.forward;
        front.Normalize();

        Vector3 up = Vector3.Cross(horizontal, front);
        if (up.sqrMagnitude < 1e-10f) up = defaultBasis * Vector3.up;
        if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
        up.Normalize();

        // Keep orthonormal frame while preserving side-specific horizontal.
        front = Vector3.Cross(up, horizontal);
        if (front.sqrMagnitude < 1e-10f) front = defaultBasis * Vector3.forward;
        front.Normalize();
        up = Vector3.Cross(horizontal, front).normalized;

        basis = Quaternion.LookRotation(front, up);
        return true;
    }

    private Vector3 GetRestWorld(bool isLeft)
    {
        Transform rp = isLeft ? leftRestPoint : rightRestPoint;
        if (rp) return rp.position;
        if (!source) return transform.position;
        Transform sh = isLeft ? source.leftShoulder : source.rightShoulder;
        return sh ? sh.position : source.StableCenter;
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
            leftLagActive = !leftLagActive ? d2 > enter2 : d2 > exit2;
            return leftLagActive;
        }

        rightLagActive = !rightLagActive ? d2 > enter2 : d2 > exit2;
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
        if (maxD <= minD + 1e-6f) return d >= minD ? 1f : 0f;

        float t = Mathf.InverseLerp(minD, maxD, d);
        return t * t * (3f - 2f * t);
    }

    private void UpdateIdleDebugForSide(bool isLeft, Quaternion defaultBasis)
    {
        Vector3 frontReference = defaultBasis * Vector3.forward;
        Vector3 front = frontReference;
        Vector3 horizontal = defaultBasis * Vector3.right;
        bool usedProjectionFallback = false;
        bool usedFinalFallback = false;
        float projectedFrontMag = 0f;

        Vector3 foot = Vector3.zero;
        if (source && source.leftShoulder && source.rightShoulder)
        {
            Vector3 leftShoulderPos = source.leftShoulder.position;
            Vector3 rightShoulderPos = source.rightShoulder.position;
            Vector3 shoulderAxis = rightShoulderPos - leftShoulderPos;
            if (shoulderAxis.sqrMagnitude < 1e-10f) shoulderAxis = defaultBasis * Vector3.right;
            if (shoulderAxis.sqrMagnitude < 1e-10f) shoulderAxis = Vector3.right;
            shoulderAxis.Normalize();

            Vector3 center = 0.5f * (leftShoulderPos + rightShoulderPos);
            Vector3 shoulderPos = isLeft ? leftShoulderPos : rightShoulderPos;
            horizontal = shoulderPos - center;
            if (horizontal.sqrMagnitude < 1e-10f) horizontal = isLeft ? -shoulderAxis : shoulderAxis;
            horizontal.Normalize();

            front = front - Vector3.Dot(front, horizontal) * horizontal;
            projectedFrontMag = front.magnitude;
            if (front.sqrMagnitude < 1e-10f) front = Vector3.Cross(Vector3.up, horizontal);
            if (front.sqrMagnitude < 1e-10f) usedProjectionFallback = true;
            if (front.sqrMagnitude < 1e-10f) front = Vector3.forward;
            if (front.sqrMagnitude < 1e-10f) usedFinalFallback = true;
            front.Normalize();

            Vector3 lag = isLeft ? source.WorldLeftPoint : source.WorldRightPoint;
            Vector3 shoulderLineCenter = 0.5f * (leftShoulderPos + rightShoulderPos);
            foot = shoulderLineCenter + Vector3.Dot(lag - shoulderLineCenter, shoulderAxis) * shoulderAxis;
        }
        else
        {
            if (horizontal.sqrMagnitude < 1e-10f) horizontal = Vector3.right;
            horizontal.Normalize();
            front = front - Vector3.Dot(front, horizontal) * horizontal;
            projectedFrontMag = front.magnitude;
            if (front.sqrMagnitude < 1e-10f) front = Vector3.forward;
            if (front.sqrMagnitude < 1e-10f) usedFinalFallback = true;
            front.Normalize();
        }

        Vector3 up = Vector3.Cross(horizontal, front);
        if (up.sqrMagnitude < 1e-10f) up = defaultBasis * Vector3.up;
        if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
        up.Normalize();

        if (isLeft)
        {
            debugLeftFront = front;
            debugLeftUp = up;
            debugLeftHorizontal = horizontal;
            debugLeftFoot = foot;
            debugIdleLeftFront = front;
            debugIdleLeftHorizontal = horizontal;
            lastLoggedIdleLeftFront = front;
        }
        else
        {
            debugRightFront = front;
            debugRightUp = up;
            debugRightHorizontal = horizontal;
            debugRightFoot = foot;
            debugIdleRightFront = front;
            debugIdleRightHorizontal = horizontal;
            lastLoggedIdleRightFront = front;
        }

        if (logIdleFrontDiagnostics &&
            Application.isPlaying &&
            idleFrontLogEveryNFrames > 0 &&
            (Time.frameCount % idleFrontLogEveryNFrames == 0))
        {
            float refDot = Vector3.Dot(front.normalized, frontReference.normalized);
            float crossSideSign = Vector3.Dot(Vector3.Cross(Vector3.up, horizontal), front.normalized);
            Debug.Log(
                $"[IKStrokeTrajectory_V2][IdleFrontDiag][{(isLeft ? "L" : "R")}] " +
                $"projMag={projectedFrontMag:F5}, projFallback={usedProjectionFallback}, finalFallback={usedFinalFallback}, " +
                $"dot(front,refF)={refDot:F4}, crossSign={crossSideSign:F4}, " +
                $"front={front.normalized}, horizontal={horizontal.normalized}",
                this);

            // After both sides updated this frame, emit a compact left-right comparison.
            if (!isLeft)
            {
                float lrDot = Vector3.Dot(lastLoggedIdleLeftFront.normalized, lastLoggedIdleRightFront.normalized);
                Debug.Log($"[IKStrokeTrajectory_V2][IdleFrontDiag][LR] dot(L,R)={lrDot:F4}", this);
            }
        }
    }

    private bool TryBuildDynamicFrameForSide(
        bool isLeft,
        Quaternion defaultBasis,
        bool hasPrev,
        Vector3 prevFront,
        out Vector3 frontOut,
        out Vector3 upOut)
    {
        frontOut = Vector3.zero;
        upOut = Vector3.zero;
        if (!source || !source.leftShoulder || !source.rightShoulder) return false;

        Vector3 leftShoulderPos = source.leftShoulder.position;
        Vector3 rightShoulderPos = source.rightShoulder.position;
        Vector3 shoulderAxis = rightShoulderPos - leftShoulderPos;
        if (shoulderAxis.sqrMagnitude < 1e-10f) shoulderAxis = defaultBasis * Vector3.right;
        if (shoulderAxis.sqrMagnitude < 1e-10f) shoulderAxis = Vector3.right;
        shoulderAxis.Normalize();

        Vector3 lag = isLeft ? source.WorldLeftPoint : source.WorldRightPoint;
        Vector3 shoulderLineCenter = 0.5f * (leftShoulderPos + rightShoulderPos);
        Vector3 foot = shoulderLineCenter + Vector3.Dot(lag - shoulderLineCenter, shoulderAxis) * shoulderAxis;

        // Step1: stable front from lag->perpendicular-foot.
        Vector3 front = foot - lag;
        front = front - Vector3.Dot(front, shoulderAxis) * shoulderAxis;
        if (front.sqrMagnitude < dynamicForwardMin * dynamicForwardMin) return false;
        front.Normalize();

        if (hasPrev && Vector3.Dot(front, prevFront) < 0f) front = -front;

        // Step2: per-side horizontal extension from shoulder->foot.
        Vector3 shoulderPos = isLeft ? leftShoulderPos : rightShoulderPos;
        Vector3 horizontal = foot - shoulderPos;
        if (horizontal.sqrMagnitude < 1e-10f) horizontal = isLeft ? shoulderAxis : -shoulderAxis;
        horizontal = horizontal - Vector3.Dot(horizontal, front) * front;
        if (horizontal.sqrMagnitude < 1e-10f) horizontal = isLeft ? shoulderAxis : -shoulderAxis;
        horizontal.Normalize();

        // Step3: use Forward + Horizontal as the only semantic source.
        // Up is derived once from their cross and will drive LookRotation(front, up).
        Vector3 up = Vector3.Cross(horizontal, front);
        if (up.sqrMagnitude < 1e-10f) up = defaultBasis * Vector3.up;
        if (up.sqrMagnitude < 1e-10f) up = Vector3.up;
        up.Normalize();

        if (isLeft)
        {
            debugLeftFront = front;
            debugLeftUp = up;
            debugLeftHorizontal = horizontal;
            debugLeftFoot = foot;
            debugDynamicLeftFront = front;
            debugDynamicLeftHorizontal = horizontal;
            hasDynamicLeftSolveDebug = true;
        }
        else
        {
            debugRightFront = front;
            debugRightUp = up;
            debugRightHorizontal = horizontal;
            debugRightFoot = foot;
            debugDynamicRightFront = front;
            debugDynamicRightHorizontal = horizontal;
            hasDynamicRightSolveDebug = true;
        }

        frontOut = front;
        upOut = up;
        return true;
    }

    private void UpdateSideBases(float dt)
    {
        if (!TryGetDefaultBasis(out Quaternion defaultBasis, out _)) return;
        Quaternion leftIdleBasis = TryGetIdleBasisForSide(true, defaultBasis, out Quaternion lib) ? lib : defaultBasis;
        Quaternion rightIdleBasis = TryGetIdleBasisForSide(false, defaultBasis, out Quaternion rib) ? rib : defaultBasis;
        if (leftBasis == Quaternion.identity) leftBasis = leftIdleBasis;
        if (rightBasis == Quaternion.identity) rightBasis = rightIdleBasis;
        if (leftDynamicBasis == Quaternion.identity) leftDynamicBasis = leftIdleBasis;
        if (rightDynamicBasis == Quaternion.identity) rightDynamicBasis = rightIdleBasis;

        // Always refresh both-side debug with idle frame first;
        // active side will overwrite with dynamic debug later in this frame.
        UpdateIdleDebugForSide(true, defaultBasis);
        UpdateIdleDebugForSide(false, defaultBasis);
        hasDynamicLeftSolveDebug = false;
        hasDynamicRightSolveDebug = false;

        bool leftOuterActive = enableDynamicOuterPlane && source && source.isLeftOuter && IsLagUnrecovered(true);
        bool rightOuterActive = enableDynamicOuterPlane && source && source.isRightOuter && IsLagUnrecovered(false);

        float leftTargetActivation = leftOuterActive ? GetLagEvidence01(true) : 0f;
        float rightTargetActivation = rightOuterActive ? GetLagEvidence01(false) : 0f;

        float leftSmoothTime = leftTargetActivation > leftActivation01 ? activationInTime : activationOutTime;
        float rightSmoothTime = rightTargetActivation > rightActivation01 ? activationInTime : activationOutTime;
        leftActivation01 = Mathf.SmoothDamp(leftActivation01, leftTargetActivation, ref leftActivationVel, leftSmoothTime, Mathf.Infinity, dt);
        rightActivation01 = Mathf.SmoothDamp(rightActivation01, rightTargetActivation, ref rightActivationVel, rightSmoothTime, Mathf.Infinity, dt);
        leftActivation01 = Mathf.Clamp01(leftActivation01);
        rightActivation01 = Mathf.Clamp01(rightActivation01);

        if (leftOuterActive &&
            TryBuildDynamicFrameForSide(true, defaultBasis, leftHasPrevDynamic, prevLeftF, out Vector3 lf, out Vector3 lu))
        {
            Quaternion dynamicTarget = Quaternion.LookRotation(lf, lu);
            leftDynamicBasis = Quaternion.Slerp(leftDynamicBasis, dynamicTarget, ExpAlpha(dynamicTargetSharpness, dt));
            prevLeftF = lf;
            leftHasPrevDynamic = leftActivation01 > 0.001f;
        }
        else
        {
            leftHasPrevDynamic = false;
        }

        if (rightOuterActive &&
            TryBuildDynamicFrameForSide(false, defaultBasis, rightHasPrevDynamic, prevRightF, out Vector3 rf, out Vector3 ru))
        {
            Quaternion dynamicTarget = Quaternion.LookRotation(rf, ru);
            rightDynamicBasis = Quaternion.Slerp(rightDynamicBasis, dynamicTarget, ExpAlpha(dynamicTargetSharpness, dt));
            prevRightF = rf;
            rightHasPrevDynamic = rightActivation01 > 0.001f;
        }
        else
        {
            rightHasPrevDynamic = false;
        }

        Quaternion leftTargetBasis = Quaternion.Slerp(leftIdleBasis, leftDynamicBasis, leftActivation01);
        Quaternion rightTargetBasis = Quaternion.Slerp(rightIdleBasis, rightDynamicBasis, rightActivation01);
        float leftTurnSpeed = Mathf.Lerp(returnTurnSpeedDegPerSec, activeTurnSpeedDegPerSec, leftActivation01);
        float rightTurnSpeed = Mathf.Lerp(returnTurnSpeedDegPerSec, activeTurnSpeedDegPerSec, rightActivation01);
        leftBasis = Quaternion.RotateTowards(leftBasis, leftTargetBasis, Mathf.Max(1f, leftTurnSpeed) * dt);
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

        // Tail points to local +X, which is aligned to solved horizontal extension.
        float x = strokeAmpHorizontal * c;
        float shape = 1f - strokeTeardrop * (1f + c) * 0.5f;
        float z = strokeAmpForward * s * Mathf.Max(0.15f, shape);
        float y = Mathf.Max(0f, outOfPlaneThickness) * strokeAmpUp * s;

        return new Vector3(x, y, z);
    }

    private Vector3 EvalStrokeLocalRested(float tRad)
    {
        Vector3 restOffset = EvalStrokeLocalRaw(RestPhaseRad);
        return EvalStrokeLocalRaw(tRad) - restOffset;
    }

    private Vector3 EvalStrokeLocalRestedForDrawSide(bool isLeft, float tRad)
    {
        Vector3 local = EvalStrokeLocalRested(tRad);
        if (mirrorCurveUpByFlippingLeft && isLeft) local.y *= -1f;
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
        return rp.position + basis * EvalStrokeLocalRested(tRad);
    }

    private void DrawCurve(bool isLeft, Quaternion basis, Color col)
    {
        Transform rp = isLeft ? leftRestPoint : rightRestPoint;
        if (!rp || segments < 8) return;

        Gizmos.color = col;
        float step = (Mathf.PI * 2f) / segments;
        float startT = RestPhaseRad;
        Vector3 prev = rp.position + basis * EvalStrokeLocalRestedForDrawSide(isLeft, startT);
        for (int i = 1; i <= segments; i++)
        {
            float t = startT + i * step;
            Vector3 p = rp.position + basis * EvalStrokeLocalRestedForDrawSide(isLeft, t);
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
            leftBasis = TryGetIdleBasisForSide(true, defaultBasis, out Quaternion lib) ? lib : defaultBasis;
            rightBasis = TryGetIdleBasisForSide(false, defaultBasis, out Quaternion rib) ? rib : defaultBasis;
            UpdateIdleDebugForSide(true, defaultBasis);
            UpdateIdleDebugForSide(false, defaultBasis);
            hasDynamicLeftSolveDebug = false;
            hasDynamicRightSolveDebug = false;
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
        if (!leftOuter && !rightOuter)
        {
            colL = leftColor;
            colR = rightColor;
        }

        DrawCurve(true, leftBasis, colL);
        if (drawBothSides) DrawCurve(false, rightBasis, colR);

        if (drawFrameDebug)
        {
            Vector3 lO = GetRestWorld(true);
            Vector3 rO = GetRestWorld(false);
            float fLen = Mathf.Max(0.01f, frontVizLength);
            float uLen = Mathf.Max(0.01f, upVizLength);
            float hLen = Mathf.Max(0.01f, horizontalVizLength);

            Gizmos.color = frontDebugColor;
            Gizmos.DrawRay(lO, debugLeftFront.normalized * fLen);
            Gizmos.DrawRay(rO, debugRightFront.normalized * fLen);

            Gizmos.color = upDebugColor;
            Gizmos.DrawRay(lO, debugLeftUp.normalized * uLen);
            Gizmos.DrawRay(rO, debugRightUp.normalized * uLen);

            Gizmos.color = horizontalDebugColor;
            Gizmos.DrawRay(lO, debugLeftHorizontal.normalized * hLen);
            Gizmos.DrawRay(rO, debugRightHorizontal.normalized * hLen);

            if (drawPerpendicularFoot)
            {
                Gizmos.DrawSphere(debugLeftFoot, footSphereRadius);
                Gizmos.DrawSphere(debugRightFoot, footSphereRadius);
                Gizmos.DrawLine(lO, debugLeftFoot);
                Gizmos.DrawLine(rO, debugRightFoot);
            }

            if (drawIdleDynamicSolveDebug)
            {
                Gizmos.color = idleFrontDebugColor;
                Gizmos.DrawRay(lO, debugIdleLeftFront.normalized * fLen);
                Gizmos.DrawRay(rO, debugIdleRightFront.normalized * fLen);

                Gizmos.color = idleHorizontalDebugColor;
                Gizmos.DrawRay(lO, debugIdleLeftHorizontal.normalized * hLen);
                Gizmos.DrawRay(rO, debugIdleRightHorizontal.normalized * hLen);

                if (hasDynamicLeftSolveDebug || hasDynamicRightSolveDebug)
                {
                    Gizmos.color = dynamicFrontDebugColor;
                    if (hasDynamicLeftSolveDebug) Gizmos.DrawRay(lO, debugDynamicLeftFront.normalized * fLen);
                    if (hasDynamicRightSolveDebug) Gizmos.DrawRay(rO, debugDynamicRightFront.normalized * fLen);

                    Gizmos.color = dynamicHorizontalDebugColor;
                    if (hasDynamicLeftSolveDebug) Gizmos.DrawRay(lO, debugDynamicLeftHorizontal.normalized * hLen);
                    if (hasDynamicRightSolveDebug) Gizmos.DrawRay(rO, debugDynamicRightHorizontal.normalized * hLen);
                }
            }
        }
    }
}
