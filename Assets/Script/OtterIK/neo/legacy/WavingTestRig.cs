using UnityEngine;

/// <summary>
/// wavingtestrig
/// Cascaded (chain) damping for spine-like joints, similar "delay/lag" feel to Animation Rigging.
/// 
/// Two input modes:
/// 1) CopyCurrentAsRawTarget:
///    - Reads current local pose as "raw target" each LateUpdate (good to lag another system's output).
///    - IMPORTANT: This script must execute AFTER the system you want to lag.
/// 2) AimToGuide:
///    - Generates a raw target pose by aiming the chain toward a guide (like SoftSpineDriving's bend),
///      then applies chain damping on top.
/// 
/// This script writes transforms directly for quick visual testing.
/// Once you like the feel, we can route its output into your mixer/applier.
/// </summary>
[DefaultExecutionOrder(9000)]
[DisallowMultipleComponent]
public class WavingTestRig : MonoBehaviour
{
    public enum InputMode
    {
        CopyCurrentAsRawTarget,
        AimToGuide
    }

    public enum PropagationDirection
    {
        HipToChest,
        ChestToHip
    }

    [Header("Chain")]
    public SpineChainDefinition spine;

    [Header("Mode")]
    public InputMode inputMode = InputMode.AimToGuide;

    [Header("AimToGuide inputs (used only in AimToGuide mode)")]
    public Transform spineAnchor;
    public Transform headIKGuide;
    public Transform upReference;
    public bool useWorldUp = false;
    public bool yawOnly = true;

    [Header("Propagation")]
    public PropagationDirection propagation = PropagationDirection.ChestToHip;

    [Tooltip("0 = each joint damps toward its own raw target; 1 = each joint mostly follows upstream filtered pose (strong propagation).")]
    [Range(0f, 1f)]
    public float propagationBlend = 0.75f;

    [Header("Damping (Half-life)")]
    [Tooltip("Rotation half-life (seconds). Smaller = snappier, larger = more lag/softness.")]
    [Range(0.01f, 1.5f)]
    public float rotationHalfLife = 0.10f;

    [Tooltip("Position half-life (seconds). Usually keep larger than rotation or disable if you don't want positional lag.")]
    [Range(0.01f, 1.5f)]
    public float positionHalfLife = 0.25f;

    [Tooltip("Per-joint multiplier curve for rotation half-life. x=normalizedFromHip01.")]
    public AnimationCurve rotHalfLifeMul = AnimationCurve.Linear(0, 1, 1, 1);

    [Tooltip("Per-joint multiplier curve for position half-life. x=normalizedFromHip01.")]
    public AnimationCurve posHalfLifeMul = AnimationCurve.Linear(0, 1, 1, 1);

    [Header("Apply options")]
    [Tooltip("If true, apply translation (localPosition) damping too. Otherwise keeps bind localPosition.")]
    public bool applyLocalPosition = false;

    [Tooltip("If true, multiplies the final applied intensity by SpineChainDefinition.Joint.weight (useful when AimToGuide has no per-joint weights).")]
    public bool applyDefinitionWeight = true;

    [Header("AimToGuide constraints")]
    [Range(0f, 180f)]
    public float maxTotalAngleDeg = 80f;

    [Range(0f, 10f)]
    public float deadZoneDeg = 0.25f;

    [Header("Debug")]
    public bool drawDebug = false;

    private Quaternion[] _bindLocalRot;
    private Vector3[] _bindLocalPos;

    private Quaternion[] _filteredLocalRot;
    private Vector3[] _filteredLocalPos;

    private bool _initialized;

    private void Reset()
    {
        spineAnchor = transform;
        upReference = transform;
    }

    private void Awake()
    {
        InitializeIfNeeded(force: true);
    }

    private void OnEnable()
    {
        InitializeIfNeeded(force: false);
    }

    private void OnValidate()
    {
        _initialized = false;
    }

    [ContextMenu("Reset Filtered State To Current")]
    public void ResetFilteredToCurrent()
    {
        if (spine == null || spine.Count == 0) return;
        EnsureArrays();

        for (int i = 0; i < spine.Count; i++)
        {
            var j = spine.GetJoint(i);
            if (j == null || j.bone == null) continue;

            _filteredLocalRot[i] = j.bone.localRotation;
            _filteredLocalPos[i] = j.bone.localPosition;
        }
    }

    private void InitializeIfNeeded(bool force)
    {
        if (_initialized && !force) return;

        if (spine == null || spine.Count == 0)
            return;

        if (spineAnchor == null) spineAnchor = transform;
        if (upReference == null) upReference = transform;

        spine.CaptureBindLocalRotations(force: false);

        EnsureArrays();

        // Capture bind pose arrays + init filtered state
        for (int i = 0; i < spine.Count; i++)
        {
            var j = spine.GetJoint(i);
            if (j == null || j.bone == null) continue;

            _bindLocalRot[i] = spine.GetBindLocalRot(i, captureIfMissing: true);
            _bindLocalPos[i] = j.bone.localPosition;

            _filteredLocalRot[i] = j.bone.localRotation;
            _filteredLocalPos[i] = j.bone.localPosition;
        }

        _initialized = true;
    }

