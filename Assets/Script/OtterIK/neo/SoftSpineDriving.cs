using System;
using UnityEngine;

/// <summary>
/// softspinedriving
/// A bind-pose-safe soft spine driver designed for "head looks first, body follows".
///
/// Features:
/// - Per-bone configurable ForwardAxis and UpAxis (in bone local space).
/// - Stable aiming toward head intent guide (yaw-only or full 3D).
/// - Per-bone weight, max angle, sharpness multiplier.
/// - Turn-roll support:
///     A) Dynamic roll from yaw rate (deg/s)
///     B) Intent roll from desired yaw (works even at 0 speed) with a minimum roll intensity.
/// - Roll is applied around each bone's forward axis (defined by your ForwardAxis).
///
/// Recommended chain order:
/// spine1 -> spine2 -> spine3 -> subneck -> neck (exclude head; head can be controlled by your head IK).
/// </summary>
[DisallowMultipleComponent]
public class SoftSpineDriving : MonoBehaviour
{
    [Serializable]
    public class BoneDrive
    {
        [Header("Bone")]
        public Transform bone;

        [Header("Local Axes (Bone Space)")]
        [Tooltip("Bone local-space forward axis, e.g. (0,0,1) for +Z, (1,0,0) for +X, or negatives.")]
        public Vector3 forwardAxis = Vector3.forward;

        [Tooltip("Bone local-space up axis, e.g. (0,1,0) for +Y. Must not be colinear with forwardAxis.")]
        public Vector3 upAxis = Vector3.up;

        [Header("Aim (Bend)")]
        [Range(0f, 1f)]
        public float weight = 0.2f;

        [Tooltip("Max aim/bend angle (deg) for this bone.")]
        [Range(0f, 90f)]
        public float maxAngleDeg = 25f;

        [Tooltip("Per-bone follow multiplier (1 = same as global followSharpness).")]
        [Range(0.1f, 5f)]
        public float sharpnessMul = 1f;

        [Header("Turn Roll")]
        [Tooltip("How much of the total roll this bone gets (0..1).")]
        [Range(0f, 1f)]
        public float rollWeight = 0.2f;

        [Tooltip("Max roll (deg) for this bone.")]
        [Range(0f, 45f)]
        public float maxRollDeg = 12f;

        [NonSerialized] public Quaternion bindLocalRot;
    }

    [Header("References")]
    [Tooltip("Reference origin + reference forward for spine intent. Typically otter_root (body center).")]
    public Transform spineAnchor;

    [Tooltip("Your head intent guide transform (the one your head IK is following).")]
    public Transform headIKGuide;

    [Tooltip("Up reference for yaw plane & yaw-rate. Typically otter_root.")]
    public Transform upReference;

    [Tooltip("If true, use Vector3.up as up axis instead of upReference.up.")]
    public bool useWorldUp = false;

    [Header("Aim Mode")]
    [Tooltip("Yaw-only is the most stable for top-down swimming (recommended).")]
    public bool yawOnly = true;

    [Header("Global Follow (Aim/Bend)")]
    [Range(0.01f, 30f)]
    public float followSharpness = 10f;

    [Tooltip("Small jitter dead zone (deg).")]
    [Range(0f, 10f)]
    public float deadZoneDeg = 0.25f;

    [Tooltip("Clamp total desired angle. If yawOnly = true, this clamps yaw intent (deg).")]
    [Range(0f, 180f)]
    public float maxTotalAngleDeg = 80f;

    [Header("Bones (spine1..neck, exclude head)")]
    public BoneDrive[] chain;

    [Header("Turn Roll")]
    public bool enableTurnRoll = true;

    [Tooltip("Max yaw rate that maps to full roll (deg/s).")]
    public float maxYawRateForFullRoll = 220f;

    [Tooltip("Max total roll amount (deg) before per-bone distribution. Final per-bone max also applies.")]
    public float maxTotalRollDeg = 16f;

    [Tooltip("Roll smoothing sharpness (higher = snappier).")]
    public float rollSharpness = 12f;

    [Header("Intent Roll (works even at 0 speed)")]
    [Tooltip("Minimum roll (deg) when there is turning intent, even if yawRate is near zero.")]
    public float minIntentRollDeg = 2.5f;

    [Tooltip("How much roll comes from intent yaw delta (deg roll per 1 deg yaw intent).")]
    public float intentYawToRoll = 0.12f;

    [Tooltip("Intent yaw dead zone (deg). Below this, no intent roll.")]
    public float intentYawDeadZoneDeg = 2.0f;

