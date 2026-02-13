using UnityEngine;

/// <summary>
/// DynamicIndirectIK_Neo
///
/// This version adds:
/// 1) Rest points for left/right limbs (user-defined) as the stroke curve origin/pivot.
/// 2) Left/right mirror support: right side mirrors the curve in StrokeBasis local X (Right) about chest midline.
/// 3) "Rest point is a vertex": the stroke curve is translated so it passes through the rest point at restPhase.
///    - i.e. localOffset(t) = EvalStrokeLocalRaw(t) - EvalStrokeLocalRaw(restPhase)
///
/// Notes:
/// - The lag anchors (WorldLeftPoint/WorldRightPoint) are still simulated and exposed, but NOT used as the stroke curve origin.
/// - StrokeBasis generation logic is unchanged (conditional instantaneous basis using ω×shoulderLine, else fallback).
/// </summary>
[DefaultExecutionOrder(101)]
public class DynamicIndirectIK_Neo : MonoBehaviour
{
    [Header("输入源 (物理对齐)")]
    public SpineDecouplerWithShadows spineSource;
    public int rootJointIndex = 8;
    public Transform leftShoulder;
    public Transform rightShoulder;

    [Header("判定权重")]
    [Tooltip("右转时，左侧向前冲，左侧变红")]
    public float yawWeight = 1.0f;
    [Tooltip("向下压水的一侧变红")]
    public float rollWeight = 1.5f;
    public float sensitivityThreshold = 0.01f;
    [Tooltip("outer 释放阈值比例（<1 可形成滞回，减少 outer/回归抖动）。实际退出阈值 = sensitivityThreshold * 此值")]
    [Range(0.1f, 1f)] public float outerReleaseRatio = 0.6f;
    [Range(0.01f, 0.5f)] public float intentSmoothTime = 0.08f;

    [Header("间接IK锚点参数")]
    public float maxAngleL = 60f;
    public float armOffset = 0.5f;
    [Range(0f, 1f)] public float rotationalDrag = 0.92f;
    public float recoverSpeed = 20f;
    [Tooltip("outer 期间超过 maxAngleL 时，不再瞬间 Reset，而是以该速度快速拉回到角度上限附近")]
    public float outerOverAngleLerpSpeed = 40f;

    [Header("Stroke Rest Points (用于轨迹起点/旋转中心)")]
    [Tooltip("左侧肢体的 rest point（轨迹起点/旋转中心）。为空则 fallback 到 shoulder+armOffset。")]
    public Transform leftRestPoint;
    [Tooltip("右侧肢体的 rest point（轨迹起点/旋转中心）。为空则 fallback 到 shoulder+armOffset。")]
    public Transform rightRestPoint;

    [Header("StrokeBasis (瞬时平面 / Fallback)")]
    [Tooltip("启用：只有在滞后点存在且不在回归时，才用 角速度×肩线 构造瞬时 StrokeBasis")]
    public bool enableInstantStrokeBasis = true;

    [Tooltip("|ω| 低于该阈值，不信任瞬时平面")]
    public float instantAngVelMin = 0.05f;

    [Tooltip("|ω×R| 低于该阈值，不信任瞬时平面")]
    public float instantCrossMin = 1e-5f;

    [Tooltip("StrokeBasis 平滑强度（越大越跟随，越小越稳定）")]
    public float strokeBasisSharpness = 18f;
    [Tooltip("进入/维持划水时向目标旋转靠拢的平滑强度。")]
    public float strokeActiveSharpness = 18f;
    [Tooltip("非外侧或未划水时，回到 default stroke rotation 的平滑强度。")]
    public float strokeReturnSharpness = 12f;
    [Tooltip("仅当 outer 且 lag 点距离其目标点超过该阈值，才启用 lag-driven 动态 StrokeBasis。")]
    [Min(0.0001f)] public float lagUnrecoveredDistance = 0.03f;
    [Tooltip("lag 未回归判定的释放比例（<1 形成滞回，减少边界抖动）。实际退出阈值=lagUnrecoveredDistance*此值")]
    [Range(0.1f, 1f)] public float lagRecoverRatio = 0.65f;
    [Tooltip("当左右侧都满足激活时，切换主导侧所需的额外距离优势（米）。")]
    [Min(0f)] public float lagSideSwitchDistance = 0.01f;
    [Tooltip("lag→target 向量在去除侧向分量后小于该阈值，则认为不可靠并回退。")]
    [Min(1e-6f)] public float lagForwardMin = 1e-4f;

    public enum DefaultStrokeMode
    {
        UseStableBasis,     // 玩家未指定时，用胸腔稳定系
        UsePlayerFrame      // 使用玩家指定的 Transform 作为默认轨迹参考系
    }

    public DefaultStrokeMode defaultStrokeMode = DefaultStrokeMode.UseStableBasis;

    [Tooltip("默认轨迹参考系（仅当 DefaultStrokeMode=UsePlayerFrame 时生效）")]
    public Transform defaultStrokeFrame;

