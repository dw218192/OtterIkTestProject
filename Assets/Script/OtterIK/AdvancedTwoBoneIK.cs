// AdvancedTwoBoneIK.cs
using System;
using UnityEngine;

[DefaultExecutionOrder(360)]
public class AdvancedTwoBoneIK : MonoBehaviour
{
    [Serializable]
    public class BoneSettings
    {
        public Transform bone;

        [Header("Axis (LOCAL)")]
        [Tooltip("Bone's local axis that should point toward the child/target direction.")]
        public Vector3 forwardLocal = Vector3.forward;

        [Tooltip("Bone's local 'up' axis used to stabilize twist (must not be parallel to forwardLocal).")]
        public Vector3 upLocal = Vector3.up;

        [Header("Weight")]
        [Range(0f, 1f)]
        public float weight = 1f;

        [Header("Limits (relative to bind pose, local space)")]
        public bool useEulerLimits = true;
        public float yawLimit = 60f;   // around local Y (approx)
        public float pitchLimit = 60f; // around local X (approx)
        public float rollLimit = 45f;  // around local Z (approx)

        [Header("Optional Hinge (recommended for elbow)")]
        public bool useHinge = false;

        [Tooltip("Hinge axis in THIS bone's local space (e.g. Vector3.right).")]
        public Vector3 hingeAxisLocal = Vector3.right;

        [Tooltip("Min hinge angle (deg) relative to bind pose, signed around hingeAxisLocal.")]
        public float hingeMinDeg = -5f;

        [Tooltip("Max hinge angle (deg) relative to bind pose, signed around hingeAxisLocal.")]
        public float hingeMaxDeg = 130f;
    }

    [Header("IK Chain (non-adjacent hierarchy OK)")]
    public BoneSettings root;   // shoulder / upper arm
    public BoneSettings mid;    // elbow / forearm
    public BoneSettings end;    // wrist / hand (position is driven indirectly, rotation optional)

    [Header("Targets")]
    public Transform target;    // desired wrist position (and optionally orientation)
    public Transform pole;      // pole position defining bend plane (elbow direction)

    [Header("Solve")]
    [Tooltip("If true, clamp unreachable targets to max reach (no stretch).")]
    public bool clampToMaxReach = true;

    [Tooltip("If true, allow slight stretch when target is beyond reach.")]
    public bool allowStretch = false;

    [Tooltip("Max stretch ratio, e.g. 1.05 = 5% stretch.")]
    [Range(1f, 1.3f)]
    public float maxStretch = 1.05f;

    [Header("Smoothing")]
    [Tooltip("Higher = snappier. 0 = no smoothing.")]
    public float followSpeed = 0f;

    [Range(0f, 1f)]
    public float globalWeight = 1f;

    [Header("End Effector Orientation (optional)")]
    public bool alignEndRotation = false;

    [Range(0f, 1f)]
    public float endRotationWeight = 1f;

    [Tooltip("If enabled, uses target.forward/target.up. If disabled, uses target.rotation directly.")]
    public bool useTargetForwardUp = true;

    [Header("Bind Pose")]
    public bool restoreOnDisable = true;

    private Quaternion _bindRootLocal, _bindMidLocal, _bindEndLocal;
    private bool _inited;

    private void Awake() => CacheBindPose();
    private void OnEnable() => CacheBindPose();

    private void OnDisable()
    {
        if (restoreOnDisable) RestoreBindPose();
    }

    private void CacheBindPose()
    {
        if (root?.bone == null || mid?.bone == null || end?.bone == null) return;
        _bindRootLocal = root.bone.localRotation;
        _bindMidLocal = mid.bone.localRotation;
        _bindEndLocal = end.bone.localRotation;
        _inited = true;
    }

    public void RestoreBindPose()
    {
        if (!_inited) return;
        if (root?.bone) root.bone.localRotation = _bindRootLocal;
        if (mid?.bone) mid.bone.localRotation = _bindMidLocal;
        if (end?.bone) end.bone.localRotation = _bindEndLocal;
    }

