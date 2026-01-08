using System;
using UnityEngine;

[DefaultExecutionOrder(350)]
public class GeckoNeckHeadTracking : MonoBehaviour
{
    [Serializable]
    public class BoneSettings
    {
        public Transform bone;

        [Header("Bone Axis (LOCAL)")]
        public Vector3 forwardLocal = Vector3.forward;
        public Vector3 upLocal = Vector3.up;

        [Header("Weight")]
        [Range(0f, 1f)]
        public float weight = 0.7f;

        [Header("Limits (relative to bind pose)")]
        public bool useLimits = true;
        public float yawLimit = 45f;
        public float pitchLimit = 25f;
        public float rollLimit = 15f;
    }

    [Header("Bones (order matters: neck -> head)")]
    public BoneSettings[] bones;

    [Header("Target")]
    public Transform aimTarget;

    [Tooltip("If null, uses this.transform.up as world up reference.")]
    public Transform characterRoot;

    [Header("Behavior")]
    [Range(0f, 1.5f)]
    public float strength = 1f;

    [Range(0f, 40f)]
    public float followSpeed = 24f;

    public bool planarAimOnly = true;
    public bool useCharacterUpAsWorldUp = true;

    [Header("Bind Pose")]
    public bool restoreOnDisable = true;

    private Quaternion[] _bindLocal;
    private bool _inited;

    private void Awake() => CacheBind();
    private void OnEnable() => CacheBind();

    private void OnDisable()
    {
        if (restoreOnDisable) RestoreBind();
    }

    private void LateUpdate()
    {
        if (strength <= 0f) return;
        if (aimTarget == null) return;
        if (bones == null || bones.Length == 0) return;
        if (!_inited) CacheBind();

        float dt = Time.deltaTime;
        float k = followSpeed <= 0f ? 1f : (1f - Mathf.Exp(-followSpeed * dt));
        float globalAlpha = Mathf.Clamp01(strength) * k;

        Vector3 worldUp = GetWorldUp();

        // IMPORTANT: neck -> head order prevents double-application and feels organic.
        for (int i = 0; i < bones.Length; i++)
        {
            var bs = bones[i];
            if (bs == null || bs.bone == null) continue;

            float w = Mathf.Clamp01(bs.weight);
            if (w <= 0f) continue;

            Vector3 toTarget = aimTarget.position - bs.bone.position;
            if (planarAimOnly) toTarget = Vector3.ProjectOnPlane(toTarget, worldUp);

            if (toTarget.sqrMagnitude < 1e-6f) continue;

            // Desired world rotation for THIS bone (not shared delta)
            Quaternion desiredWorld = DesiredWorldRotationForBone(bs, toTarget.normalized, worldUp);

            // Delta from current to desired
            Quaternion currentWorld = bs.bone.rotation;
            Quaternion delta = desiredWorld * Quaternion.Inverse(currentWorld);

            // Smooth + weight
            float alpha = globalAlpha * w;
            Quaternion deltaApply = Quaternion.Slerp(Quaternion.identity, delta, alpha);

            Quaternion newWorld = deltaApply * currentWorld;

            // Convert to local using actual parent (handles hierarchy mismatches)
            Quaternion parentInv = bs.bone.parent ? Quaternion.Inverse(bs.bone.parent.rotation) : Quaternion.identity;
            Quaternion newLocal = parentInv * newWorld;

            if (bs.useLimits)
                newLocal = LimitRelativeToBind(newLocal, _bindLocal[i], bs);

            bs.bone.localRotation = newLocal;
        }
    }

    private Vector3 GetWorldUp()
    {
        if (!useCharacterUpAsWorldUp) return Vector3.up;
        Transform t = characterRoot != null ? characterRoot : transform;
        return t != null ? t.up : Vector3.up;
    }

    private static Quaternion DesiredWorldRotationForBone(BoneSettings bs, Vector3 desiredForwardWorld, Vector3 worldUp)
    {
        Vector3 fL = SafeNormalize(bs.forwardLocal, Vector3.forward);
        Vector3 uL = SafeNormalize(bs.upLocal, Vector3.up);

        if (Vector3.Cross(fL, uL).sqrMagnitude < 1e-6f)
            uL = (Mathf.Abs(Vector3.Dot(fL, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

        // "canonical" rotation that points +Z forward and +Y up in world
        Quaternion basis = Quaternion.LookRotation(desiredForwardWorld, worldUp);

        // map bone's (forwardLocal, upLocal) to canonical (Vector3.forward, Vector3.up)
        Quaternion localAxisToCanonical = Quaternion.Inverse(Quaternion.LookRotation(fL, uL));

        return basis * localAxisToCanonical;
    }

    private static Quaternion LimitRelativeToBind(Quaternion localRot, Quaternion bindLocal, BoneSettings bs)
    {
        Quaternion d = Quaternion.Inverse(bindLocal) * localRot;

        Vector3 e = d.eulerAngles;
        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        e.y = Mathf.Clamp(e.y, -Mathf.Abs(bs.yawLimit), Mathf.Abs(bs.yawLimit));
        e.x = Mathf.Clamp(e.x, -Mathf.Abs(bs.pitchLimit), Mathf.Abs(bs.pitchLimit));
        e.z = Mathf.Clamp(e.z, -Mathf.Abs(bs.rollLimit), Mathf.Abs(bs.rollLimit));

        return bindLocal * Quaternion.Euler(e);
    }

    private static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        => v.sqrMagnitude < 1e-8f ? fallback : v.normalized;

    private void CacheBind()
    {
        if (bones == null || bones.Length == 0) return;

        _bindLocal = new Quaternion[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            _bindLocal[i] = (bones[i] != null && bones[i].bone != null)
                ? bones[i].bone.localRotation
                : Quaternion.identity;
        }
        _inited = true;
    }

    public void RestoreBind()
    {
        if (!_inited) return;
        if (_bindLocal == null || bones == null) return;

        int n = Mathf.Min(_bindLocal.Length, bones.Length);
        for (int i = 0; i < n; i++)
        {
            if (bones[i] != null && bones[i].bone != null)
                bones[i].bone.localRotation = _bindLocal[i];
        }
    }
}