    [Header("Stroke 曲线：rest 点为顶点")]
    [Tooltip("rest 相位（0..1）。在这个相位，曲线偏移为 0（也就是点恰好在 rest point 上）。")]
    [Range(0f, 1f)] public float restPhase01 = 0f;

    [Header("Gizmo - Stroke Curve 可视化")]
    public bool drawStrokeCurve = false;
    [Range(8, 256)] public int strokeCurveSegments = 64;
    [Tooltip("绘制两侧曲线（左=左restPoint，右=右restPoint）")]
    public bool strokeCurveDrawBothSides = true;

    [Header("Gizmo - 参考点可视化")]
    public bool drawRestPoints = true;
    public float restPointGizmoRadius = 0.04f;

    [Header("Gizmo - Instant f 方向可视化 (f = normalize(ω×shoulderLine))")]
    [Tooltip("在构造瞬时 StrokeBasis 时，把 f = fRaw/crossMag 的方向画出来（仅 Gizmos，不影响逻辑）")]
    public bool drawInstantF = true;
    [Min(0.01f)] public float instantFRayLength = 0.45f;
    public Color instantFColor = new Color(1f, 0f, 1f, 1f); // magenta

    [Header("Gizmo - Instant fRaw 可视化 (lag->target raw)")]
    [Tooltip("可视化 lag-driven 模式下的 raw 向量（target - lagPoint）。关闭 useMagnitude 时只看方向；开启时按向量长度缩放。")]
    public bool drawInstantFRaw = false;
    public bool instantFRawUseMagnitude = true;
    [Min(0.001f)] public float instantFRawScale = 0.08f;
    [Min(0.01f)] public float instantFRawRayLength = 0.45f;
    public Color instantFRawColor = new Color(1f, 0.85f, 0.2f, 1f); // amber

    [Header("Gizmo - Instant fFinal 方向可视化 (fFinal = normalize(r×n))")]
    [Tooltip("可视化 UpdateStrokeBasis() 里最终用于 LookRotation 的 fFinal（正交化后的 forward）")]
    public bool drawInstantFFinal = true;
    [Min(0.01f)] public float instantFFinalRayLength = 0.50f;
    public Color instantFFinalColor = new Color(0.2f, 1f, 0.4f, 1f); // green-ish

    [Header("Gizmo - StableBasis 的 raw forward 可视化 (forward = right×upRef)")]
    [Tooltip("可视化 CreateStableBasis() 中 forward = Cross(right, upRef)（OrthoNormalize 之前）")]
    public bool drawStableRawForward = false;
    [Min(0.01f)] public float stableRawForwardRayLength = 0.45f;
    public Color stableRawForwardColor = new Color(1f, 1f, 0f, 1f); // yellow

    [Header("Gizmo - StableBasis Up 可视化")]
    [Tooltip("单独可视化 stable 空间的上方向（StableBasis * Vector3.up）。")]
    public bool drawStableUpDirection = false;
    [Min(0.01f)] public float stableUpRayLength = 0.6f;
    public Color stableUpColor = new Color(0.1f, 1f, 0.1f, 1f);

    [Header("Gizmo - StrokeBasis.right vs shoulderLine r")]
    [Tooltip("对比：StrokeBasis 的 Right 轴（strokeBasis * Vector3.right）与 r=normalize(RShoulder-LShoulder)。")]
    public bool drawStrokeRightVsR = false;
    [Min(0.01f)] public float strokeRightVsRRayLength = 0.45f;
    public Color strokeBasisRightColor = new Color(0.2f, 0.9f, 1f, 1f); // cyan-ish
    public Color shoulderLineRColor = new Color(1f, 0.2f, 0.2f, 1f);   // red-ish

    [Header("Gizmo - Stroke Curve 形状参数 (StrokeBasis 本地轴)")]
    public float strokeAmpRight = 0.25f;   // X
    public float strokeAmpUp = 0.15f;      // Y
    public float strokeAmpForward = 0.20f; // Z
    [Range(0f, 1f)] public float strokeTeardrop = 0.6f;
    [Tooltip("交换泪痕曲线的 Y/Z（让前后摆动更像 sin 摆动、上下摆动更像 forward 偏移）。不改变 StrokeBasis，仅改变曲线局部坐标。")]
    public bool strokeSwapYZ = false;

    // ---- Public readonly outputs ----
    public Vector3 WorldLeftPoint => worldLeftPoint;
    public Vector3 WorldRightPoint => worldRightPoint;
    public Quaternion StableBasis => cachedStableRot;
    public Vector3 StableCenter => cachedStablePos;

    /// <summary>本帧给 trajectory / IK matcher 用的参考系</summary>
    public Quaternion StrokeBasis => strokeBasis;
    public Quaternion LeftStrokeBasis => leftStrokeBasis;
    public Quaternion RightStrokeBasis => rightStrokeBasis;