    private void EnsureArrays()
    {
        int n = spine.Count;

        if (_bindLocalRot == null || _bindLocalRot.Length != n) _bindLocalRot = new Quaternion[n];
        if (_bindLocalPos == null || _bindLocalPos.Length != n) _bindLocalPos = new Vector3[n];

        if (_filteredLocalRot == null || _filteredLocalRot.Length != n) _filteredLocalRot = new Quaternion[n];
        if (_filteredLocalPos == null || _filteredLocalPos.Length != n) _filteredLocalPos = new Vector3[n];
    }

    private void LateUpdate()
    {
        InitializeIfNeeded(force: false);
        if (!_initialized) return;

        int n = spine.Count;
        if (n <= 0) return;

        // Build raw target arrays for this frame
        Quaternion[] rawRot = new Quaternion[n];
        Vector3[] rawPos = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            var j = spine.GetJoint(i);
            if (j == null || j.bone == null)
            {
                rawRot[i] = Quaternion.identity;
                rawPos[i] = Vector3.zero;
                continue;
            }

            if (inputMode == InputMode.CopyCurrentAsRawTarget)
            {
                rawRot[i] = j.bone.localRotation;
                rawPos[i] = j.bone.localPosition;
            }
            else
            {
                // AimToGuide raw target
                rawRot[i] = ComputeAimTargetLocal(i);
                rawPos[i] = _bindLocalPos[i]; // default: keep bind position
            }
        }

        // Chain damping update (propagation + per-joint half-life)
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Determine iteration order so "upstream" gets updated first
        int start, end, step;
        bool chestToHip = (propagation == PropagationDirection.ChestToHip);

        if (chestToHip)
        {
            start = n - 1; end = -1; step = -1;
        }
        else
        {
            start = 0; end = n; step = 1;
        }

        for (int i = start; i != end; i += step)
        {
            var j = spine.GetJoint(i);
            if (j == null || j.bone == null) continue;

            float hip01 = spine.GetNormalizedFromHip01(i);

            // Upstream index (the one closer to the source end)
            int upstreamIndex = chestToHip ? (i + 1) : (i - 1);
            bool hasUpstream = upstreamIndex >= 0 && upstreamIndex < n && spine.GetBoneHipToChest(upstreamIndex) != null;

            Quaternion upstreamRot = hasUpstream ? _filteredLocalRot[upstreamIndex] : rawRot[i];
            Vector3 upstreamPos = hasUpstream ? _filteredLocalPos[upstreamIndex] : rawPos[i];

            // Blend raw target with upstream filtered pose (propagation)
            Quaternion stageRotTarget = Quaternion.Slerp(rawRot[i], upstreamRot, propagationBlend);
            Vector3 stagePosTarget = Vector3.Lerp(rawPos[i], upstreamPos, propagationBlend);

            // Per-joint half-life multipliers
            float rotHL = Mathf.Max(0.001f, rotationHalfLife) * Mathf.Max(0.01f, rotHalfLifeMul.Evaluate(hip01));
            float posHL = Mathf.Max(0.001f, positionHalfLife) * Mathf.Max(0.01f, posHalfLifeMul.Evaluate(hip01));

            float rotAlpha = HalfLifeToAlpha(rotHL, dt);
            float posAlpha = HalfLifeToAlpha(posHL, dt);

            _filteredLocalRot[i] = Quaternion.Slerp(_filteredLocalRot[i], stageRotTarget, rotAlpha);
            _filteredLocalPos[i] = Vector3.Lerp(_filteredLocalPos[i], stagePosTarget, posAlpha);

            // Apply to transforms
            Quaternion appliedRot = _filteredLocalRot[i];
            Vector3 appliedPos = _filteredLocalPos[i];

            if (applyDefinitionWeight)
            {
                float w = Mathf.Clamp01(j.weight);
                appliedRot = Quaternion.Slerp(_bindLocalRot[i], appliedRot, w);
                // position weight: usually keep bind unless you really want it
                appliedPos = Vector3.Lerp(_bindLocalPos[i], appliedPos, w);
            }

            j.bone.localRotation = appliedRot;

            if (applyLocalPosition)
                j.bone.localPosition = appliedPos;
            else
                j.bone.localPosition = _bindLocalPos[i];
        }

