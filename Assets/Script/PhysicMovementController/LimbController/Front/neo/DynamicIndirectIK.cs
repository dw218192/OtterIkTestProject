using UnityEngine;

[DefaultExecutionOrder(101)]
public class DynamicIndirectIK_Stabilized : MonoBehaviour
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
    [Range(0.01f, 0.5f)] public float intentSmoothTime = 0.08f;

    [Header("间接IK锚点参数")]
    public float maxAngleL = 60f;
    public float armOffset = 0.5f;
    [Range(0f, 1f)] public float rotationalDrag = 0.92f;
    public float recoverSpeed = 20f;

    // 公开只读：给轨迹系统/可视化系统直接用，彻底干掉反射
    public Vector3 WorldLeftPoint  => worldLeftPoint;
    public Vector3 WorldRightPoint => worldRightPoint;
    public Quaternion StableBasis  => cachedStableRot;
    public Vector3 StableCenter    => cachedStablePos;

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

        // 先构造 forward，再正交化
        Vector3 forward = Vector3.Cross(right, upRef);
        if (forward.sqrMagnitude < 1e-10f) forward = activeRoot.forward;

        forward.Normalize();

        // 用 OrthoNormalize 彻底正交、单位化
        Vector3 up = upRef;
        Vector3.OrthoNormalize(ref forward, ref up); // forward & up 正交
        // 确保 right = up x forward（保持右手系）
        right = Vector3.Cross(up, forward).normalized;

        // 最终 basis：LookRotation(forward, up)
        return Quaternion.LookRotation(forward, up);
    }

    void LateUpdate()
    {
        if (!isInitialized)
        {
            Initialize();
            if (!isInitialized) return;
        }

        // ExecuteAlways 下编辑器可能有奇怪 dt，这里保护一下
        float dt = Time.deltaTime;
        if (dt < 1e-6f) dt = 1f / 60f;

        // --- 1. 更新虚拟参考系（缓存：这一帧只算一次） ---
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
        float lScore = CalculateSwimScore(leftShoulder, currentAngularVel, cachedStableRot, lastStablePos);
        float rScore = CalculateSwimScore(rightShoulder, currentAngularVel, cachedStableRot, lastStablePos);

        filteredScore = Mathf.SmoothDamp(filteredScore, lScore - rScore, ref scoreVelocity, intentSmoothTime);

        if (Mathf.Abs(filteredScore) > sensitivityThreshold)
        {
            isLeftOuter = filteredScore > 0f;
            isRightOuter = filteredScore < 0f;
        }
        else
        {
            // 没有明显意图：回归默认（你的目标）
            isLeftOuter = false;
            isRightOuter = false;
        }

        // --- 3. 锚点物理模拟 ---
        ApplyDrag(ref worldLeftPoint, deltaRot, lastStablePos, posDelta);
        ApplyDrag(ref worldRightPoint, deltaRot, lastStablePos, posDelta);

        UpdateAnchor(leftShoulder, ref worldLeftPoint, isLeftOuter, true, cachedStableRot);
        UpdateAnchor(rightShoulder, ref worldRightPoint, isRightOuter, false, cachedStableRot);

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

            float angle = Vector3.Angle(side, dir.normalized);
            if (angle > maxAngleL) ResetPoint(isLeft);
        }
        else
        {
            point = Vector3.Lerp(point, restPos, Time.deltaTime * recoverSpeed);
        }
    }

    public void ResetPoint(bool isLeft)
    {
        if (!activeRoot || !leftShoulder || !rightShoulder) return;

        // 用“当前缓存 basis”更稳定（避免 Reset 时算出另一套 basis）
        Quaternion basis = cachedStableRot == Quaternion.identity ? CreateStableBasis(cachedStablePos) : cachedStableRot;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);

        if (isLeft) worldLeftPoint = leftShoulder.position + side * armOffset;
        else worldRightPoint = rightShoulder.position + side * armOffset;
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        if (!leftShoulder || !rightShoulder) return;

        Quaternion basis = cachedStableRot == Quaternion.identity
            ? CreateStableBasis((leftShoulder.position + rightShoulder.position) * 0.5f)
            : cachedStableRot;

        Vector3 center = cachedStablePos;

        Gizmos.color = Color.red;   Gizmos.DrawRay(center, basis * Vector3.right * 0.5f);
        Gizmos.color = Color.green; Gizmos.DrawRay(center, basis * Vector3.up * 0.5f);
        Gizmos.color = Color.blue;  Gizmos.DrawRay(center, basis * Vector3.forward * 0.5f);

        DrawSide(leftShoulder, worldLeftPoint, isLeftOuter);
        DrawSide(rightShoulder, worldRightPoint, isRightOuter);
    }

    void DrawSide(Transform shoulder, Vector3 point, bool isOuter)
    {
        Gizmos.color = isOuter ? Color.red : new Color(0.5f, 0.5f, 0.5f, 0.2f);
        Gizmos.DrawSphere(point, 0.05f);
        if (isOuter) Gizmos.DrawLine(shoulder.position, point);
    }
}
