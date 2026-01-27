using System;
using UnityEngine;

[DisallowMultipleComponent]
public class SoftSpineDriving : MonoBehaviour
{
    [Serializable]
    public class BoneDrive
    {
        public Transform bone;

        [Tooltip("Bone local-space forward axis (e.g. (0,0,1) for +Z).")]
        public Vector3 forwardAxis = Vector3.forward;

        [Tooltip("Bone local-space up axis (e.g. (0,1,0) for +Y). Must not be colinear with forwardAxis.")]
        public Vector3 upAxis = Vector3.up;

        [Range(0f, 1f)]
        public float weight = 0.2f;

        [Tooltip("Max swing angle (degrees) allowed for this bone.")]
        [Range(0f, 90f)]
        public float maxAngleDeg = 25f;

        [Tooltip("Extra per-bone sharpness multiplier (1 = same as global).")]
        [Range(0.1f, 5f)]
        public float sharpnessMul = 1f;

        [NonSerialized] public Quaternion bindLocalRot;
    }

    [Header("References")]
    public Transform spineAnchor;
    public Transform headIKGuide;

    [Header("Up / Plane")]
    public Transform upReference;
    public bool useWorldUp = false;

    [Tooltip("If true, only follow yaw around Up (very stable). If false, full 3D aim (still stable due to upAxis).")]
    public bool yawOnly = true;

    [Header("Follow")]
    [Range(0.01f, 30f)]
    public float followSharpness = 10f;

    [Range(0f, 10f)]
    public float deadZoneDeg = 0.25f;

    [Tooltip("Clamp total angle (deg) between anchor forward and intent direction (yaw-only uses yaw delta).")]
    [Range(0f, 180f)]
    public float maxTotalAngleDeg = 80f;

    [Header("Bones (spine1..neck, exclude head)")]
    public BoneDrive[] chain;

    [Header("Debug")]
    public bool drawDebug = true;

    private bool _initialized;
    private Vector3 _smoothedDirWorld;

    private void Reset()
    {
        spineAnchor = transform;
        upReference = transform;
    }

    private void Awake() => Initialize();

