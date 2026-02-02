using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base class for spine pose providers.
/// Providers must NOT write transforms. They only output per-joint deltas relative to bind pose.
/// </summary>
public abstract class SpinePoseProviderBase : MonoBehaviour
{
    [Range(0f, 1f)]
    public float globalWeight = 1f;

    /// <summary>
    /// For joint index, output local-space deltas relative to bind:
    /// - localRotDelta: relative to bindLocalRot (identity = no change)
    /// - localPosDelta: relative to bindLocalPos (zero = no change)
    /// - weight: 0..1 for this joint (will be multiplied by globalWeight)
    /// Return true if provider is active for this joint this frame.
    /// </summary>
    public abstract bool Evaluate(
        int index,
        SpineChainDefinition spine,
        out Quaternion localRotDelta,
        out Vector3 localPosDelta,
        out float weight);
}

/// <summary>
/// Chain damping + propagation (Animation Rigging-like lag).
/// This is the ONLY script that should write bone.localRotation/localPosition for the spine chain.
/// </summary>
[DefaultExecutionOrder(9000)]
[DisallowMultipleComponent]
public class SpineCascadedDampingChain : MonoBehaviour
{
    public enum PropagationDirection
    {
        HipToChest,
        ChestToHip
    }

    [Header("Chain")]
    public SpineChainDefinition spine;

    [Header("Providers (inputs)")]
    [Tooltip("All providers contribute deltas which are blended into a raw target pose each frame.")]
    public List<SpinePoseProviderBase> providers = new();

    [Header("Propagation")]
    public PropagationDirection propagation = PropagationDirection.ChestToHip;

    [Tooltip("0 = each joint damps toward its own raw target; 1 = strongly follows upstream filtered pose (strong propagation).")]
    [Range(0f, 1f)]
    public float propagationBlend = 0.75f;

    [Header("Damping (Half-life)")]
    [Tooltip("Rotation half-life (seconds). Smaller = snappier, larger = more lag/softness.")]
    [Range(0.01f, 2.0f)]
    public float rotationHalfLife = 0.12f;

    [Tooltip("Position half-life (seconds). Usually keep >= rotation or disable position apply.")]
    [Range(0.01f, 2.0f)]
    public float positionHalfLife = 0.25f;

    [Tooltip("Per-joint multiplier curve for rotation half-life. x=normalizedFromHip01.")]
    public AnimationCurve rotHalfLifeMul = AnimationCurve.Linear(0, 1, 1, 1);

    [Tooltip("Per-joint multiplier curve for position half-life. x=normalizedFromHip01.")]
    public AnimationCurve posHalfLifeMul = AnimationCurve.Linear(0, 1, 1, 1);

    [Header("Apply")]
    [Tooltip("If true, apply localPosition damping too. Otherwise keeps bind localPosition.")]
    public bool applyLocalPosition = false;

    [Tooltip("If true, scales final influence by SpineChainDefinition.Joint.weight (recommended for bend gradient).")]
    public bool applyDefinitionWeight = true;

    [Tooltip("If false, total provider weight is clamped to 1. (recommended)")]
    public bool allowOverdriveWeight = false;

    [Header("Debug")]
    public bool drawDebug = false;

    private Quaternion[] _bindLocalRot;
    private Vector3[] _bindLocalPos;

    private Quaternion[] _filteredLocalRot;
    private Vector3[] _filteredLocalPos;

    private bool _initialized;

    private void Awake() => InitializeIfNeeded(force: true);
    private void OnEnable() => InitializeIfNeeded(force: false);
    private void OnValidate() => _initialized = false;

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
        if (spine == null || spine.Count == 0) return;

        spine.CaptureBindLocalRotations(force: false);
        EnsureArrays();

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

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        int n = spine.Count;
        if (n <= 0) return;

        // 1) Build raw target pose for each joint from providers (relative to bind)
        Quaternion[] rawTargetLocalRot = new Quaternion[n];
        Vector3[] rawTargetLocalPos = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            var joint = spine.GetJoint(i);
            if (joint == null || joint.bone == null)
            {
                rawTargetLocalRot[i] = Quaternion.identity;
                rawTargetLocalPos[i] = Vector3.zero;
                continue;
            }

            Quaternion bindRot = _bindLocalRot[i];
            Vector3 bindPos = _bindLocalPos[i];

            Vector3 rotVecSum = Vector3.zero;
            float rotWSum = 0f;

            Vector3 posSum = Vector3.zero;
            float posWSum = 0f;

            for (int p = 0; p < providers.Count; p++)
            {
                var prov = providers[p];
                if (prov == null || !prov.isActiveAndEnabled) continue;

                if (prov.Evaluate(i, spine, out Quaternion dRot, out Vector3 dPos, out float w))
                {
                    w = Mathf.Clamp01(w) * Mathf.Clamp01(prov.globalWeight);
                    if (w <= 0f) continue;

                    rotVecSum += ToAxisAngleVector(dRot) * w;
                    rotWSum += w;

                    posSum += dPos * w;
                    posWSum += w;
                }
            }

