using UnityEngine;

/// <summary>
/// Refined SoftSpineDriver (Top-down, Y-up, XZ movement) with:
/// - Yaw-only bending (prevents "looking toward sky").
/// - Rig axis support (your cat/otter head forward axis is +Y).
/// - Stable direction source priority: velocity (optional) -> target -> fallback.
/// - Per-bone soft follow + tail lag (fish-like softness).
/// - Per-bone bend limit from bind pose (safety).
///
/// IMPORTANT SETUP:
/// 1) bones: drag spine bones in order FRONT -> BACK (chest/neck -> pelvis/tailRoot)
/// 2) target: set to your HeadIK_Guide (recommended) and keep its Y locked to water plane or head height.
/// 3) If your rig's "forward" is +Y, keep boneForwardAxis = LocalY (default).
/// </summary>
[ExecuteAlways]
public class SoftSpineDriverRefined : MonoBehaviour
{
    public enum BoneForwardAxis { LocalX, LocalY, LocalZ }

    [Header("Spine bones (front -> back)")]
    public Transform[] bones;

    [Header("Desired Direction Source (priority: velocity -> target -> fallback)")]
    public bool useVelocityIfAvailable = false;
    public Rigidbody rb;

    [Tooltip("If set, spine bends toward this target (recommended: HeadIK_Guide).")]
    public Transform target;

    [Header("Top-down Constraints")]
    [Tooltip("Project all direction to XZ plane and rotate only around worldUp.")]
    public bool yawOnly = true;

    [Tooltip("World up axis for your game. Usually Vector3.up.")]
    public Vector3 worldUp = Vector3.up;

    [Tooltip("If true, desired direction is projected onto plane orthogonal to worldUp (XZ).")]
    public bool constrainToXZ = true;

    [Header("Rig Axis (CRITICAL)")]
    [Tooltip("Which LOCAL axis of each bone points forward along the character (nose direction). For your cat/otter head: LocalY.")]
    public BoneForwardAxis boneForwardAxis = BoneForwardAxis.LocalY;

    [Tooltip("Which LOCAL axis of the bone represents 'up' for stabilizing twist (rarely needed in yawOnly mode).")]
    public BoneForwardAxis boneUpAxis = BoneForwardAxis.LocalZ;

    [Header("Softness")]
    [Tooltip("Overall responsiveness. Higher = stiffer (more immediate).")]
    public float baseFollowSpeed = 12f;

    [Tooltip("Tail is slower than head. >1 makes tail lag more (softer).")]
    public float tailLagMultiplier = 3.0f;

    [Tooltip("Head-to-tail influence curve. Higher = head follows more strongly, tail follows less.")]
    public float headInfluencePower = 1.6f;

    [Tooltip("Max degrees each bone may deviate from its bind/rest local rotation.")]
    public float maxBendDegPerBone = 25f;

    [Header("Optional (very subtle) wave")]
    public bool addWave = false;
    public float waveYawDeg = 5f;
    public float waveFrequency = 1.2f;
    public float wavePhasePerBone = 0.35f;
    public float waveTailBoost = 1.8f;

    [Header("Debug")]
    public bool drawGizmos = true;
    public float gizmoAxisLen = 0.15f;

    private Quaternion[] restLocalRot;
    private Transform[] parents;

    void Awake() => InitCache();
    void OnEnable() => InitCache();

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

