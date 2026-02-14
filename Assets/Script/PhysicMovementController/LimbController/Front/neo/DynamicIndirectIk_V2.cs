using UnityEngine;

/// <summary>
/// DynamicIndirectIk_V2
///
/// Purpose (minimal version):
/// 1) Generate and maintain lag points (left/right) with drag + recovery.
/// 2) Judge which shoulder is currently "outer" during motion.
///
/// No stroke-basis / trajectory logic in this version.
/// </summary>
[DefaultExecutionOrder(101)]
public class DynamicIndirectIk_V2 : MonoBehaviour
{
    [Header("输入源 (物理对齐)")]
    public SpineDecouplerWithShadows spineSource;
    public int rootJointIndex = 8;
    public Transform leftShoulder;
    public Transform rightShoulder;

    [Header("外侧判定权重")]
    [Tooltip("右转时，左侧向前冲，左侧变红")]
    public float yawWeight = 1.0f;
    [Tooltip("向下压水的一侧变红")]
    public float rollWeight = 1.5f;
    public float sensitivityThreshold = 0.01f;
    [Tooltip("outer 释放阈值比例（<1 可形成滞回，减少 outer/回归抖动）。实际退出阈值 = sensitivityThreshold * 此值")]
    [Range(0.1f, 1f)] public float outerReleaseRatio = 0.6f;
    [Range(0.01f, 0.5f)] public float intentSmoothTime = 0.08f;

    [Header("滞后点参数")]
    public float maxAngleL = 60f;
    public float armOffset = 0.5f;
    [Range(0f, 1f)] public float rotationalDrag = 0.92f;
    public float recoverSpeed = 20f;
    [Tooltip("outer 期间超过 maxAngleL 时，以该速度快速拉回到角度上限附近")]
    public float outerOverAngleLerpSpeed = 40f;

    [Header("Gizmo")]
    public bool drawGizmos = true;
    [Header("Gizmo - 内外侧 Shoulder 可视化")]
    public bool drawShoulderRole = true;
    public float shoulderRoleSphereRadius = 0.06f;
    public Color outerShoulderColor = new Color(1f, 0.35f, 0.15f, 1f);
    public Color innerShoulderColor = new Color(0.2f, 0.85f, 1f, 1f);
    public Color neutralShoulderColor = new Color(0.75f, 0.75f, 0.75f, 0.65f);

    // public readonly outputs
    public Vector3 WorldLeftPoint => worldLeftPoint;
    public Vector3 WorldRightPoint => worldRightPoint;
    public Quaternion StableBasis => cachedStableRot;
    public Vector3 StableCenter => cachedStablePos;
    public Vector3 CurrentAngularVel => currentAngularVel;

    public bool isLeftOuter { get; private set; }
    public bool isRightOuter { get; private set; }
    public Transform CurrentOuterShoulder =>
        isLeftOuter ? leftShoulder : (isRightOuter ? rightShoulder : null);
    public Transform CurrentInnerShoulder =>
        isLeftOuter ? rightShoulder : (isRightOuter ? leftShoulder : null);

    private Transform activeRoot;
    private bool isInitialized;

    private Vector3 worldLeftPoint;
    private Vector3 worldRightPoint;

    private Quaternion cachedStableRot = Quaternion.identity;
    private Vector3 cachedStablePos;
    private Quaternion lastStableRot = Quaternion.identity;
    private Vector3 lastStablePos;

    private Vector3 currentAngularVel;
    private float filteredScore;
    private float scoreVelocity;

    private void Start()
    {
        Initialize();
    }

    private void Initialize()
    {
        if (spineSource == null || spineSource.shadowNodes == null) return;
        if (rootJointIndex < 0 || rootJointIndex >= spineSource.shadowNodes.Count) return;
        if (!leftShoulder || !rightShoulder) return;

        activeRoot = spineSource.shadowNodes[rootJointIndex];
        if (!activeRoot) return;

        cachedStablePos = 0.5f * (leftShoulder.position + rightShoulder.position);
        cachedStableRot = CreateStableBasis();
        lastStablePos = cachedStablePos;
        lastStableRot = cachedStableRot;

        ResetPoint(true);
        ResetPoint(false);
        isInitialized = true;
    }

    private Quaternion CreateStableBasis()
    {
        Vector3 right = rightShoulder.position - leftShoulder.position;
        if (right.sqrMagnitude < 1e-10f) right = activeRoot.right;
        right.Normalize();

        Vector3 upRef = activeRoot.up;
        if (upRef.sqrMagnitude < 1e-10f) upRef = Vector3.up;

        Vector3 forward = Vector3.Cross(right, upRef);
        if (forward.sqrMagnitude < 1e-10f) forward = activeRoot.forward;
        forward.Normalize();

        Vector3 up = upRef;
        Vector3.OrthoNormalize(ref forward, ref up);
        return Quaternion.LookRotation(forward, up);
    }