    [Tooltip("Intent yaw (deg) at which the minimum roll is fully allowed (smooth ramp).")]
    public float intentYawForFullMinRollDeg = 25f;

    [Tooltip("If your roll direction feels reversed, toggle this.")]
    public bool invertRollSign = false;

    [Header("Debug")]
    public bool drawDebug = true;

    // State
    private bool _initialized;
    private Vector3 _smoothedDirWorld;

    private Vector3 _prevAnchorFwdPlane;
    private float _smoothedRollDeg;
    private float _rollVel;

    private void Reset()
    {
        spineAnchor = transform;
        upReference = transform;
    }

    private void Awake() => Initialize();

    private void OnValidate()
    {
        if (chain == null) return;
        for (int i = 0; i < chain.Length; i++)
        {
            var b = chain[i];
            if (b == null) continue;
            if (b.forwardAxis.sqrMagnitude < 1e-6f) b.forwardAxis = Vector3.forward;
            if (b.upAxis.sqrMagnitude < 1e-6f) b.upAxis = Vector3.up;

            // Keep intentYawForFullMinRollDeg sane
            intentYawForFullMinRollDeg = Mathf.Max(intentYawDeadZoneDeg + 0.001f, intentYawForFullMinRollDeg);
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

        Vector3 up = useWorldUp ? Vector3.up : upReference.up;

        _smoothedDirWorld = yawOnly
            ? Vector3.ProjectOnPlane(spineAnchor.forward, up).normalized
            : spineAnchor.forward.normalized;

        if (_smoothedDirWorld.sqrMagnitude < 1e-6f) _smoothedDirWorld = transform.forward;
        _smoothedDirWorld.Normalize();

        _prevAnchorFwdPlane = Vector3.ProjectOnPlane(spineAnchor.forward, up).normalized;
        _smoothedRollDeg = 0f;
        _rollVel = 0f;

        _initialized = true;
    }

    private void LateUpdate()
    {
        if (!_initialized) Initialize();
        if (headIKGuide == null || spineAnchor == null || chain == null || chain.Length == 0) return;

        Vector3 upWorld = useWorldUp ? Vector3.up : upReference.up;

        // ----- 1) Compute desired direction from anchor -> head guide -----
        Vector3 intentWorld = headIKGuide.position - spineAnchor.position;
        if (intentWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredDirWorld = intentWorld.normalized;

        // We'll compute "desiredYawDeg" for intent-roll (yawOnly uses this naturally).
        float desiredYawDeg = 0f;

        if (yawOnly)
        {
            Vector3 desiredPlane = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
            if (desiredPlane.sqrMagnitude < 1e-6f) return;
            desiredPlane.Normalize();

            Vector3 anchorFwd = Vector3.ProjectOnPlane(spineAnchor.forward, upWorld);
            if (anchorFwd.sqrMagnitude < 1e-6f) anchorFwd = desiredPlane;
            else anchorFwd.Normalize();

            desiredYawDeg = Vector3.SignedAngle(anchorFwd, desiredPlane, upWorld);
            if (Mathf.Abs(desiredYawDeg) < deadZoneDeg) desiredYawDeg = 0f;
            desiredYawDeg = Mathf.Clamp(desiredYawDeg, -maxTotalAngleDeg, maxTotalAngleDeg);

            desiredDirWorld = (Quaternion.AngleAxis(desiredYawDeg, upWorld) * anchorFwd).normalized;
        }
        else
        {
            // Full 3D clamp (still stable due to per-bone upAxis roll handling)
            float ang = Vector3.Angle(spineAnchor.forward, desiredDirWorld);
            if (ang < deadZoneDeg)
            {
                desiredDirWorld = spineAnchor.forward.normalized;
            }
            else if (ang > maxTotalAngleDeg)
            {
                Vector3 axis = Vector3.Cross(spineAnchor.forward, desiredDirWorld);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    axis.Normalize();
                    desiredDirWorld = (Quaternion.AngleAxis(maxTotalAngleDeg, axis) * spineAnchor.forward).normalized;
                }
            }

            // For intent-roll when not yawOnly: approximate desiredYawDeg from plane projection.
            Vector3 anchorFwdPlane = Vector3.ProjectOnPlane(spineAnchor.forward, upWorld);
            Vector3 desiredPlane = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
            if (anchorFwdPlane.sqrMagnitude > 1e-6f && desiredPlane.sqrMagnitude > 1e-6f)
            {
                anchorFwdPlane.Normalize();
                desiredPlane.Normalize();
                desiredYawDeg = Vector3.SignedAngle(anchorFwdPlane, desiredPlane, upWorld);
                if (Mathf.Abs(desiredYawDeg) < deadZoneDeg) desiredYawDeg = 0f;
                desiredYawDeg = Mathf.Clamp(desiredYawDeg, -maxTotalAngleDeg, maxTotalAngleDeg);
            }
        }

        // Smooth direction (exponential slerp)
        float tDir = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
        _smoothedDirWorld = Vector3.Slerp(_smoothedDirWorld, desiredDirWorld, tDir);
        if (_smoothedDirWorld.sqrMagnitude < 1e-6f) _smoothedDirWorld = desiredDirWorld;
        _smoothedDirWorld.Normalize();

        // ----- 2) Compute roll (optional) -----
        float desiredRollDeg = 0f;
        if (enableTurnRoll)
        {
            float dt = Mathf.Max(1e-4f, Time.deltaTime);

            // A) dynamic roll from yawRate (deg/s), using anchor forward on plane
            Vector3 anchorFwdPlane = Vector3.ProjectOnPlane(spineAnchor.forward, upWorld);
            if (anchorFwdPlane.sqrMagnitude > 1e-6f) anchorFwdPlane.Normalize();

            float yawDeltaDeg = 0f;
            if (_prevAnchorFwdPlane.sqrMagnitude > 1e-6f && anchorFwdPlane.sqrMagnitude > 1e-6f)
                yawDeltaDeg = Vector3.SignedAngle(_prevAnchorFwdPlane, anchorFwdPlane, upWorld);

            _prevAnchorFwdPlane = anchorFwdPlane;

            float yawRateDegPerSec = yawDeltaDeg / dt;
            float roll01_rate = Mathf.Clamp(yawRateDegPerSec / Mathf.Max(1e-3f, maxYawRateForFullRoll), -1f, 1f);
            float rollFromRateDeg = roll01_rate * maxTotalRollDeg;

            // B) intent roll from desired yaw (works even at 0 speed)
            float yawIntentDeg = desiredYawDeg;
            float absIntent = Mathf.Abs(yawIntentDeg);

            float rollFromIntentDeg = 0f;
            if (absIntent > intentYawDeadZoneDeg)
            {
                float sign = Mathf.Sign(yawIntentDeg);

                float mapped = absIntent * intentYawToRoll;

                float denom = Mathf.Max(1e-3f, intentYawForFullMinRollDeg - intentYawDeadZoneDeg);
                float tMin = Mathf.Clamp01((absIntent - intentYawDeadZoneDeg) / denom);
                float minRoll = Mathf.Lerp(0f, minIntentRollDeg, tMin);

                float absRoll = Mathf.Max(mapped, minRoll);
                rollFromIntentDeg = sign * absRoll;
            }

            // Combine: pick the stronger one (more stable than pure add), but still allow intent when rate is tiny
            desiredRollDeg = (Mathf.Abs(rollFromRateDeg) > Mathf.Abs(rollFromIntentDeg)) ? rollFromRateDeg : rollFromIntentDeg;

            if (invertRollSign) desiredRollDeg = -desiredRollDeg;

            desiredRollDeg = Mathf.Clamp(desiredRollDeg, -maxTotalRollDeg, maxTotalRollDeg);

            float rollSmoothTime = Mathf.Max(0.001f, 1f / Mathf.Max(0.01f, rollSharpness));
            _smoothedRollDeg = Mathf.SmoothDampAngle(_smoothedRollDeg, desiredRollDeg, ref _rollVel, rollSmoothTime);
        }
        else
        {
            // Smooth back to zero if disabled
            float rollSmoothTime = Mathf.Max(0.001f, 1f / Mathf.Max(0.01f, rollSharpness));
            _smoothedRollDeg = Mathf.SmoothDampAngle(_smoothedRollDeg, 0f, ref _rollVel, rollSmoothTime);
        }

        // ----- 3) Apply to each bone (parent-space basis method, bind safe) -----
        for (int i = 0; i < chain.Length; i++)
        {
            var bd = chain[i];
            if (bd == null || bd.bone == null) continue;

            Transform bone = bd.bone;
            Transform parent = bone.parent;
            if (parent == null) continue;

            // Axes sanity
            Vector3 fAxis = bd.forwardAxis;
            Vector3 uAxis = bd.upAxis;

            if (fAxis.sqrMagnitude < 1e-6f) fAxis = Vector3.forward;
            if (uAxis.sqrMagnitude < 1e-6f) uAxis = Vector3.up;

            fAxis.Normalize();
            uAxis.Normalize();

            // Prevent colinear axes
            if (Vector3.Cross(fAxis, uAxis).sqrMagnitude < 1e-6f)
                uAxis = (Mathf.Abs(Vector3.Dot(fAxis, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

            Quaternion bindLocal = bd.bindLocalRot;

            // Desired direction in parent space
            Vector3 desiredDirParent = parent.InverseTransformDirection(_smoothedDirWorld);
            if (yawOnly)
            {
                Vector3 upParentPlane = parent.InverseTransformDirection(upWorld).normalized;
                desiredDirParent = Vector3.ProjectOnPlane(desiredDirParent, upParentPlane);
            }
            if (desiredDirParent.sqrMagnitude < 1e-6f) continue;
            desiredDirParent.Normalize();

            // Bind forward/up in parent space (using bind local rot and custom axes)
            Vector3 bindFwdParent = (bindLocal * fAxis).normalized;

            Vector3 bindUpParentRaw = (bindLocal * uAxis);
            Vector3 bindUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, bindFwdParent);
            if (bindUpParent.sqrMagnitude < 1e-6f)
            {
                // fallback: use parent up projected
                bindUpParent = Vector3.ProjectOnPlane(Vector3.up, bindFwdParent);
                if (bindUpParent.sqrMagnitude < 1e-6f) bindUpParent = Vector3.forward;
            }
            bindUpParent.Normalize();

            Quaternion bindBasis = Quaternion.LookRotation(bindFwdParent, bindUpParent);

            // Target up: preserve bind roll by reusing bindUpParentRaw projected onto desired forward
            Vector3 targetUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, desiredDirParent);
            if (targetUpParent.sqrMagnitude < 1e-6f)
            {
                Vector3 upParent = parent.InverseTransformDirection(upWorld).normalized;
                targetUpParent = Vector3.ProjectOnPlane(upParent, desiredDirParent);
                if (targetUpParent.sqrMagnitude < 1e-6f) targetUpParent = bindUpParent;
            }
            targetUpParent.Normalize();

            Quaternion targetBasis = Quaternion.LookRotation(desiredDirParent, targetUpParent);

            // Aim delta in parent space
            Quaternion deltaAim = targetBasis * Quaternion.Inverse(bindBasis);

            // Clamp aim delta angle per bone
            float aimAngle = Quaternion.Angle(Quaternion.identity, deltaAim);
            if (aimAngle > 1e-4f)
            {
                float maxA = Mathf.Max(0f, bd.maxAngleDeg);
                float clamped = Mathf.Min(aimAngle, maxA);
                float clampT = clamped / aimAngle;
                deltaAim = Quaternion.Slerp(Quaternion.identity, deltaAim, clampT);
            }

            // Roll delta (around bone forward axis in parent space)
            float boneRoll = _smoothedRollDeg * Mathf.Clamp01(bd.rollWeight);
            boneRoll = Mathf.Clamp(boneRoll, -bd.maxRollDeg, bd.maxRollDeg);

            Vector3 rollAxisParent = (bindLocal * fAxis).normalized; // parent's local axis
            if (rollAxisParent.sqrMagnitude < 1e-6f) rollAxisParent = Vector3.forward;

            Quaternion deltaRoll = Quaternion.AngleAxis(boneRoll, rollAxisParent);

            // Compose target local rotation: roll after aim (feel free to swap if you prefer)
            Quaternion targetLocal = (deltaRoll * deltaAim) * bindLocal;

            // Smooth + weight
            float wt = Mathf.Clamp01(bd.weight);
            float sharp = followSharpness * Mathf.Max(0.1f, bd.sharpnessMul);
            float boneT = 1f - Mathf.Exp(-sharp * Time.deltaTime);

            bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, boneT * wt);
        }

        // ----- Debug -----
        if (drawDebug)
        {
            Debug.DrawLine(spineAnchor.position, headIKGuide.position, Color.white);
            Debug.DrawRay(spineAnchor.position, _smoothedDirWorld * 0.6f, Color.cyan);

            if (enableTurnRoll)
            {
                // Visualize roll magnitude
                float r = Mathf.Abs(_smoothedRollDeg) / Mathf.Max(1e-3f, maxTotalRollDeg);
                Debug.DrawRay(spineAnchor.position, upWorld * (0.3f + 0.4f * r), Color.magenta);
            }
        }
    }
}