    private void LateUpdate()
    {
        if (globalWeight <= 0f) return;
        if (!_inited) CacheBindPose();

        if (root?.bone == null || mid?.bone == null || end?.bone == null || target == null || pole == null)
            return;

        float dt = Time.deltaTime;
        float alpha = ComputeAlpha(dt);

        // 1) Gather positions
        Vector3 A = root.bone.position;
        Vector3 B = mid.bone.position;
        Vector3 C = target.position;

        float l1 = Vector3.Distance(A, B);
        float l2 = Vector3.Distance(B, end.bone.position);

        if (l1 < 1e-5f || l2 < 1e-5f)
            return;

        Vector3 AC = C - A;
        float d = AC.magnitude;

        if (d < 1e-6f)
            return;

        Vector3 dirAC = AC / d;

        // 2) Decide bend plane using pole
        Vector3 toPole = pole.position - A;
        // Project pole direction onto plane perpendicular to dirAC
        Vector3 poleProj = toPole - dirAC * Vector3.Dot(toPole, dirAC);

        // If pole is nearly collinear with dirAC, pick any stable perpendicular
        if (poleProj.sqrMagnitude < 1e-8f)
        {
            poleProj = Vector3.Cross(dirAC, Vector3.up);
            if (poleProj.sqrMagnitude < 1e-8f)
                poleProj = Vector3.Cross(dirAC, Vector3.right);
        }

        Vector3 bendDir = poleProj.normalized;
        Vector3 planeNormal = Vector3.Cross(dirAC, bendDir).normalized;

        // 3) Clamp / stretch distance
        float maxReach = l1 + l2;
        float minReach = Mathf.Abs(l1 - l2);

        float dUsed = d;

        if (allowStretch && dUsed > maxReach)
            dUsed = Mathf.Min(dUsed, maxReach * Mathf.Max(1f, maxStretch));
        else if (clampToMaxReach)
            dUsed = Mathf.Min(dUsed, maxReach - 1e-4f);

        // Avoid inside triangle degeneracy
        dUsed = Mathf.Max(dUsed, minReach + 1e-4f);

        // 4) Solve elbow position using law of cosines
        // x along dirAC from A, y along bendDir within the plane
        float cosA = (dUsed * dUsed + l1 * l1 - l2 * l2) / (2f * dUsed * l1);
        cosA = Mathf.Clamp(cosA, -1f, 1f);

        float angA = Mathf.Acos(cosA); // radians
        float x = Mathf.Cos(angA) * l1;
        float y = Mathf.Sin(angA) * l1;

        Vector3 Bsol = A + dirAC * x + bendDir * y;

        // 5) Compute desired world rotations for root & mid
        Quaternion desiredRootWorld = DesiredWorldRotationForBone(
            root, forwardWorld: (Bsol - A).normalized, planeNormal: planeNormal);

        Quaternion desiredMidWorld = DesiredWorldRotationForBone(
            mid, forwardWorld: (C - Bsol).normalized, planeNormal: planeNormal);

        // 6) Apply rotations with smoothing + per-bone weight + limits (in local space)
        ApplyBoneRotation(root, desiredRootWorld, _bindRootLocal, alpha);
        ApplyBoneRotation(mid, desiredMidWorld, _bindMidLocal, alpha);

        // 7) Optional: end effector orientation (recommend splitting to palm IK later)
        if (alignEndRotation)
        {
            Quaternion desiredEndWorld = ComputeEndDesiredWorld();
            ApplyBoneRotation(end, desiredEndWorld, _bindEndLocal, alpha * Mathf.Clamp01(endRotationWeight));
        }
    }

    private float ComputeAlpha(float dt)
    {
        float gw = Mathf.Clamp01(globalWeight);
        if (followSpeed <= 0f) return gw;
        float k = 1f - Mathf.Exp(-followSpeed * Mathf.Max(0f, dt));
        return gw * k;
    }