    private void LateUpdate()
    {
        if (!isInitialized)
        {
            Initialize();
            if (!isInitialized) return;
        }

        float dt = Time.deltaTime;
        if (dt < 1e-6f) dt = 1f / 60f;

        // 1) update stable frame
        cachedStablePos = 0.5f * (leftShoulder.position + rightShoulder.position);
        cachedStableRot = CreateStableBasis();
        Quaternion deltaRot = cachedStableRot * Quaternion.Inverse(lastStableRot);
        Vector3 posDelta = cachedStablePos - lastStablePos;

        // 2) angular velocity
        deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
        if (axis.sqrMagnitude < 1e-10f) axis = Vector3.up;
        if (angle > 180f) angle -= 360f;
        currentAngularVel = axis.normalized * (angle * Mathf.Deg2Rad / dt);

        // 3) outer-side judgement
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

        // 4) lag points
        ApplyDrag(ref worldLeftPoint, deltaRot, lastStablePos, posDelta);
        ApplyDrag(ref worldRightPoint, deltaRot, lastStablePos, posDelta);
        UpdateAnchor(leftShoulder, ref worldLeftPoint, isLeftOuter, true, cachedStableRot);
        UpdateAnchor(rightShoulder, ref worldRightPoint, isRightOuter, false, cachedStableRot);

        lastStableRot = cachedStableRot;
        lastStablePos = cachedStablePos;
    }

    private float CalculateSwimScore(Transform shoulder, Vector3 angVel, Quaternion basis, Vector3 pivot)
    {
        Vector3 radiusVec = shoulder.position - pivot;
        Vector3 v = Vector3.Cross(angVel, radiusVec);
        float yawPart = Vector3.Dot(v, basis * Vector3.forward) * yawWeight;
        float rollPart = Vector3.Dot(v, -(basis * Vector3.up)) * rollWeight;
        return yawPart + rollPart;
    }

    private void ApplyDrag(ref Vector3 point, Quaternion deltaRot, Vector3 pivot, Vector3 moveDelta)
    {
        Vector3 rel = point - pivot;
        Quaternion follow = Quaternion.Slerp(Quaternion.identity, deltaRot, 1f - rotationalDrag);
        point = pivot + (follow * rel) + moveDelta;
    }

    private void UpdateAnchor(Transform shoulder, ref Vector3 point, bool isOuter, bool isLeft, Quaternion basis)
    {
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        Vector3 restPos = shoulder.position + side * armOffset;

        if (isOuter)
        {
            Vector3 dir = point - shoulder.position;
            if (dir.sqrMagnitude < 1e-10f)
            {
                ResetPoint(isLeft);
                return;
            }

            Vector3 dirN = dir.normalized;
            float angle = Vector3.Angle(side, dirN);
            if (angle > maxAngleL)
            {
                Vector3 clampedDir = Vector3.RotateTowards(side, dirN, maxAngleL * Mathf.Deg2Rad, 0f);
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
        Quaternion basis = cachedStableRot == Quaternion.identity ? CreateStableBasis() : cachedStableRot;
        Vector3 side = isLeft ? -(basis * Vector3.right) : (basis * Vector3.right);
        if (isLeft) worldLeftPoint = leftShoulder.position + side * armOffset;
        else worldRightPoint = rightShoulder.position + side * armOffset;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos || !Application.isPlaying) return;
        if (!leftShoulder || !rightShoulder) return;

        Vector3 center = cachedStablePos;

        // stable axes
        Gizmos.color = Color.red;
        Gizmos.DrawRay(center, cachedStableRot * Vector3.right * 0.5f);
        Gizmos.color = Color.green;
        Gizmos.DrawRay(center, cachedStableRot * Vector3.up * 0.5f);
        Gizmos.color = Color.blue;
        Gizmos.DrawRay(center, cachedStableRot * Vector3.forward * 0.5f);

        // lag points + links
        Gizmos.color = isLeftOuter ? Color.red : new Color(0.6f, 0.6f, 0.6f, 0.3f);
        Gizmos.DrawSphere(worldLeftPoint, 0.05f);
        if (isLeftOuter) Gizmos.DrawLine(leftShoulder.position, worldLeftPoint);

        Gizmos.color = isRightOuter ? Color.red : new Color(0.6f, 0.6f, 0.6f, 0.3f);
        Gizmos.DrawSphere(worldRightPoint, 0.05f);
        if (isRightOuter) Gizmos.DrawLine(rightShoulder.position, worldRightPoint);

        // shoulder role visualization (outer/inner)
        if (drawShoulderRole)
        {
            Color leftCol = neutralShoulderColor;
            Color rightCol = neutralShoulderColor;

            if (isLeftOuter)
            {
                leftCol = outerShoulderColor;
                rightCol = innerShoulderColor;
            }
            else if (isRightOuter)
            {
                leftCol = innerShoulderColor;
                rightCol = outerShoulderColor;
            }

            Gizmos.color = leftCol;
            Gizmos.DrawSphere(leftShoulder.position, shoulderRoleSphereRadius);
            Gizmos.color = rightCol;
            Gizmos.DrawSphere(rightShoulder.position, shoulderRoleSphereRadius);
        }
    }
}
