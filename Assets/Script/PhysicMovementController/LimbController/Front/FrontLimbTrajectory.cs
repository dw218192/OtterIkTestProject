using UnityEngine;

public class FrontLimbTrajectory : MonoBehaviour
{
    [Header("输入源")]
    public DynamicIndirectIK_Stabilized ikSource;

    [Header("Authoring (World -> Local Bake)")]
    [Tooltip("世界空间：椭球中心相对肩膀的偏移（更符合直觉的编辑方式）。Bake 后会写入 localCenter。")]
    public Vector3 offset = new Vector3(0f, -0.1f, 0f);

    [Tooltip("世界坐标轴欧拉：用于调节椭球姿态（在世界坐标系中转）。Bake 后会写入 localEllipsoidRotation。")]
    public Vector3 extraRotation = Vector3.zero;

    [Tooltip("若勾选：进入 Play 时自动 Bake 一次，把 offset/extraRotation 转成 local 并锁死。")]
    public bool autoBakeOnPlay = true;

    [Tooltip("若勾选：在编辑期当你改动 offset/extraRotation 或 local 值时，自动互相同步（避免两套参数不一致）。")]
    public bool keepAuthoringSyncedInEditor = true;

    [Header("椭球定义（半径在 shoulder 局部轴上）")]
    public Vector3 radius = new Vector3(0.15f, 0.25f, 0.35f);

    // ===== Runtime truth (baked, hidden) =====
    [Header("Baked Runtime (Do not edit)")]
    [SerializeField, HideInInspector] private Vector3 localCenter = new Vector3(0f, -0.1f, 0f);
    [SerializeField, HideInInspector] private Quaternion localEllipsoidRotation = Quaternion.identity;

    [Header("平面调节（截线切面，不是椭球姿态）")]
    [Range(-180, 180)] public float tiltDegree = 0f;
    [Range(-180, 180)] public float planeTwistDeg = 0f;

    [Header("外侧判定回归阈值")]
    public float alphaReturnThreshold = 1.2f;

    private bool _bakedThisPlay = false;

#if UNITY_EDITOR
    [SerializeField, HideInInspector] private Vector3 _lastOffset;
    [SerializeField, HideInInspector] private Vector3 _lastExtraRotation;
    [SerializeField, HideInInspector] private Vector3 _lastLocalCenter;
    [SerializeField, HideInInspector] private Quaternion _lastLocalEllipsoidRotation;
#endif

    private void Awake()
    {
        if (autoBakeOnPlay && !_bakedThisPlay)
        {
            BakeWorldToLocal();
            _bakedThisPlay = true;
        }
    }

    private void Reset()
    {
        if (TryGetAnyShoulder(out var shoulder))
        {
            // 初始化 offset/extraRotation 为当前 baked 的世界表达
            Vector3 worldCenter = shoulder.TransformPoint(localCenter);
            offset = worldCenter - shoulder.position;

            Quaternion worldRot = shoulder.rotation * localEllipsoidRotation;
            Quaternion q = worldRot * Quaternion.Inverse(shoulder.rotation); // world-space delta
            extraRotation = q.eulerAngles;
        }
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (Application.isPlaying || !keepAuthoringSyncedInEditor) return;
        if (!TryGetAnyShoulder(out var shoulder)) return;

        bool authoringChanged = (offset != _lastOffset) || (extraRotation != _lastExtraRotation);
        bool bakedChanged = (localCenter != _lastLocalCenter) || (localEllipsoidRotation != _lastLocalEllipsoidRotation);

        // 谁改了就同步谁（避免互相覆盖导致无法编辑）
        if (authoringChanged && !bakedChanged)
        {
            BakeWorldToLocal_Internal(shoulder);
        }
        else if (bakedChanged && !authoringChanged)
        {
            SyncLocalToWorldAuthoring_Internal(shoulder);
        }

        _lastOffset = offset;
        _lastExtraRotation = extraRotation;
        _lastLocalCenter = localCenter;
        _lastLocalEllipsoidRotation = localEllipsoidRotation;
#endif
    }