    private Quaternion ComputeEndDesiredWorld()
    {
        if (!useTargetForwardUp)
            return target.rotation;

        Vector3 f = target.forward;
        Vector3 u = target.up;
        if (f.sqrMagnitude < 1e-8f) f = Vector3.forward;
        if (u.sqrMagnitude < 1e-8f) u = Vector3.up;

        // Map the target's forward/up onto end bone axes
        Vector3 fL = SafeNormalize(end.forwardLocal, Vector3.forward);
        Vector3 uL = SafeNormalize(end.upLocal, Vector3.up);
        if (Vector3.Cross(fL, uL).sqrMagnitude < 1e-8f)
            uL = (Mathf.Abs(Vector3.Dot(fL, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

        Quaternion basis = Quaternion.LookRotation(f.normalized, u.normalized);
        Quaternion localAxisToCanonical = Quaternion.Inverse(Quaternion.LookRotation(fL, uL));
        return basis * localAxisToCanonical;
    }

    private static Quaternion DesiredWorldRotationForBone(BoneSettings bs, Vector3 forwardWorld, Vector3 planeNormal)
    {
        // We stabilize twist using the bend plane:
        // upWorld lies in the bend plane and is perpendicular to forwardWorld.
        Vector3 upWorld = Vector3.Cross(planeNormal, forwardWorld);
        if (upWorld.sqrMagnitude < 1e-8f)
        {
            // fallback: any stable up
            upWorld = Vector3.up;
        }
        upWorld.Normalize();

        Vector3 fL = SafeNormalize(bs.forwardLocal, Vector3.forward);
        Vector3 uL = SafeNormalize(bs.upLocal, Vector3.up);

        if (Vector3.Cross(fL, uL).sqrMagnitude < 1e-8f)
            uL = (Mathf.Abs(Vector3.Dot(fL, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

        Quaternion basis = Quaternion.LookRotation(forwardWorld, upWorld);
        Quaternion localAxisToCanonical = Quaternion.Inverse(Quaternion.LookRotation(fL, uL));
        return basis * localAxisToCanonical;
    }

    private void ApplyBoneRotation(BoneSettings bs, Quaternion desiredWorld, Quaternion bindLocal, float alpha)
    {
        if (bs == null || bs.bone == null) return;

        float w = Mathf.Clamp01(bs.weight);
        if (w <= 0f) return;

        float a = Mathf.Clamp01(alpha) * w;

        Quaternion currentWorld = bs.bone.rotation;
        Quaternion blendedWorld = Quaternion.Slerp(currentWorld, desiredWorld, a);

        Quaternion parentInv = bs.bone.parent != null ? Quaternion.Inverse(bs.bone.parent.rotation) : Quaternion.identity;
        Quaternion newLocal = parentInv * blendedWorld;

        // Limits are applied relative to bind pose
        newLocal = ApplyLimitsRelativeToBind(newLocal, bindLocal, bs);

        bs.bone.localRotation = newLocal;
    }

    private static Quaternion ApplyLimitsRelativeToBind(Quaternion localRot, Quaternion bindLocal, BoneSettings bs)
    {
        Quaternion delta = Quaternion.Inverse(bindLocal) * localRot;

        // Optional hinge (good for elbow)
        if (bs.useHinge)
        {
            Vector3 axis = SafeNormalize(bs.hingeAxisLocal, Vector3.right);

            // swing-twist decomposition around axis
            SwingTwist(delta, axis, out Quaternion swing, out Quaternion twist);

            // signed angle of twist around axis
            float twistAngle = SignedAngleAroundAxis(twist, axis);
            float clamped = Mathf.Clamp(twistAngle, bs.hingeMinDeg, bs.hingeMaxDeg);

            // Rebuild: clamp twist, keep swing
            Quaternion clampedTwist = Quaternion.AngleAxis(clamped, axis);
            delta = swing * clampedTwist;

            // Optionally still clamp tiny residual roll/yaw/pitch if desired
            if (!bs.useEulerLimits)
                return bindLocal * delta;
        }

        if (bs.useEulerLimits)
        {
            Vector3 e = delta.eulerAngles;
            e.x = NormalizeAngle(e.x);
            e.y = NormalizeAngle(e.y);
            e.z = NormalizeAngle(e.z);

            e.y = Mathf.Clamp(e.y, -Mathf.Abs(bs.yawLimit), Mathf.Abs(bs.yawLimit));
            e.x = Mathf.Clamp(e.x, -Mathf.Abs(bs.pitchLimit), Mathf.Abs(bs.pitchLimit));
            e.z = Mathf.Clamp(e.z, -Mathf.Abs(bs.rollLimit), Mathf.Abs(bs.rollLimit));

            delta = Quaternion.Euler(e);
        }

        return bindLocal * delta;
    }

    // --- helpers ---

    private static void SwingTwist(Quaternion q, Vector3 twistAxis, out Quaternion swing, out Quaternion twist)
    {
        // project rotation axis onto twistAxis in quaternion vector part
        Vector3 r = new Vector3(q.x, q.y, q.z);
        Vector3 p = Vector3.Project(r, twistAxis);

        twist = new Quaternion(p.x, p.y, p.z, q.w);
        twist = NormalizeQuat(twist);

        swing = q * Quaternion.Inverse(twist);
    }

    private static Quaternion NormalizeQuat(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (mag < 1e-8f) return Quaternion.identity;
        return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
    }

    private static float SignedAngleAroundAxis(Quaternion q, Vector3 axis)
    {
        // assumes q is a (near) pure twist around axis after decomposition
        q = NormalizeQuat(q);
        float angle = 2f * Mathf.Acos(Mathf.Clamp(q.w, -1f, 1f)) * Mathf.Rad2Deg;

        // Determine sign using quaternion vector part direction
        Vector3 v = new Vector3(q.x, q.y, q.z);
        float sign = Mathf.Sign(Vector3.Dot(v, axis));
        if (float.IsNaN(sign) || sign == 0f) sign = 1f;

        // map to [-180,180]
        angle = NormalizeAngle(angle);
        return angle * sign;
    }

    private static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        if (v.sqrMagnitude < 1e-8f) return fallback;
        return v.normalized;
    }
}