    /// <summary>本帧 StrokeBasis 是否来自瞬时平面（ω×肩线）</summary>
    public bool UsingInstantStrokeBasis => usingInstantStrokeBasis;

    /// <summary>世界空间角速度（rad/s）</summary>
    public Vector3 CurrentAngularVel => currentAngularVel;

    /// <summary>左侧rest point（世界坐标，fallback 到 shoulder+armOffset）</summary>
    public Vector3 LeftRestWorld => GetRestWorld(true);

    /// <summary>右侧rest point（世界坐标，fallback 到 shoulder+armOffset）</summary>
    public Vector3 RightRestWorld => GetRestWorld(false);

    private Transform activeRoot;

    private Vector3 worldLeftPoint, worldRightPoint;
    private Quaternion lastStableRot;
    private Vector3 lastStablePos;

    private Quaternion cachedStableRot;
    private Vector3 cachedStablePos;

    public bool isLeftOuter { get; private set; }
    public bool isRightOuter { get; private set; }

    private float filteredScore = 0f;
    private float scoreVelocity = 0f;
    private Vector3 currentAngularVel;
    private bool isInitialized = false;

    // Stroke basis caches
    private Quaternion strokeBasis = Quaternion.identity;
    private Quaternion leftStrokeBasis = Quaternion.identity;
    private Quaternion rightStrokeBasis = Quaternion.identity;
    private bool usingInstantStrokeBasis = false;
    private bool hasPrevStrokeBasis = false;
    private Vector3 prevStrokeF = Vector3.forward;
    private Vector3 prevStrokeN = Vector3.up;

    // Per-side continuity/state: continuity is only enforced during active outer stroke segments.
    private bool leftStrokeActive = false;
    private bool rightStrokeActive = false;
    private bool leftHasPrevStroke = false;
    private bool rightHasPrevStroke = false;
    private Vector3 prevLeftStrokeF = Vector3.forward;
    private Vector3 prevLeftStrokeN = Vector3.up;
    private Vector3 prevRightStrokeF = Vector3.forward;
    private Vector3 prevRightStrokeN = Vector3.up;
    private bool leftLagActive = false;
    private bool rightLagActive = false;

    // Debug/Gizmo
    private Vector3 debugInstantF = Vector3.forward;
    private bool debugInstantFValid = false;
    private Vector3 debugInstantFRaw = Vector3.zero;
    private bool debugInstantFRawValid = false;
    private Vector3 debugInstantFFinal = Vector3.forward;
    private bool debugInstantFFinalValid = false;
    private Vector3 debugStableRawForward = Vector3.forward;
    private bool debugStableRawForwardValid = false;

    void Start() { Initialize(); }

    void Initialize()
    {
        if (spineSource == null || spineSource.shadowNodes == null) return;
        if (rootJointIndex < 0 || rootJointIndex >= spineSource.shadowNodes.Count) return;

        activeRoot = spineSource.shadowNodes[rootJointIndex];
        if (activeRoot == null) return;
        if (!leftShoulder || !rightShoulder) return;

        cachedStablePos = (leftShoulder.position + rightShoulder.position) * 0.5f;
        cachedStableRot = CreateStableBasis(cachedStablePos);

        lastStablePos = cachedStablePos;
        lastStableRot = cachedStableRot;

        ResetPoint(true);
        ResetPoint(false);

        // Init stroke basis to stable/default
        strokeBasis = GetDefaultStrokeBasis(cachedStableRot);
        hasPrevStrokeBasis = true;
        prevStrokeF = strokeBasis * Vector3.forward;
        prevStrokeN = strokeBasis * Vector3.up;
        leftStrokeBasis = strokeBasis;
        rightStrokeBasis = strokeBasis;
        leftHasPrevStroke = false;
        rightHasPrevStroke = false;
        leftStrokeActive = false;
        rightStrokeActive = false;

        isInitialized = true;
    }

    /// <summary>
    /// 稳定参考系：
    /// - X: 肩膀连线（Right）
    /// - Y: 尽量贴近 activeRoot.up，但强制正交化，避免 flip
    /// - Z: Forward
    /// </summary>
    Quaternion CreateStableBasis(Vector3 center)
    {
        Vector3 right = (rightShoulder.position - leftShoulder.position);
        if (right.sqrMagnitude < 1e-10f) right = activeRoot.right;
        right.Normalize();

        Vector3 upRef = activeRoot.up;
        if (upRef.sqrMagnitude < 1e-10f) upRef = Vector3.up;

        // raw forward, then orthonormalize
        Vector3 forward = Vector3.Cross(right, upRef);
        debugStableRawForward = forward;
        debugStableRawForwardValid = forward.sqrMagnitude > 1e-10f;

        if (forward.sqrMagnitude < 1e-10f) forward = activeRoot.forward;
        forward.Normalize();

        Vector3 up = upRef;
        Vector3.OrthoNormalize(ref forward, ref up);

        // ensure right-hand
        right = Vector3.Cross(up, forward).normalized;

        return Quaternion.LookRotation(forward, up);
    }