        if (worldUp.sqrMagnitude < 1e-6f) worldUp = Vector3.up;
        worldUp.Normalize();
    }

    void LateUpdate()
    {
        if (bones == null || bones.Length < 2) return;
        if (restLocalRot == null || restLocalRot.Length != bones.Length) InitCache();
        if (worldUp.sqrMagnitude < 1e-6f) worldUp = Vector3.up;
        worldUp.Normalize();

        Vector3 desiredDir = GetDesiredDirection();
        if (desiredDir.sqrMagnitude < 1e-6f) return;

        if (constrainToXZ)
        {
            desiredDir = Vector3.ProjectOnPlane(desiredDir, worldUp);
            if (desiredDir.sqrMagnitude < 1e-6f) return;
            desiredDir.Normalize();
        }

        // Reference forward direction in world (flat)
        Vector3 refForward = Vector3.ProjectOnPlane(transform.forward, worldUp);
        if (refForward.sqrMagnitude < 1e-6f) refForward = Vector3.forward;
        refForward.Normalize();

        for (int i = 0; i < bones.Length; i++)
        {
            Transform b = bones[i];
            if (!b) continue;

            float t = (bones.Length == 1) ? 0f : (float)i / (bones.Length - 1);  // 0 front, 1 back

            // How strongly this bone should follow the global desired direction (front > back)
            float followAlpha = Mathf.Pow(1f - t, headInfluencePower);

            // Current bone forward (in world), flattened
            Vector3 boneFwdWorld = GetBoneForwardWorld(b);
            if (constrainToXZ) boneFwdWorld = Vector3.ProjectOnPlane(boneFwdWorld, worldUp);
            if (boneFwdWorld.sqrMagnitude < 1e-6f) boneFwdWorld = desiredDir;
            boneFwdWorld.Normalize();

            // Blend desired vs current so tail lags behind
            Vector3 boneDesiredDir = Vector3.Slerp(desiredDir, boneFwdWorld, 1f - followAlpha);
            if (constrainToXZ) boneDesiredDir = Vector3.ProjectOnPlane(boneDesiredDir, worldUp);
            if (boneDesiredDir.sqrMagnitude < 1e-6f) continue;
            boneDesiredDir.Normalize();

            // Optional subtle wave (yaw only)
            if (addWave)
            {
                float tailBoost = Mathf.Lerp(1f, waveTailBoost, t);
                float phase = (Time.time * (Mathf.PI * 2f) * waveFrequency) - i * wavePhasePerBone;
                float yaw = Mathf.Sin(phase) * waveYawDeg * tailBoost;
                boneDesiredDir = Quaternion.AngleAxis(yaw, worldUp) * boneDesiredDir;
                boneDesiredDir.Normalize();
            }

            // Build target world rotation
            Quaternion targetWorldRot = yawOnly
                ? BuildYawOnlyTargetWorldRotation(i, b, refForward, boneDesiredDir)
                : BuildFull3DTargetWorldRotation(boneDesiredDir);

            // Convert to target local rotation
            Transform p = parents[i];
            Quaternion parentWorldRot = p ? p.rotation : Quaternion.identity;
            Quaternion targetLocal = Quaternion.Inverse(parentWorldRot) * targetWorldRot;

            // Bend limit around bind pose
            Quaternion limitedLocal = LimitFromRest(targetLocal, restLocalRot[i], maxBendDegPerBone);

            // Smooth follow (tail slower)
            float speed = baseFollowSpeed / Mathf.Lerp(1f, tailLagMultiplier, t);
            float dt = Application.isPlaying ? Time.deltaTime : 0.016f;
            float k = 1f - Mathf.Exp(-speed * dt);

            b.localRotation = Quaternion.Slerp(b.localRotation, limitedLocal, k);
        }
    }

    Vector3 GetDesiredDirection()
    {
        Vector3 dir = Vector3.zero;

        if (useVelocityIfAvailable && rb)
        {
            Vector3 v = rb.velocity;
            if (constrainToXZ) v = Vector3.ProjectOnPlane(v, worldUp);
            if (v.sqrMagnitude > 0.0001f) dir = v.normalized;
        }

        if (dir.sqrMagnitude < 1e-6f && target && bones != null && bones.Length > 0 && bones[0])
        {
            dir = target.position - bones[0].position;
            if (constrainToXZ) dir = Vector3.ProjectOnPlane(dir, worldUp);
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        }

        if (dir.sqrMagnitude < 1e-6f)
        {
            dir = transform.forward;
            if (constrainToXZ) dir = Vector3.ProjectOnPlane(dir, worldUp);
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        }

        return dir;
    }

    Vector3 GetBoneForwardWorld(Transform bone)
    {
        switch (boneForwardAxis)
        {
            case BoneForwardAxis.LocalX: return bone.right;
            case BoneForwardAxis.LocalY: return bone.up;
            default: return bone.forward; // LocalZ
        }
    }

    Vector3 GetBoneUpWorld(Transform bone)
    {
        switch (boneUpAxis)
        {
            case BoneForwardAxis.LocalX: return bone.right;
            case BoneForwardAxis.LocalY: return bone.up;
            default: return bone.forward;
        }
    }

    Quaternion BuildFull3DTargetWorldRotation(Vector3 desiredDir)
    {
        // Full 3D aiming (may pitch). Kept for completeness.
        return Quaternion.LookRotation(desiredDir, worldUp);
    }

    Quaternion BuildYawOnlyTargetWorldRotation(int boneIndex, Transform bone, Vector3 refForwardFlat, Vector3 desiredDirFlat)
    {
        // We want to rotate around worldUp only, but respecting that the bone's "forward" might be local Y.
        // Strategy:
        // 1) Compute a world-space yaw rotation that maps refForwardFlat -> desiredDirFlat around worldUp.
        // 2) Apply that yaw rotation onto this bone's "rest world rotation" (parent * restLocalRot).
        Transform p = parents[boneIndex];
        Quaternion parentWorldRot = p ? p.rotation : Quaternion.identity;

        Quaternion restWorldRot = parentWorldRot * restLocalRot[boneIndex];

        // Compute yaw from reference forward to desired direction
        float yawDeg = Vector3.SignedAngle(refForwardFlat, desiredDirFlat, worldUp);
        Quaternion yawRot = Quaternion.AngleAxis(yawDeg, worldUp);

        // Apply yaw onto the rest pose
        // This keeps the bone from pitching toward sky regardless of bone forward axis.
        return yawRot * restWorldRot;
    }

    static Quaternion LimitFromRest(Quaternion targetLocal, Quaternion restLocal, float maxDeg)
    {
        if (maxDeg <= 0f) return restLocal;

        Quaternion delta = Quaternion.Inverse(restLocal) * targetLocal;
        delta.ToAngleAxis(out float angle, out Vector3 axis);

        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f)
            return restLocal;

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
            Gizmos.DrawLine(p, p + GetBoneForwardWorld(bones[i]).normalized * gizmoAxisLen);

            // Draw chain
            if (i < bones.Length - 1 && bones[i + 1])
                Gizmos.DrawLine(p, bones[i + 1].position);
        }
    }
}