        if (drawDebug && inputMode == InputMode.AimToGuide && spineAnchor != null && headIKGuide != null)
        {
            Debug.DrawLine(spineAnchor.position, headIKGuide.position, Color.white);
        }
    }

    private Quaternion ComputeAimTargetLocal(int index)
    {
        var j = spine.GetJoint(index);
        Transform bone = j.bone;
        Transform parent = bone.parent;

        if (spineAnchor == null || headIKGuide == null || parent == null)
            return _bindLocalRot[index];

        Vector3 upWorld = useWorldUp ? Vector3.up : upReference.up;
        if (upWorld.sqrMagnitude < 1e-6f) upWorld = Vector3.up;
        upWorld.Normalize();

        // Desired dir in world
        Vector3 desiredDirWorld = (headIKGuide.position - spineAnchor.position);
        if (desiredDirWorld.sqrMagnitude < 1e-6f) return _bindLocalRot[index];
        desiredDirWorld.Normalize();

        // Anchor forward in world (for yaw-only clamp)
        Vector3 anchorFwdWorld = spineAnchor.forward;
        if (yawOnly)
        {
            Vector3 desiredPlane = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
            Vector3 anchorPlane = Vector3.ProjectOnPlane(anchorFwdWorld, upWorld);

            if (desiredPlane.sqrMagnitude < 1e-6f || anchorPlane.sqrMagnitude < 1e-6f)
                return _bindLocalRot[index];

            desiredPlane.Normalize();
            anchorPlane.Normalize();

            float yawDeg = Vector3.SignedAngle(anchorPlane, desiredPlane, upWorld);
            if (Mathf.Abs(yawDeg) < deadZoneDeg) yawDeg = 0f;
            yawDeg = Mathf.Clamp(yawDeg, -maxTotalAngleDeg, maxTotalAngleDeg);

            desiredDirWorld = (Quaternion.AngleAxis(yawDeg, upWorld) * anchorPlane).normalized;
        }
        else
        {
            // full 3D clamp if you want later (optional)
            float ang = Vector3.Angle(anchorFwdWorld, desiredDirWorld);
            if (ang < deadZoneDeg) desiredDirWorld = anchorFwdWorld.normalized;
            else if (ang > maxTotalAngleDeg)
            {
                Vector3 axis = Vector3.Cross(anchorFwdWorld, desiredDirWorld);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    axis.Normalize();
                    desiredDirWorld = (Quaternion.AngleAxis(maxTotalAngleDeg, axis) * anchorFwdWorld).normalized;
                }
            }
        }

        // Convert desired direction into parent space
        Vector3 desiredDirParent = parent.InverseTransformDirection(desiredDirWorld);
        if (yawOnly)
        {
            Vector3 upParent = parent.InverseTransformDirection(upWorld);
            desiredDirParent = Vector3.ProjectOnPlane(desiredDirParent, upParent);
        }
        if (desiredDirParent.sqrMagnitude < 1e-6f) return _bindLocalRot[index];
        desiredDirParent.Normalize();

        // Build bind basis in parent space using custom axes
        Quaternion bindLocal = _bindLocalRot[index];

        Vector3 fAxis = j.forwardAxis.sqrMagnitude < 1e-6f ? Vector3.forward : j.forwardAxis.normalized;
        Vector3 uAxis = j.upAxis.sqrMagnitude < 1e-6f ? Vector3.up : j.upAxis.normalized;

        if (Vector3.Cross(fAxis, uAxis).sqrMagnitude < 1e-6f)
            uAxis = (Mathf.Abs(Vector3.Dot(fAxis, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;

        Vector3 bindFwdParent = (bindLocal * fAxis).normalized;
        Vector3 bindUpParentRaw = (bindLocal * uAxis);

        Vector3 bindUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, bindFwdParent);
        if (bindUpParent.sqrMagnitude < 1e-6f)
            bindUpParent = Vector3.ProjectOnPlane(Vector3.up, bindFwdParent);
        if (bindUpParent.sqrMagnitude < 1e-6f) bindUpParent = Vector3.forward;
        bindUpParent.Normalize();

        Quaternion bindBasis = Quaternion.LookRotation(bindFwdParent, bindUpParent);

        // Target basis
        Vector3 targetUpParent = Vector3.ProjectOnPlane(bindUpParentRaw, desiredDirParent);
        if (targetUpParent.sqrMagnitude < 1e-6f) targetUpParent = bindUpParent;
        targetUpParent.Normalize();

        Quaternion targetBasis = Quaternion.LookRotation(desiredDirParent, targetUpParent);

        Quaternion deltaAim = targetBasis * Quaternion.Inverse(bindBasis);

        // Per-joint clamp (maxAngleDeg)
        float aimAngle = Quaternion.Angle(Quaternion.identity, deltaAim);
        float maxA = Mathf.Max(0f, j.maxAngleDeg);
        if (aimAngle > 1e-4f && maxA < aimAngle)
        {
            float t = maxA / aimAngle;
            deltaAim = Quaternion.Slerp(Quaternion.identity, deltaAim, t);
        }

        return deltaAim * bindLocal;
    }

    private static float HalfLifeToAlpha(float halfLife, float dt)
    {
        // alpha = 1 - exp(-ln(2) * dt / halfLife)
        return 1f - Mathf.Exp(-0.69314718056f * dt / Mathf.Max(1e-6f, halfLife));
    }
}