    void LateUpdate()
    {
        if (!isInitialized)
        {
            Initialize();
            if (!isInitialized) return;
        }

        float dt = Time.deltaTime;
        if (dt < 1e-6f) dt = 1f / 60f;

        // --- 1. 更新稳定参考系（缓存：这一帧只算一次） ---
        cachedStablePos = (leftShoulder.position + rightShoulder.position) * 0.5f;
        cachedStableRot = CreateStableBasis(cachedStablePos);

        Quaternion deltaRot = cachedStableRot * Quaternion.Inverse(lastStableRot);
        Vector3 posDelta = cachedStablePos - lastStablePos;

        // --- 角速度 ---
        deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
        if (axis.sqrMagnitude < 1e-10f) axis = Vector3.up;
        if (angle > 180f) angle -= 360f;
        currentAngularVel = axis.normalized * (angle * Mathf.Deg2Rad / dt);

        // --- 2. 判定逻辑 ---
        float lScore = CalculateSwimScore(leftShoulder, currentAngularVel, cachedStableRot, cachedStablePos);
        float rScore = CalculateSwimScore(rightShoulder, currentAngularVel, cachedStableRot, cachedStablePos);

        filteredScore = Mathf.SmoothDamp(filteredScore, lScore - rScore, ref scoreVelocity, intentSmoothTime);

        float absScore = Mathf.Abs(filteredScore);
        float enterTh = sensitivityThreshold;
        float exitTh = sensitivityThreshold * Mathf.Clamp01(outerReleaseRatio);

        bool currentlyOuter = isLeftOuter || isRightOuter;
        if (!currentlyOuter)
        {
            if (absScore > enterTh)
            {
                isLeftOuter = filteredScore > 0f;
                isRightOuter = filteredScore < 0f;
            }
            else
            {
                isLeftOuter = false;
                isRightOuter = false;
            }
        }
        else
        {
            if (absScore < exitTh)
            {
                isLeftOuter = false;
                isRightOuter = false;
            }
            else
            {
                isLeftOuter = filteredScore > 0f;
                isRightOuter = filteredScore < 0f;
            }
        }

        // --- 3. 锚点物理模拟（保留：供其他系统使用，不再作为曲线中心） ---
        ApplyDrag(ref worldLeftPoint, deltaRot, lastStablePos, posDelta);
        ApplyDrag(ref worldRightPoint, deltaRot, lastStablePos, posDelta);

        UpdateAnchor(leftShoulder, ref worldLeftPoint, isLeftOuter, true, cachedStableRot);
        UpdateAnchor(rightShoulder, ref worldRightPoint, isRightOuter, false, cachedStableRot);

        // --- 4. StrokeBasis（有条件 ω×肩线，否则 fallback） ---
        UpdateStrokeBasis(dt);

        lastStableRot = cachedStableRot;
        lastStablePos = cachedStablePos;
    }

    float CalculateSwimScore(Transform shoulder, Vector3 angVel, Quaternion basis, Vector3 pivot)
    {
        Vector3 radiusVec = shoulder.position - pivot;
        Vector3 v = Vector3.Cross(angVel, radiusVec);

        float yawPart = Vector3.Dot(v, basis * Vector3.forward) * yawWeight;
        float rollPart = Vector3.Dot(v, -(basis * Vector3.up)) * rollWeight;

        return yawPart + rollPart;
    }

    void ApplyDrag(ref Vector3 point, Quaternion deltaRot, Vector3 pivot, Vector3 moveDelta)
    {
        Vector3 rel = point - pivot;
        Quaternion follow = Quaternion.Slerp(Quaternion.identity, deltaRot, 1f - rotationalDrag);
        point = pivot + (follow * rel) + moveDelta;
    }

    void UpdateAnchor(Transform shoulder, ref Vector3 point, bool isOuter, bool isLeft, Quaternion basis)
    {
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        Vector3 restPos = shoulder.position + side * armOffset;

        if (isOuter)
        {
            Vector3 dir = (point - shoulder.position);
            if (dir.sqrMagnitude < 1e-10f) { ResetPoint(isLeft); return; }

            Vector3 dirN = dir.normalized;
            float angle = Vector3.Angle(side, dirN);
            if (angle > maxAngleL)
            {
                // Soft clamp: keep outer behavior but quickly pull back inside angle limit.
                Vector3 clampedDir = Vector3.RotateTowards(
                    side,
                    dirN,
                    maxAngleL * Mathf.Deg2Rad,
                    0f
                );
                Vector3 clampedPos = shoulder.position + clampedDir * armOffset;
                point = Vector3.MoveTowards(point, clampedPos, Mathf.Max(0f, outerOverAngleLerpSpeed) * Time.deltaTime);
            }
        }
        else
        {
            point = Vector3.MoveTowards(point, restPos, Mathf.Max(0f, recoverSpeed) * Time.deltaTime);
        }
    }

