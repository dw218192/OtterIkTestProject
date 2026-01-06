using UnityEngine;

[ExecuteAlways]
public class SoftSpineDriver : MonoBehaviour
{
    [Header("Spine bones (front -> back)")]
    public Transform[] bones;

    [Header("Direction source (optional)")]
    public Transform target;                 // 若设置：朝向 target
    public Rigidbody rb;                     // 若设置：用速度方向（可选）
    public bool useVelocityIfAvailable = true;

    [Header("Plane / Up")]
    public bool constrainToXZ = true;         // 你的项目是Y向上，运动主要在XZ
    public Vector3 worldUp = default;         // 默认 Vector3.up

    [Header("Softness")]
    [Tooltip("头部跟随快，尾部跟随慢。值越大整体越硬。")]
    public float baseFollowSpeed = 12f;

    [Tooltip("尾部额外变慢的倍率（>1更软）。")]
    public float tailLagMultiplier = 3.0f;

    [Tooltip("每节骨骼允许偏离其bind pose的最大角度（度）。")]
    public float maxBendDegPerBone = 25f;

    [Tooltip("沿链条分布跟随权重：越大越“头跟、尾不跟”（更像鱼）。")]
    public float headInfluencePower = 1.6f;

    [Header("Optional wave (very subtle)")]
    public bool addWave = false;
    public float waveYawDeg = 6f;             // 只做很小的“摆”
    public float waveFrequency = 1.2f;        // Hz
    public float wavePhasePerBone = 0.35f;    // 沿骨骼相位差
    public float waveTailBoost = 1.8f;        // 尾部摆幅更大

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public float gizmoAxisLen = 0.15f;

    Quaternion[] restLocalRot;
    Transform[] parents; // cache

    void Awake()
    {
        InitCache();
    }

    void OnEnable()
    {
        InitCache();
    }

    void InitCache()
    {
        if (bones == null || bones.Length == 0) return;

        restLocalRot = new Quaternion[bones.Length];
        parents = new Transform[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (!bones[i]) continue;
            restLocalRot[i] = bones[i].localRotation;
            parents[i] = bones[i].parent;
        }

        if (worldUp == default) worldUp = Vector3.up;
    }

    void LateUpdate()
    {
        if (bones == null || bones.Length < 2) return;
        if (restLocalRot == null || restLocalRot.Length != bones.Length) InitCache();
        if (worldUp == default) worldUp = Vector3.up;

        // 1) 计算“想朝向的方向”
        Vector3 desiredDir = GetDesiredDirection();
        if (desiredDir.sqrMagnitude < 1e-6f) return;

        // 2) 沿链条逐节设置旋转（只改 localRotation，让 skin 自己弯）
        for (int i = 0; i < bones.Length; i++)
        {
            Transform b = bones[i];
            if (!b) continue;

            float t = (bones.Length == 1) ? 0f : (float)i / (bones.Length - 1); // 0=head/front, 1=tail/back

            // 跟随权重：头部更贴近 desiredDir，尾部更保留原方向（产生弯曲）
            float followAlpha = Mathf.Pow(1f - t, headInfluencePower);

            // 目标方向：在“当前朝向”和“desiredDir”之间插值（越靠后越不跟）
            Vector3 currentFwd = b.forward;
            if (constrainToXZ)
            {
                currentFwd = Vector3.ProjectOnPlane(currentFwd, worldUp);
            }
            currentFwd = currentFwd.sqrMagnitude < 1e-6f ? desiredDir : currentFwd.normalized;

            Vector3 boneDesiredDir = Vector3.Slerp(desiredDir, currentFwd, 1f - followAlpha).normalized;

            // 可选：给一点点尾部更明显的波浪（非常小，主要用来“活”）
            if (addWave)
            {
                float tailBoost = Mathf.Lerp(1f, waveTailBoost, t);
                float phase = (Time.time * (Mathf.PI * 2f) * waveFrequency) - i * wavePhasePerBone;
                float yaw = Mathf.Sin(phase) * waveYawDeg * tailBoost;
                boneDesiredDir = Quaternion.AngleAxis(yaw, worldUp) * boneDesiredDir;
                boneDesiredDir.Normalize();
            }

            // 目标 world rotation（用 worldUp 锁住翻滚，避免扭麻花）
            Quaternion targetWorldRot = Quaternion.LookRotation(boneDesiredDir, worldUp);

            // 转回 local rotation
            Transform p = parents[i];
            Quaternion parentWorldRot = p ? p.rotation : Quaternion.identity;
            Quaternion targetLocal = Quaternion.Inverse(parentWorldRot) * targetWorldRot;

            // 3) 限制每节偏离 bind pose 的最大弯曲角（防折）
            Quaternion limitedLocal = LimitFromRest(targetLocal, restLocalRot[i], maxBendDegPerBone);

            // 4) 柔化：头快尾慢（尾部 lag 更大）
            float speed = baseFollowSpeed / Mathf.Lerp(1f, tailLagMultiplier, t);
            float dt = Application.isPlaying ? Time.deltaTime : 0.016f;
            float k = 1f - Mathf.Exp(-speed * dt);

            b.localRotation = Quaternion.Slerp(b.localRotation, limitedLocal, k);
        }
    }

    Vector3 GetDesiredDirection()
    {
        Vector3 dir = Vector3.zero;

        // 优先用速度方向（如果你有刚体）
        if (useVelocityIfAvailable && rb)
        {
            Vector3 v = rb.velocity;
            if (constrainToXZ) v = Vector3.ProjectOnPlane(v, worldUp);
            if (v.sqrMagnitude > 0.0001f) dir = v.normalized;
        }

        // 其次用 target（比如鼠标投影点/前进目标）
        if (dir.sqrMagnitude < 1e-6f && target && bones[0])
        {
            dir = (target.position - bones[0].position);
            if (constrainToXZ) dir = Vector3.ProjectOnPlane(dir, worldUp);
            if (dir.sqrMagnitude > 1e-6f) dir = dir.normalized;
        }

        // 最后 fallback：用当前物体 forward
        if (dir.sqrMagnitude < 1e-6f)
        {
            dir = transform.forward;
            if (constrainToXZ) dir = Vector3.ProjectOnPlane(dir, worldUp);
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        }

        return dir;
    }

    static Quaternion LimitFromRest(Quaternion targetLocal, Quaternion restLocal, float maxDeg)
    {
        if (maxDeg <= 0f) return restLocal;

        // delta = rest^-1 * target
        Quaternion delta = Quaternion.Inverse(restLocal) * targetLocal;

        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) return restLocal;

        // angle 是 0..180
        float clamped = Mathf.Min(angle, maxDeg);
        Quaternion limitedDelta = Quaternion.AngleAxis(clamped, axis.normalized);

        return restLocal * limitedDelta;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos || bones == null) return;

        Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
        for (int i = 0; i < bones.Length; i++)
        {
            if (!bones[i]) continue;

            Vector3 p = bones[i].position;
            Vector3 f = bones[i].forward * gizmoAxisLen;
            Vector3 r = bones[i].right * (gizmoAxisLen * 0.7f);

            Gizmos.DrawLine(p, p + f);
            Gizmos.DrawLine(p, p + r);

            if (i < bones.Length - 1 && bones[i + 1])
                Gizmos.DrawLine(p, bones[i + 1].position);
        }
    }
}