    [ContextMenu("Bake Authoring (offset/extraRotation) -> Local")]
    public void BakeWorldToLocal()
    {
        if (!TryGetAnyShoulder(out var shoulder))
        {
            Debug.LogWarning($"{name}: Bake failed (missing shoulder/ikSource).");
            return;
        }

        BakeWorldToLocal_Internal(shoulder);

#if UNITY_EDITOR
        _lastOffset = offset;
        _lastExtraRotation = extraRotation;
        _lastLocalCenter = localCenter;
        _lastLocalEllipsoidRotation = localEllipsoidRotation;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    private void BakeWorldToLocal_Internal(Transform shoulder)
    {
        // 以“右肩优先”的 shoulder 为参考 bake；左侧运行时依旧通过 localCenter.x 镜像
        Vector3 worldCenter = shoulder.position + offset;

        // extraRotation 是“世界坐标轴”的旋转：先在世界里转，再乘上肩膀旋转
        Quaternion worldRot = Quaternion.Euler(extraRotation) * shoulder.rotation;

        localCenter = shoulder.InverseTransformPoint(worldCenter);

        // 运行时我们用：ellipsoidRotW = shoulder.rotation * localEllipsoidRotation
        localEllipsoidRotation = Quaternion.Inverse(shoulder.rotation) * worldRot;
    }

    private void SyncLocalToWorldAuthoring_Internal(Transform shoulder)
    {
        Vector3 worldCenter = shoulder.TransformPoint(localCenter);
        offset = worldCenter - shoulder.position;

        Quaternion worldRot = shoulder.rotation * localEllipsoidRotation;
        Quaternion q = worldRot * Quaternion.Inverse(shoulder.rotation);
        extraRotation = q.eulerAngles;
    }

    private bool TryGetAnyShoulder(out Transform shoulder)
    {
        shoulder = null;
        if (!ikSource) return false;
        shoulder = ikSource.rightShoulder ? ikSource.rightShoulder : ikSource.leftShoulder;
        return shoulder != null;
    }

    // ======= Public helpers for Visualizer =======
    public Vector3 LocalCenterBaked => localCenter;

    // Mirror helper: mirror a rotation across the YZ plane (X flip) in the frame where X means "right".
    // This is more robust than negating Euler angles.
    private static Quaternion MirrorRotationAcrossYZ_Plane(Quaternion q)
    {
        Matrix4x4 R = Matrix4x4.Rotate(q);
        Matrix4x4 M = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)); // diag(-1,1,1)
        Matrix4x4 RM = M * R * M;
        return RM.rotation;
    }

    public Quaternion GetEllipsoidWorldRotation(bool isLeft)
    {
        if (!ikSource) return Quaternion.identity;
        Transform sJoint = isLeft ? ikSource.leftShoulder : ikSource.rightShoulder;
        if (!sJoint) return Quaternion.identity;

        // Bake stores the "right side" local rotation. Left side should mirror it for strict symmetry.
        Quaternion localRot = localEllipsoidRotation;
        if (isLeft) localRot = MirrorRotationAcrossYZ_Plane(localRot);

        return sJoint.rotation * localRot;
    }

    private Vector3 GetCenterW(bool isLeft, Transform sJoint)
    {
        Vector3 lc = localCenter;
        if (isLeft) lc.x *= -1f; // 左侧镜像（沿 shoulder local X）
        return sJoint.TransformPoint(lc);
    }

    public Vector3 GetWorldPoint(bool isLeft, float theta)
    {
        if (!ikSource || !ikSource.spineSource) return Vector3.zero;

        Transform sJoint = isLeft ? ikSource.leftShoulder : ikSource.rightShoulder;
        if (!sJoint) return Vector3.zero;

        Vector3 centerW = GetCenterW(isLeft, sJoint);

        if (!TryGetPlaneFrame(isLeft, out _, out Vector3 u, out Vector3 v, out _))
            return centerW;

        Vector3 dirW = (Mathf.Cos(theta) * u + Mathf.Sin(theta) * v).normalized;

        // 关键：椭球主轴应使用“椭球世界旋转”的逆，而不是 sJoint 的逆
        Quaternion ellipsoidRotW = GetEllipsoidWorldRotation(isLeft);
        Vector3 dirE = Quaternion.Inverse(ellipsoidRotW) * dirW; // dir in ellipsoid local frame

        float invK2 =
            (dirE.x * dirE.x) / (radius.x * radius.x) +
            (dirE.y * dirE.y) / (radius.y * radius.y) +
            (dirE.z * dirE.z) / (radius.z * radius.z);

        if (invK2 < 1e-12f) return centerW;

        float k = 1f / Mathf.Sqrt(invK2);
        return centerW + dirW * k;
    }

    public bool TryGetPlaneFrame(bool isLeft, out Vector3 centerW, out Vector3 u, out Vector3 v, out Vector3 normalW)
    {
        centerW = u = v = normalW = Vector3.zero;

        if (!ikSource || !ikSource.spineSource) return false;

        Transform sJoint = isLeft ? ikSource.leftShoulder : ikSource.rightShoulder;
        if (!sJoint) return false;

        centerW = GetCenterW(isLeft, sJoint);

        Vector3 stableUp = ikSource.spineSource.transform.up;

        normalW = GetBaseWorldNormal(isLeft, sJoint, stableUp);

        // 平面倾斜：仍沿 shoulder.forward（你原逻辑保留）
        float tilt = isLeft ? -tiltDegree : tiltDegree;
        Quaternion adjust = Quaternion.AngleAxis(tilt, sJoint.forward);
        normalW = (adjust * normalW).normalized;

        // 用 stableUp 作为参考 up，避免 roll 时 u/v 跳变
        Vector3 refUp = stableUp;
        Vector3 cross = Vector3.Cross(normalW, refUp);

        if (cross.sqrMagnitude < 1e-8f)
        {
            cross = Vector3.Cross(normalW, sJoint.right);
            if (cross.sqrMagnitude < 1e-8f)
                cross = Vector3.Cross(normalW, sJoint.forward);
        }

        u = cross.normalized;
        v = Vector3.Cross(normalW, u).normalized;

        // 平面内 twist
        float twistDeg = isLeft ? -planeTwistDeg : planeTwistDeg;
        if (Mathf.Abs(twistDeg) > 1e-4f)
        {
            Quaternion twist = Quaternion.AngleAxis(twistDeg, normalW);
            u = twist * u;
            v = twist * v;
        }

        return true;
    }

    private Vector3 GetBaseWorldNormal(bool isLeft, Transform sJoint, Vector3 stableUp)
    {
        bool isOuter = isLeft ? ikSource.isLeftOuter : ikSource.isRightOuter;

        if (!isOuter)
            return -stableUp;

        Vector3 center = (ikSource.leftShoulder.position + ikSource.rightShoulder.position) * 0.5f;
        Vector3 lagPoint = isLeft ? ikSource.WorldLeftPoint : ikSource.WorldRightPoint;

        Vector3 vecS = (sJoint.position - center);
        Vector3 vecL = (lagPoint - center);

        if (vecS.sqrMagnitude < 1e-10f || vecL.sqrMagnitude < 1e-10f)
            return -stableUp;

        vecS.Normalize();
        vecL.Normalize();

        Vector3 n = Vector3.Cross(vecS, vecL);

        if (n.sqrMagnitude < 1e-10f)
        {
            n = Vector3.Cross(vecS, stableUp);
            if (n.sqrMagnitude < 1e-10f)
                return -stableUp;
        }

        n.Normalize();
        n = (Vector3.Dot(n, stableUp) > 0f) ? -n : n;

        float ang = Vector3.Angle(vecS, vecL);
        float t = Mathf.InverseLerp(0f, alphaReturnThreshold, ang);
        return Vector3.Slerp(-stableUp, n, t).normalized;
    }
}