    public void ResetPoint(bool isLeft)
    {
        if (!activeRoot || !leftShoulder || !rightShoulder) return;

        Quaternion basis = cachedStableRot == Quaternion.identity ? CreateStableBasis(cachedStablePos) : cachedStableRot;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);

        if (isLeft) worldLeftPoint = leftShoulder.position + side * armOffset;
        else worldRightPoint = rightShoulder.position + side * armOffset;
    }

    Vector3 GetRestWorld(bool isLeft)
    {
        if (isLeft && leftRestPoint) return leftRestPoint.position;
        if (!isLeft && rightRestPoint) return rightRestPoint.position;

        // Fallback: shoulder + side * armOffset (same as anchor rest pos)
        Quaternion basis = cachedStableRot == Quaternion.identity ? CreateStableBasis(cachedStablePos) : cachedStableRot;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        Transform sh = isLeft ? leftShoulder : rightShoulder;
        return sh ? (sh.position + side * armOffset) : cachedStablePos;
    }

    // -------------------- StrokeBasis logic --------------------

    bool CanUseInstantStrokeBasis()
    {
        // Your rule: only when lag exists AND not in recovery.
        // In this implementation: lag exists if any side is currently outer (i.e. the lag point is "active").
        // When neither side is outer, anchors are recovering => fallback.
        return (isLeftOuter || isRightOuter);
    }

    Quaternion GetDefaultStrokeBasis(Quaternion stable)
    {
        if (defaultStrokeMode == DefaultStrokeMode.UsePlayerFrame && defaultStrokeFrame)
        {
            Vector3 f = defaultStrokeFrame.forward;
            Vector3 u = defaultStrokeFrame.up;
            if (f.sqrMagnitude < 1e-8f) f = Vector3.forward;
            if (u.sqrMagnitude < 1e-8f) u = Vector3.up;
            f.Normalize();
            u.Normalize();

            Vector3 r = Vector3.Cross(u, f);
            if (r.sqrMagnitude < 1e-8f)
            {
                u = Vector3.up;
                r = Vector3.Cross(u, f);
            }
            r.Normalize();
            u = Vector3.Cross(f, r).normalized;
            return Quaternion.LookRotation(f, u);
        }

        return stable;
    }

    static float ExpAlpha(float sharpness, float dt)
    {
        return 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, dt));
    }

    bool IsLagUnrecovered(bool isLeft)
    {
        Vector3 lag = isLeft ? worldLeftPoint : worldRightPoint;
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

    void ApplyFallbackStrokeBasis(float dt, Quaternion defaultBasis)
    {
        // When no active lag-driven stroke exists, smoothly return to default frame.
        if (!hasPrevStrokeBasis)
        {
            strokeBasis = defaultBasis;
            hasPrevStrokeBasis = true;
            prevStrokeF = strokeBasis * Vector3.forward;
            prevStrokeN = strokeBasis * Vector3.up;
            return;
        }

        float a = ExpAlpha(strokeBasisSharpness, dt);
        strokeBasis = Quaternion.Slerp(strokeBasis, defaultBasis, a);
        prevStrokeF = strokeBasis * Vector3.forward;
        prevStrokeN = strokeBasis * Vector3.up;
    }

    bool TryBuildLagDrivenBasisForSide(
        bool isLeft,
        float dt,
        bool hasPrev,
        Vector3 prevF,
        Vector3 prevN,
        out Vector3 fFinalOut,
        out Vector3 nOut)
    {
        fFinalOut = Vector3.zero;
        nOut = Vector3.zero;

        Vector3 r = (rightShoulder.position - leftShoulder.position);
        if (r.sqrMagnitude < 1e-10f) r = cachedStableRot * Vector3.right;
        r.Normalize();

        Vector3 lagPoint = isLeft ? worldLeftPoint : worldRightPoint;
        Vector3 targetPoint = GetRestWorld(isLeft);

        // Raw lag-driven vector used to build the stroke plane in this mode.
        Vector3 fRawLag = targetPoint - lagPoint;

        // Build forward from lag->target, then remove shoulder-line component.
        // Projection uses canonical shoulder axis r to keep left/right tilt behavior symmetric.
        Vector3 fCandidate = fRawLag - Vector3.Dot(fRawLag, r) * r;
        if (fCandidate.sqrMagnitude < lagForwardMin * lagForwardMin) return false;
        Vector3 f = fCandidate.normalized;

        // Hemisphere continuity
        if (hasPrev && Vector3.Dot(f, prevF) < 0f) f = -f;

        float a = ExpAlpha(strokeBasisSharpness, dt);
        Vector3 fSmoothed = hasPrev ? Vector3.Slerp(prevF, f, a) : f;
        if (fSmoothed.sqrMagnitude < 1e-8f) fSmoothed = f;
        fSmoothed.Normalize();

        Vector3 n = Vector3.Cross(r, fSmoothed);
        if (n.sqrMagnitude < 1e-8f) n = cachedStableRot * Vector3.up;
        n.Normalize();

        // Use r x n so LookRotation(forward=nx?, up=n) yields right aligned with shoulder line r.
        Vector3 fFinal = Vector3.Cross(r, n);
        if (fFinal.sqrMagnitude < 1e-8f) fFinal = fSmoothed;
        fFinal.Normalize();

        // Choose hemisphere by continuity + stable up preference (single decision avoids contradictory flips).
        Vector3 stableUp = cachedStableRot * Vector3.up;
        if (hasPrev)
        {
            const float wF = 1.0f;
            const float wN = 0.35f;
            const float wUp = 0.35f;
            float scoreKeep =
                wF * Vector3.Dot(fFinal, prevF) +
                wN * Vector3.Dot(n, prevN) +
                wUp * Vector3.Dot(n, stableUp);
            float scoreFlip =
                wF * Vector3.Dot(-fFinal, prevF) +
                wN * Vector3.Dot(-n, prevN) +
                wUp * Vector3.Dot(-n, stableUp);
            if (scoreFlip > scoreKeep)
            {
                fFinal = -fFinal;
                n = -n;
            }
        }
        else if (Vector3.Dot(n, stableUp) < 0f)
        {
            // First active frame: prefer chest-up hemisphere.
            fFinal = -fFinal;
            n = -n;
        }

        fFinalOut = fFinal;
        nOut = n;
        return true;
    }

    void UpdateStrokeBasis(float dt)
    {
        usingInstantStrokeBasis = false;
        debugInstantFValid = false;
        debugInstantFRawValid = false;
        debugInstantFFinalValid = false;

        Quaternion defaultBasis = GetDefaultStrokeBasis(cachedStableRot);
        if (leftStrokeBasis == Quaternion.identity) leftStrokeBasis = defaultBasis;
        if (rightStrokeBasis == Quaternion.identity) rightStrokeBasis = defaultBasis;

        bool leftOuterActive = enableInstantStrokeBasis && isLeftOuter && IsLagUnrecovered(true);
        bool rightOuterActive = enableInstantStrokeBasis && isRightOuter && IsLagUnrecovered(false);

        // Left side update
        if (leftOuterActive &&
            TryBuildLagDrivenBasisForSide(true, dt, leftHasPrevStroke, prevLeftStrokeF, prevLeftStrokeN, out Vector3 leftF, out Vector3 leftN))
        {
            Quaternion leftTarget = Quaternion.LookRotation(leftF, leftN);
            float aActive = ExpAlpha(strokeActiveSharpness, dt);
            leftStrokeBasis = Quaternion.Slerp(leftStrokeBasis, leftTarget, aActive);

            prevLeftStrokeF = leftF;
            prevLeftStrokeN = leftN;
            leftHasPrevStroke = true;
            leftStrokeActive = true;
            usingInstantStrokeBasis = true;
        }
        else
        {
            float aReturn = ExpAlpha(strokeReturnSharpness, dt);
            leftStrokeBasis = Quaternion.Slerp(leftStrokeBasis, defaultBasis, aReturn);
            leftStrokeActive = false;
            leftHasPrevStroke = false;
        }

        // Right side update
        if (rightOuterActive &&
            TryBuildLagDrivenBasisForSide(false, dt, rightHasPrevStroke, prevRightStrokeF, prevRightStrokeN, out Vector3 rightF, out Vector3 rightN))
        {
            Quaternion rightTarget = Quaternion.LookRotation(rightF, rightN);
            float aActive = ExpAlpha(strokeActiveSharpness, dt);
            rightStrokeBasis = Quaternion.Slerp(rightStrokeBasis, rightTarget, aActive);

            prevRightStrokeF = rightF;
            prevRightStrokeN = rightN;
            rightHasPrevStroke = true;
            rightStrokeActive = true;
            usingInstantStrokeBasis = true;
        }
        else
        {
            float aReturn = ExpAlpha(strokeReturnSharpness, dt);
            rightStrokeBasis = Quaternion.Slerp(rightStrokeBasis, defaultBasis, aReturn);
            rightStrokeActive = false;
            rightHasPrevStroke = false;
        }

        // Backward-compat: expose a single StrokeBasis for existing consumers.
        if (leftStrokeActive) strokeBasis = leftStrokeBasis;
        else if (rightStrokeActive) strokeBasis = rightStrokeBasis;
        else strokeBasis = Quaternion.Slerp(leftStrokeBasis, rightStrokeBasis, 0.5f);

        prevStrokeF = strokeBasis * Vector3.forward;
        prevStrokeN = strokeBasis * Vector3.up;
        hasPrevStrokeBasis = true;

        // Debug vectors follow the currently active side for easy inspection.
        if (leftStrokeActive || rightStrokeActive)
        {
            bool dbgLeft = leftStrokeActive;
            Vector3 lagPoint = dbgLeft ? worldLeftPoint : worldRightPoint;
            Vector3 targetPoint = GetRestWorld(dbgLeft);
            Vector3 raw = targetPoint - lagPoint;
            debugInstantFRaw = raw;
            debugInstantFRawValid = raw.sqrMagnitude > 1e-12f;

            Vector3 fDir = (dbgLeft ? leftStrokeBasis : rightStrokeBasis) * Vector3.forward;
            if (fDir.sqrMagnitude > 1e-10f)
            {
                debugInstantF = fDir.normalized;
                debugInstantFValid = true;
                debugInstantFFinal = debugInstantF;
                debugInstantFFinalValid = true;
            }
        }
    }

    // -------------------- Stroke Curve (rest vertex + mirror) --------------------

    float RestPhaseRad => Mathf.Repeat(restPhase01, 1f) * Mathf.PI * 2f;

    Vector3 EvalStrokeLocalRaw(float tRad)
    {
        float c = Mathf.Cos(tRad);
        float s = Mathf.Sin(tRad);

        float x = strokeAmpRight * c;

        // Teardrop shaping
        float shape = 1f - strokeTeardrop * (c + 1f) * 0.5f; // [1-strokeTeardrop .. 1]
        float y = strokeAmpUp * s * Mathf.Max(0.15f, shape);

        // Forward reach
        float z = strokeAmpForward * (0.65f * Mathf.Max(0f, c) - 0.35f * Mathf.Max(0f, -c));

        return strokeSwapYZ ? new Vector3(x, z, y) : new Vector3(x, y, z);
    }

    Vector3 EvalStrokeLocalRested(float tRad)
    {
        // Translate curve so that at restPhase the local offset is exactly zero.
        Vector3 restOffset = EvalStrokeLocalRaw(RestPhaseRad);
        return EvalStrokeLocalRaw(tRad) - restOffset;
    }

    bool ShouldMirrorLocalX(Vector3 origin, Quaternion basis)
    {
        // Keep extension consistent with CURRENT world side (not static bone label):
        // world-left side should extend toward -r, world-right toward +r.
        Vector3 r = (rightShoulder && leftShoulder)
            ? (rightShoulder.position - leftShoulder.position)
            : (cachedStableRot * Vector3.right);
        if (r.sqrMagnitude < 1e-10f) r = Vector3.right;
        r.Normalize();

        Vector3 basisRight = basis * Vector3.right;
        if (basisRight.sqrMagnitude < 1e-10f) basisRight = r;
        basisRight.Normalize();

        bool basisRightReversed = Vector3.Dot(basisRight, r) < 0f;
        Vector3 center = cachedStablePos;
        bool worldLeftSide = Vector3.Dot(origin - center, r) < 0f;

        // For our current local curve (x starts negative), this chooses whether to flip x
        // so the major extension points to the expected world side.
        return worldLeftSide ? basisRightReversed : !basisRightReversed;
    }

    Vector3 EvalStrokeLocalRestedMirrored(Vector3 origin, Quaternion basis, float tRad)
    {
        Vector3 local = EvalStrokeLocalRested(tRad);
        if (ShouldMirrorLocalX(origin, basis)) local.x *= -1f;
        return local;
    }

    /// <summary>
    /// Evaluate a world-space stroke point for visualization / other systems.
    /// t01 in [0..1] maps to 0..2π.
    /// </summary>
    public Vector3 EvaluateStrokeWorld(bool isLeft, float t01)
    {
        float tRad = Mathf.Repeat(t01, 1f) * Mathf.PI * 2f;
        Vector3 origin = isLeft ? GetRestWorld(true) : GetRestWorld(false);
        Quaternion basis = isLeft ? leftStrokeBasis : rightStrokeBasis;
        return origin + basis * EvalStrokeLocalRestedMirrored(origin, basis, tRad);
    }

    void DrawStrokeCurveSide(bool isLeft, Vector3 origin, Quaternion basis, Color col)
    {
        if (!drawStrokeCurve || strokeCurveSegments < 8) return;

        Gizmos.color = col;

        float step = (Mathf.PI * 2f) / strokeCurveSegments;
        float t0 = 0f;

        Vector3 prev = origin + basis * EvalStrokeLocalRestedMirrored(origin, basis, t0);
        for (int i = 1; i <= strokeCurveSegments; i++)
        {
            float t = i * step;
            Vector3 p = origin + basis * EvalStrokeLocalRestedMirrored(origin, basis, t);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    // -------------------- Gizmos --------------------

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        if (!leftShoulder || !rightShoulder) return;

        Quaternion basisStable = cachedStableRot == Quaternion.identity
            ? CreateStableBasis((leftShoulder.position + rightShoulder.position) * 0.5f)
            : cachedStableRot;

        Vector3 center = cachedStablePos;

        // Stable basis axes
        Gizmos.color = Color.red;   Gizmos.DrawRay(center, basisStable * Vector3.right * 0.5f);
        Gizmos.color = Color.green; Gizmos.DrawRay(center, basisStable * Vector3.up * 0.5f);
        Gizmos.color = Color.blue;  Gizmos.DrawRay(center, basisStable * Vector3.forward * 0.5f);

        // Lag anchors (kept for debugging)
        Gizmos.color = isLeftOuter ? Color.red : new Color(0.5f, 0.5f, 0.5f, 0.2f);
        Gizmos.DrawSphere(worldLeftPoint, 0.05f);
        if (isLeftOuter) Gizmos.DrawLine(leftShoulder.position, worldLeftPoint);

        Gizmos.color = isRightOuter ? Color.red : new Color(0.5f, 0.5f, 0.5f, 0.2f);
        Gizmos.DrawSphere(worldRightPoint, 0.05f);
        if (isRightOuter) Gizmos.DrawLine(rightShoulder.position, worldRightPoint);

        // Rest points
        if (drawRestPoints)
        {
            Vector3 lRest = GetRestWorld(true);
            Vector3 rRest = GetRestWorld(false);

            Gizmos.color = new Color(1f, 1f, 1f, 0.85f);
            Gizmos.DrawSphere(lRest, restPointGizmoRadius);
            Gizmos.DrawSphere(rRest, restPointGizmoRadius);
        }

        // Stroke basis axis (forward)
        if (strokeBasis != Quaternion.identity)
        {
            Gizmos.color = usingInstantStrokeBasis ? new Color(1f, 0.55f, 0.15f, 1f) : new Color(0.2f, 0.85f, 1f, 1f);
            Gizmos.DrawRay(center, strokeBasis * Vector3.forward * 0.35f);
        }

        // StrokeBasis.right vs shoulderLine r
        if (drawStrokeRightVsR && strokeBasis != Quaternion.identity)
        {
            Vector3 strokeRightW = strokeBasis * Vector3.right;
            if (strokeRightW.sqrMagnitude < 1e-10f) strokeRightW = basisStable * Vector3.right;
            strokeRightW.Normalize();

            Vector3 r = (rightShoulder.position - leftShoulder.position);
            if (r.sqrMagnitude < 1e-10f) r = basisStable * Vector3.right;
            r.Normalize();

            Gizmos.color = strokeBasisRightColor;
            Gizmos.DrawRay(center, strokeRightW * strokeRightVsRRayLength);
            Gizmos.color = shoulderLineRColor;
            Gizmos.DrawRay(center, r * strokeRightVsRRayLength);
        }

        // Stable basis raw forward
        if (drawStableRawForward && debugStableRawForwardValid)
        {
            Gizmos.color = stableRawForwardColor;
            Gizmos.DrawRay(center, debugStableRawForward.normalized * stableRawForwardRayLength);
        }

        // Stable basis up direction
        if (drawStableUpDirection)
        {
            Gizmos.color = stableUpColor;
            Gizmos.DrawRay(center, basisStable * Vector3.up * stableUpRayLength);
        }

        // Instant f
        if (drawInstantF && debugInstantFValid)
        {
            Gizmos.color = instantFColor;
            Gizmos.DrawRay(center, debugInstantF.normalized * instantFRayLength);
        }

        // Instant fRaw (lag-driven raw: target - lagPoint)
        if (drawInstantFRaw && debugInstantFRawValid)
        {
            Vector3 rawDir = debugInstantFRaw.normalized;
            float rawLen = instantFRawUseMagnitude
                ? Mathf.Max(0.01f, debugInstantFRaw.magnitude * instantFRawScale)
                : instantFRawRayLength;
            Gizmos.color = instantFRawColor;
            Gizmos.DrawRay(center, rawDir * rawLen);
        }

        // Instant fFinal
        if (drawInstantFFinal && debugInstantFFinalValid)
        {
            Gizmos.color = instantFFinalColor;
            Gizmos.DrawRay(center, debugInstantFFinal.normalized * instantFFinalRayLength);
        }

        // Stroke curve (origin = rest points)
        if (drawStrokeCurve && strokeBasis != Quaternion.identity)
        {
            // Per-side coloring for debugging:
            // - Outer (actively stroking side): warm/highlight colors
            // - Inner/default side: cool colors
            Color colL = isLeftOuter
                ? new Color(1f, 0.55f, 0.15f, 1f)
                : new Color(0.2f, 0.85f, 1f, 1f);
            Color colR = isRightOuter
                ? new Color(1f, 0.35f, 0.15f, 1f)
                : new Color(0.15f, 0.7f, 1f, 1f);

            Vector3 lOrigin = GetRestWorld(true);
            DrawStrokeCurveSide(true, lOrigin, leftStrokeBasis, colL);

            if (strokeCurveDrawBothSides)
            {
                Vector3 rOrigin = GetRestWorld(false);
                DrawStrokeCurveSide(false, rOrigin, rightStrokeBasis, colR);
            }
        }
    }
}