    private void OnValidate()
    {
        // Normalize axes in editor (avoid zero vectors)
        if (chain == null) return;
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i] == null) continue;
            if (chain[i].forwardAxis.sqrMagnitude < 1e-6f) chain[i].forwardAxis = Vector3.forward;
            if (chain[i].upAxis.sqrMagnitude < 1e-6f) chain[i].upAxis = Vector3.up;
        }
    }

    private void Initialize()
    {
        if (_initialized) return;

        if (spineAnchor == null) spineAnchor = transform;
        if (upReference == null) upReference = transform;

        if (chain == null) chain = Array.Empty<BoneDrive>();

        for (int i = 0; i < chain.Length; i++)
        {
            var b = chain[i];
            if (b == null || b.bone == null) continue;
            b.bindLocalRot = b.bone.localRotation;
        }

        // initial smoothed dir = anchor forward
        Vector3 up = useWorldUp ? Vector3.up : upReference.up;
        _smoothedDirWorld = yawOnly
            ? Vector3.ProjectOnPlane(spineAnchor.forward, up).normalized
            : spineAnchor.forward.normalized;

        if (_smoothedDirWorld.sqrMagnitude < 1e-6f) _smoothedDirWorld = transform.forward;

        _initialized = true;
    }

    private void LateUpdate()
    {
        if (!_initialized) Initialize();
        if (headIKGuide == null || spineAnchor == null || chain == null || chain.Length == 0) return;

        Vector3 upWorld = useWorldUp ? Vector3.up : upReference.up;

        Vector3 intentWorld = headIKGuide.position - spineAnchor.position;
        if (intentWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredDirWorld = intentWorld.normalized;

        if (yawOnly)
        {
            desiredDirWorld = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
            if (desiredDirWorld.sqrMagnitude < 1e-6f) return;
            desiredDirWorld.Normalize();

            Vector3 anchorFwd = Vector3.ProjectOnPlane(spineAnchor.forward, upWorld).normalized;
            if (anchorFwd.sqrMagnitude < 1e-6f) anchorFwd = desiredDirWorld;

            float yawDelta = Vector3.SignedAngle(anchorFwd, desiredDirWorld, upWorld);

            if (Mathf.Abs(yawDelta) < deadZoneDeg) yawDelta = 0f;
            yawDelta = Mathf.Clamp(yawDelta, -maxTotalAngleDeg, maxTotalAngleDeg);

            desiredDirWorld = Quaternion.AngleAxis(yawDelta, upWorld) * anchorFwd;
        }
        else
        {
            // Full 3D clamp
            float ang = Vector3.Angle(spineAnchor.forward, desiredDirWorld);
            if (ang < deadZoneDeg) desiredDirWorld = spineAnchor.forward;
            if (ang > maxTotalAngleDeg)
            {
                // clamp by rotating anchor forward toward desiredDirWorld by maxTotalAngleDeg
                Vector3 axis = Vector3.Cross(spineAnchor.forward, desiredDirWorld);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    axis.Normalize();
                    desiredDirWorld = Quaternion.AngleAxis(maxTotalAngleDeg, axis) * spineAnchor.forward;
                }
            }
            desiredDirWorld.Normalize();
        }

        // Smooth the direction (spherical exponential smoothing)
        float t = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
        _smoothedDirWorld = Vector3.Slerp(_smoothedDirWorld, desiredDirWorld, t);
        if (_smoothedDirWorld.sqrMagnitude < 1e-6f) _smoothedDirWorld = desiredDirWorld;
        _smoothedDirWorld.Normalize();

        // Apply to each bone
        for (int i = 0; i < chain.Length; i++)
        {
            var bd = chain[i];
            if (bd == null || bd.bone == null) continue;
            Transform bone = bd.bone;
            Transform parent = bone.parent;
            if (parent == null) continue;

            // Ensure axes are valid
            Vector3 fAxis = bd.forwardAxis.normalized;
            Vector3 uAxis = bd.upAxis.normalized;
            if (fAxis.sqrMagnitude < 1e-6f) fAxis = Vector3.forward;
            if (uAxis.sqrMagnitude < 1e-6f) uAxis = Vector3.up;

            // Prevent colinear axes
            if (Vector3.Cross(fAxis, uAxis).sqrMagnitude < 1e-6f)
            {
                // fallback: pick an arbitrary up that's not colinear
                uAxis = Mathf.Abs(Vector3.Dot(fAxis, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
            }

            // Desired dir in parent space
            Vector3 desiredDirParent = parent.InverseTransformDirection(_smoothedDirWorld);
            if (yawOnly)
            {
                // project onto the plane defined by upWorld, but in parent space
                Vector3 upParentPlane = parent.InverseTransformDirection(upWorld).normalized;
                desiredDirParent = Vector3.ProjectOnPlane(desiredDirParent, upParentPlane);
            }
            if (desiredDirParent.sqrMagnitude < 1e-6f) continue;
            desiredDirParent.Normalize();

            // Build bind basis in parent space using bindLocalRot and custom axes
            Quaternion bindLocal = bd.bindLocalRot;
            Vector3 bindFwdParent = (bindLocal * fAxis).normalized;
            Vector3 bindUpParent = (bindLocal * uAxis);

            // Orthonormalize bindUp relative to bindFwd
            Vector3 bindUpOrtho = Vector3.ProjectOnPlane(bindUpParent, bindFwdParent);
            if (bindUpOrtho.sqrMagnitude < 1e-6f)
            {
                // fallback: use parent's up
                bindUpOrtho = Vector3.ProjectOnPlane(Vector3.up, bindFwdParent);
                if (bindUpOrtho.sqrMagnitude < 1e-6f) bindUpOrtho = Vector3.forward;
            }
            bindUpOrtho.Normalize();

            Quaternion bindBasis = Quaternion.LookRotation(bindFwdParent, bindUpOrtho);

            // Target up reference: try to preserve bind roll by using bindUpParent projected onto desired forward
            Vector3 targetUpOrtho = Vector3.ProjectOnPlane(bindUpParent, desiredDirParent);
            if (targetUpOrtho.sqrMagnitude < 1e-6f)
            {
                // fallback to plane up
                Vector3 upParent = parent.InverseTransformDirection(upWorld).normalized;
                targetUpOrtho = Vector3.ProjectOnPlane(upParent, desiredDirParent);
                if (targetUpOrtho.sqrMagnitude < 1e-6f) targetUpOrtho = bindUpOrtho;
            }
            targetUpOrtho.Normalize();

            Quaternion targetBasis = Quaternion.LookRotation(desiredDirParent, targetUpOrtho);

            // Delta from bind basis to target basis (parent space)
            Quaternion delta = targetBasis * Quaternion.Inverse(bindBasis);

            // Clamp delta angle for this bone
            float deltaAngle = Quaternion.Angle(Quaternion.identity, delta);
            if (deltaAngle > 1e-4f)
            {
                float maxA = Mathf.Max(0f, bd.maxAngleDeg);
                float clamped = Mathf.Min(deltaAngle, maxA);
                float clampT = clamped / deltaAngle;
                delta = Quaternion.Slerp(Quaternion.identity, delta, clampT);
            }

            // Weight + per-bone sharpness (smooth the delta application)
            float wt = Mathf.Clamp01(bd.weight);
            float sharp = followSharpness * Mathf.Max(0.1f, bd.sharpnessMul);
            float boneT = 1f - Mathf.Exp(-sharp * Time.deltaTime);

            // target local rot = (delta * bindLocal) ; then blend toward it
            Quaternion targetLocal = delta * bindLocal;
            bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, boneT * wt);
        }

        if (drawDebug)
        {
            Debug.DrawLine(spineAnchor.position, headIKGuide.position, Color.white);
            Debug.DrawRay(spineAnchor.position, _smoothedDirWorld * 0.6f, Color.cyan);
        }
    }
}