            // IMPORTANT:
            // We want provider weights (joint weight * provider.globalWeight) to scale INFLUENCE, not just pick a winner.
            // So we compute a weighted *mean* direction, then scale its magnitude by total weight (clamped if desired).
            float rotWTotal = allowOverdriveWeight ? rotWSum : Mathf.Clamp01(rotWSum);
            float posWTotal = allowOverdriveWeight ? posWSum : Mathf.Clamp01(posWSum);

            Vector3 rotVec = (rotWSum > 1e-6f) ? ((rotVecSum / rotWSum) * rotWTotal) : Vector3.zero;
            Vector3 posDelta = (posWSum > 1e-6f) ? ((posSum / posWSum) * posWTotal) : Vector3.zero;

            Quaternion deltaRot = (rotVec.sqrMagnitude > 1e-12f) ? FromAxisAngleVector(rotVec) : Quaternion.identity;
            Vector3 deltaPos = posDelta;

            Quaternion rawRot = deltaRot * bindRot;
            Vector3 rawPos = bindPos + deltaPos;

            if (applyDefinitionWeight)
            {
                float w = Mathf.Clamp01(joint.weight);
                rawRot = Quaternion.Slerp(bindRot, rawRot, w);
                rawPos = Vector3.Lerp(bindPos, rawPos, w);
            }

            rawTargetLocalRot[i] = rawRot;
            rawTargetLocalPos[i] = rawPos;
        }

        // 2) Cascaded propagation + damping
        bool chestToHip = (propagation == PropagationDirection.ChestToHip);
        int start = chestToHip ? (n - 1) : 0;
        int end = chestToHip ? -1 : n;
        int step = chestToHip ? -1 : 1;

        for (int i = start; i != end; i += step)
        {
            var joint = spine.GetJoint(i);
            if (joint == null || joint.bone == null) continue;

            float hip01 = spine.GetNormalizedFromHip01(i);

            int upstreamIndex = chestToHip ? (i + 1) : (i - 1);
            bool hasUpstream = upstreamIndex >= 0 && upstreamIndex < n && spine.GetBoneHipToChest(upstreamIndex) != null;

            Quaternion upstreamRot = hasUpstream ? _filteredLocalRot[upstreamIndex] : rawTargetLocalRot[i];
            Vector3 upstreamPos = hasUpstream ? _filteredLocalPos[upstreamIndex] : rawTargetLocalPos[i];

            Quaternion stageRotTarget = Quaternion.Slerp(rawTargetLocalRot[i], upstreamRot, propagationBlend);
            Vector3 stagePosTarget = Vector3.Lerp(rawTargetLocalPos[i], upstreamPos, propagationBlend);

            float rotHL = Mathf.Max(0.001f, rotationHalfLife) * Mathf.Max(0.01f, rotHalfLifeMul.Evaluate(hip01));
            float posHL = Mathf.Max(0.001f, positionHalfLife) * Mathf.Max(0.01f, posHalfLifeMul.Evaluate(hip01));

            float rotAlpha = HalfLifeToAlpha(rotHL, dt);
            float posAlpha = HalfLifeToAlpha(posHL, dt);

            _filteredLocalRot[i] = Quaternion.Slerp(_filteredLocalRot[i], stageRotTarget, rotAlpha);
            _filteredLocalPos[i] = Vector3.Lerp(_filteredLocalPos[i], stagePosTarget, posAlpha);

            // 3) Apply (single-writer)
            joint.bone.localRotation = _filteredLocalRot[i];

            if (applyLocalPosition)
                joint.bone.localPosition = _filteredLocalPos[i];
            else
                joint.bone.localPosition = _bindLocalPos[i];
        }
    }

    private static float HalfLifeToAlpha(float halfLife, float dt)
    {
        // alpha = 1 - exp(-ln(2) * dt / halfLife)
        return 1f - Mathf.Exp(-0.69314718056f * dt / Mathf.Max(1e-6f, halfLife));
    }

    // Quaternion delta -> axis-angle vector (radians * axis)
    private static Vector3 ToAxisAngleVector(Quaternion q)
    {
        if (q.w > 1f) q.Normalize();
        q.ToAngleAxis(out float angleDeg, out Vector3 axis);

        if (axis.sqrMagnitude < 1e-12f) return Vector3.zero;

        // map angle to [-180,180] for stability
        if (angleDeg > 180f) angleDeg -= 360f;

        float angleRad = angleDeg * Mathf.Deg2Rad;
        axis.Normalize();
        return axis * angleRad;
    }

    // axis-angle vector -> Quaternion delta
    private static Quaternion FromAxisAngleVector(Vector3 v)
    {
        float angleRad = v.magnitude;
        if (angleRad < 1e-8f) return Quaternion.identity;

        Vector3 axis = v / angleRad;
        float angleDeg = angleRad * Mathf.Rad2Deg;
        return Quaternion.AngleAxis(angleDeg, axis);
    }
}
